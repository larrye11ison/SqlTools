using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;
using SqlPhanos.ScriptDatabases;
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

    // Shared with Script Databases (see SqlPhanos.ScriptDatabases.ScriptingOptionsFactory /
    // SingleObjectScriptingService) so both entry points produce identical output - the only
    // sanctioned difference is that Script Databases' header also carries Created/Last Mod
    // dates (used for Delta-mode change detection), which don't apply to a single interactively
    // opened object. Encrypted objects are decrypted via the read-only RC4/DAC technique
    // (EncryptedModuleDecryptor) only - never by executing an ALTER against the live object.
    public async Task<string> ScriptObjectAsync(
        string connectionString,
        SearchResultViewModel result,
        bool allowEncryptedModuleDecrypt = false,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = result.DbName
            };

            if (result.IsEncrypted)
            {
                return SingleObjectScriptingService.ScriptEncryptedObject(
                    builder.ConnectionString,
                    result.SchemaName,
                    result.ObjectName,
                    result.ServerName,
                    result.DbName,
                    allowEncryptedModuleDecrypt);
            }

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

                cancellationToken.ThrowIfCancellationRequested();

                Urn? urn = null;
                string? notFoundMessage = null;

                switch (result.TypeDesc)
                {
                    case "USER_TABLE":
                        if (database.Tables.Contains(result.ObjectName, result.SchemaName))
                            urn = database.Tables[result.ObjectName, result.SchemaName].Urn;
                        break;

                    case "SQL_STORED_PROCEDURE":
                        if (database.StoredProcedures.Contains(result.ObjectName, result.SchemaName))
                            urn = database.StoredProcedures[result.ObjectName, result.SchemaName].Urn;
                        break;

                    case "VIEW":
                        if (database.Views.Contains(result.ObjectName, result.SchemaName))
                            urn = database.Views[result.ObjectName, result.SchemaName].Urn;
                        break;

                    case "SQL_SCALAR_FUNCTION":
                    case "SQL_TABLE_VALUED_FUNCTION":
                    case "SQL_INLINE_TABLE_VALUED_FUNCTION":
                        if (database.UserDefinedFunctions.Contains(result.ObjectName, result.SchemaName))
                            urn = database.UserDefinedFunctions[result.ObjectName, result.SchemaName].Urn;
                        break;

                    case "SQL_TRIGGER":
                        // DML triggers aren't in their own top-level SMO collection - they live
                        // under whichever table or view they're attached to. A trigger's schema
                        // always matches its parent object's schema (T-SQL requires this), so
                        // result.SchemaName is reused for the parent lookup.
                        if (database.Tables.Contains(result.ParentFqName, result.SchemaName) &&
                            database.Tables[result.ParentFqName, result.SchemaName].Triggers.Contains(result.ObjectName))
                        {
                            urn = database.Tables[result.ParentFqName, result.SchemaName].Triggers[result.ObjectName].Urn;
                        }
                        else if (database.Views.Contains(result.ParentFqName, result.SchemaName) &&
                                 database.Views[result.ParentFqName, result.SchemaName].Triggers.Contains(result.ObjectName))
                        {
                            urn = database.Views[result.ParentFqName, result.SchemaName].Triggers[result.ObjectName].Urn;
                        }
                        else
                        {
                            notFoundMessage = $"-- Trigger's parent object '{result.SchemaName}.{result.ParentFqName}' was not found among tables or views.";
                        }
                        break;

                    default:
                        notFoundMessage = $"-- Scripting not implemented for type: {result.TypeDesc}";
                        break;
                }

                if (urn is null)
                {
                    return notFoundMessage ?? "-- Object not found in SMO collections.";
                }

                cancellationToken.ThrowIfCancellationRequested();

                // A trigger opened directly is its own standalone object here, regardless of
                // result.TypeDesc - it must not also embed itself via a parent table/view script,
                // so triggers are never embedded inline in this path (unlike Script Databases,
                // which has no other way to reach a table's triggers at all).
                return SingleObjectScriptingService.ScriptResolvedObject(
                    server,
                    urn,
                    result.ObjectName,
                    result.ServerName,
                    result.DbName,
                    includeTriggersInTableScript: false);
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