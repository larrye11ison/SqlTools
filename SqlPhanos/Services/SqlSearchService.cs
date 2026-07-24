using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using SqlPhanos.ViewModels;

namespace SqlPhanos.Services;

public class SqlSearchService
{
    private const string GetDBsQuery = @"
        SELECT db.NAME AS db_name
        FROM master.sys.databases db
        WHERE db.NAME NOT IN ('master', 'msdb', 'tempdb', 'model')
        ORDER BY db.NAME";

    private const string ObjectsQuery = @"
        DECLARE @objectNameSearch VARCHAR(max)
            ,@objectSchemaSearch VARCHAR(max)
            ,@objectDefinitionSearch VARCHAR(max);

        SET @objectNameSearch = '%' + ltrim(rtrim(isnull(@objectNameSearchParam______, ''))) + '%';
        SET @objectSchemaSearch = '%' + ltrim(rtrim(isnull(@objectSchemaSearchParam______, ''))) + '%';
        SET @objectDefinitionSearch = '%' + ltrim(rtrim(isnull(@objectDefinitionSearchParam______, ''))) + '%';

        WITH cols
        AS (
            SELECT count(*) as MatchingColumnCount
                ,c.object_id
            FROM sys.columns c
            WHERE
                c.NAME LIKE @objectDefinitionSearch
                AND @objectDefinitionSearch != '%%'
            GROUP BY c.object_id
            )
        SELECT @@SERVERNAME AS server_name
            ,cast(db_name() AS SYSNAME) AS db_name
            ,ao.type_desc
            ,ao.object_id
            ,sch.NAME AS schema_name
            ,ao.NAME AS object_name
            ,sp.NAME AS parent_object_schema_name
            ,aop.NAME AS parent_object_name
            ,ao.create_date
            ,ao.modify_date
            ,isnull(cast(objectproperty(ao.object_id, 'IsEncrypted') AS BIT), 0) AS is_encrypted
        FROM sys.all_objects ao
        LEFT OUTER JOIN sys.schemas sch ON sch.schema_id = ao.schema_id
        LEFT OUTER JOIN sys.all_objects aop ON aop.object_id = ao.parent_object_id
        LEFT OUTER JOIN sys.schemas sp ON sp.schema_id = aop.schema_id
        LEFT OUTER JOIN cols c ON c.object_id = ao.object_id
        OUTER APPLY (
            SELECT isnull(object_definition(ao.object_id), '') AS object_definition
            ) def
        WHERE ao.is_ms_shipped = 0
            AND ao.type_desc IN (
                'CHECK_CONSTRAINT'
                ,'SQL_INLINE_TABLE_VALUED_FUNCTION'
                ,'SQL_SCALAR_FUNCTION'
                ,'SQL_STORED_PROCEDURE'
                ,'SQL_TABLE_VALUED_FUNCTION'
                ,'SQL_TRIGGER'
                ,'USER_TABLE'
                ,'VIEW'
                )
            AND (ao.NAME LIKE @objectNameSearch)
            AND (sch.NAME LIKE @objectSchemaSearch)
            AND (
                def.object_definition LIKE @objectDefinitionSearch
                OR c.MatchingColumnCount > 0
                );";

    // Triggers are the only dependent-object relationship in sys.objects that hangs off a
    // simple parent_object_id, so tables and views are the only parent types handled today.
    // A trigger's own schema always matches its parent's (T-SQL requires this).
    private const string DependentTriggersQuery = @"
        SELECT @@SERVERNAME AS server_name
            ,cast(db_name() AS SYSNAME) AS db_name
            ,ao.type_desc
            ,sch.NAME AS schema_name
            ,ao.NAME AS object_name
            ,aop.NAME AS parent_object_name
            ,isnull(cast(objectproperty(ao.object_id, 'IsEncrypted') AS BIT), 0) AS is_encrypted
        FROM sys.all_objects ao
        INNER JOIN sys.all_objects aop ON aop.object_id = ao.parent_object_id
        LEFT OUTER JOIN sys.schemas sch ON sch.schema_id = ao.schema_id
        WHERE ao.type_desc = 'SQL_TRIGGER'
            AND ao.parent_object_id = OBJECT_ID(@parentFqName______);";

    public async Task<List<SearchResultViewModel>> GetDependentObjectsAsync(string connectionString, SearchResultViewModel forObject)
    {
        var results = new List<SearchResultViewModel>();
        if (forObject.TypeDesc != "USER_TABLE" && forObject.TypeDesc != "VIEW")
        {
            return results;
        }

        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = forObject.DbName
        };

        using (var connection = new SqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            using (var command = new SqlCommand(DependentTriggersQuery, connection))
            {
                var quotedName = $"[{forObject.SchemaName.Replace("]", "]]")}].[{forObject.ObjectName.Replace("]", "]]")}]";
                command.Parameters.AddWithValue("@parentFqName______", quotedName);

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        results.Add(new SearchResultViewModel
                        {
                            ServerName = reader["server_name"].ToString() ?? "",
                            DbName = reader["db_name"].ToString() ?? "",
                            TypeDesc = reader["type_desc"].ToString() ?? "",
                            SchemaName = reader["schema_name"].ToString() ?? "",
                            ObjectName = reader["object_name"].ToString() ?? "",
                            ParentFqName = reader["parent_object_name"] != DBNull.Value ? reader["parent_object_name"].ToString() ?? "" : "",
                            IsEncrypted = (bool)reader["is_encrypted"]
                        });
                    }
                }
            }
        }

        return results;
    }

    public async Task<List<string>> GetDatabasesAsync(string connectionString)
    {
        var databases = new List<string>();
        using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync();
            using (var command = new SqlCommand(GetDBsQuery, connection))
            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    databases.Add(reader.GetString(0));
                }
            }
        }
        return databases;
    }

    public async Task<string> ScriptObjectAsync(string connectionString, SearchResultViewModel result, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = result.DbName
            };

            var connInfo = new SqlConnectionInfo();
            connInfo.ServerName = builder.DataSource;
            connInfo.DatabaseName = builder.InitialCatalog;
            connInfo.UseIntegratedSecurity = builder.IntegratedSecurity;
            if (!builder.IntegratedSecurity)
            {
                connInfo.UserName = builder.UserID;
                connInfo.Password = builder.Password;
            }
            connInfo.ConnectionTimeout = builder.ConnectTimeout;
            connInfo.TrustServerCertificate = builder.TrustServerCertificate;

            var serverConnection = new ServerConnection(connInfo);
            var server = new Server(serverConnection);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var database = server.Databases[result.DbName];
                if (database == null) return "-- Database not found";

                ScriptingOptions options = new ScriptingOptions
                {
                    ScriptDrops = false,
                    IncludeIfNotExists = false,
                    ScriptForCreateOrAlter = true,
                    EnforceScriptingOptions = true,
                    TargetServerVersion = SqlServerVersion.Version150,
                    TargetDatabaseEngineType = DatabaseEngineType.Standalone,
                    ClusteredIndexes = true,
                    DriAll = true,
                    Indexes = true,
                    // Scripting a table must produce only the table, not every trigger attached
                    // to it - triggers are scripted independently as their own first-class
                    // object type (see the SQL_TRIGGER case below).
                    Triggers = false,
                    ScriptSchema = true,
                    ScriptData = false,
                    Permissions = true
                };

                var sb = new StringBuilder();
                sb.AppendLine($"-- Scripting object: {result.SchemaName}.{result.ObjectName}");
                sb.AppendLine($"-- Type: {result.TypeDesc}");
                sb.AppendLine($"-- Server: {result.ServerName}");
                sb.AppendLine($"-- Database: {result.DbName}");
                sb.AppendLine("GO");

                cancellationToken.ThrowIfCancellationRequested();

                StringCollection? sc = null;

                switch (result.TypeDesc)
                {
                    case "USER_TABLE":
                        if (database.Tables.Contains(result.ObjectName, result.SchemaName))
                            sc = database.Tables[result.ObjectName, result.SchemaName].Script(options);
                        break;

                    case "SQL_STORED_PROCEDURE":
                        if (database.StoredProcedures.Contains(result.ObjectName, result.SchemaName))
                            sc = database.StoredProcedures[result.ObjectName, result.SchemaName].Script(options);
                        break;

                    case "VIEW":
                        if (database.Views.Contains(result.ObjectName, result.SchemaName))
                            sc = database.Views[result.ObjectName, result.SchemaName].Script(options);
                        break;

                    case "SQL_SCALAR_FUNCTION":
                    case "SQL_TABLE_VALUED_FUNCTION":
                    case "SQL_INLINE_TABLE_VALUED_FUNCTION":
                        if (database.UserDefinedFunctions.Contains(result.ObjectName, result.SchemaName))
                            sc = database.UserDefinedFunctions[result.ObjectName, result.SchemaName].Script(options);
                        break;

                    case "SQL_TRIGGER":
                        // DML triggers aren't in their own top-level SMO collection - they live
                        // under whichever table or view they're attached to. A trigger's schema
                        // always matches its parent object's schema (T-SQL requires this), so
                        // result.SchemaName is reused for the parent lookup.
                        if (database.Tables.Contains(result.ParentFqName, result.SchemaName) &&
                            database.Tables[result.ParentFqName, result.SchemaName].Triggers.Contains(result.ObjectName))
                        {
                            sc = database.Tables[result.ParentFqName, result.SchemaName].Triggers[result.ObjectName].Script(options);
                        }
                        else if (database.Views.Contains(result.ParentFqName, result.SchemaName) &&
                                 database.Views[result.ParentFqName, result.SchemaName].Triggers.Contains(result.ObjectName))
                        {
                            sc = database.Views[result.ParentFqName, result.SchemaName].Triggers[result.ObjectName].Script(options);
                        }
                        else
                        {
                            sb.AppendLine($"-- Trigger's parent object '{result.SchemaName}.{result.ParentFqName}' was not found among tables or views.");
                        }
                        break;

                    default:
                        sb.AppendLine($"-- Scripting not implemented for type: {result.TypeDesc}");
                        break;
                }

                if (sc != null)
                {
                    foreach (var s in sc)
                    {
                        sb.AppendLine(s);
                        sb.AppendLine("GO");
                    }
                }
                else if (result.TypeDesc != "USER_TABLE" && !string.IsNullOrEmpty(result.TypeDesc)) // Fallback for some types if not found in collections
                {
                    sb.AppendLine("-- Object not found in SMO collections.");
                }

                if (result.IsEncrypted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AppendDecryptionAttempt(sb, builder.ConnectionString, result);
                }

                return sb.ToString();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return $"-- Error scripting object: {ex.Message}\r\n/*\r\n{ex}\r\n*/";
            }
            finally
            {
                serverConnection.Disconnect();
            }
        }, cancellationToken);
    }

    // SQL Server never exposes an encrypted object's definition through sys.sql_modules or
    // SMO, to anyone, regardless of permissions - that's the whole point of WITH ENCRYPTION.
    // EncryptedObjectDecryptionService's best-effort recovery is the only way to get the text
    // back, and only for object types where reconstructing a valid ALTER needs nothing beyond
    // the schema-qualified name (see its class comment for the full scope/caveats).
    private static void AppendDecryptionAttempt(StringBuilder sb, string connectionString, SearchResultViewModel result)
    {
        sb.AppendLine();

        if (!EncryptedObjectDecryptionService.IsSupportedType(result.TypeDesc))
        {
            sb.AppendLine($"-- Object is encrypted (WITH ENCRYPTION). Automatic decryption is only implemented for");
            sb.AppendLine($"-- stored procedures and views, not '{result.TypeDesc}'.");
            return;
        }

        var decrypted = EncryptedObjectDecryptionService.TryDecrypt(connectionString, result.TypeDesc, result.SchemaName, result.ObjectName);
        if (decrypted is null)
        {
            sb.AppendLine("-- Object is encrypted (WITH ENCRYPTION). Automatic decryption was attempted but failed -");
            sb.AppendLine("-- this requires sysadmin rights, the Dedicated Admin Connection (DAC) enabled on the");
            sb.AppendLine("-- server, and ALTER permission on the object.");
            return;
        }

        sb.AppendLine("-- The definition below was recovered from a WITH ENCRYPTION object using a known-plaintext");
        sb.AppendLine("-- XOR recovery technique (see EncryptedObjectDecryptionService). This is a best-effort,");
        sb.AppendLine("-- unofficial recovery, not a supported SQL Server feature - verify it carefully before use.");
        sb.AppendLine(decrypted);
        sb.AppendLine("GO");
    }

    public async Task<List<SearchResultViewModel>> SearchDatabaseAsync(
            string connectionString,
        string dbName,
        string objectName,
        string schemaName,
        string definition)
    {
        var results = new List<SearchResultViewModel>();
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = dbName
        };

        using (var connection = new SqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            using (var command = new SqlCommand(ObjectsQuery, connection))
            {
                command.Parameters.AddWithValue("@objectNameSearchParam______", objectName ?? "");
                command.Parameters.AddWithValue("@objectSchemaSearchParam______", schemaName ?? "");
                command.Parameters.AddWithValue("@objectDefinitionSearchParam______", definition ?? "");

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        results.Add(new SearchResultViewModel
                        {
                            ServerName = reader["server_name"].ToString() ?? "",
                            DbName = reader["db_name"].ToString() ?? "",
                            TypeDesc = reader["type_desc"].ToString() ?? "",
                            SchemaName = reader["schema_name"].ToString() ?? "",
                            ObjectName = reader["object_name"].ToString() ?? "",
                            ParentFqName = reader["parent_object_name"] != DBNull.Value ? reader["parent_object_name"].ToString() ?? "" : "",
                            IsEncrypted = (bool)reader["is_encrypted"]
                        });
                    }
                }
            }
        }
        return results;
    }
}