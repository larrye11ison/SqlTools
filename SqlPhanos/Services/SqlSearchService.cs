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
                'SQL_INLINE_TABLE_VALUED_FUNCTION'
                ,'SQL_SCALAR_FUNCTION'
                ,'SQL_STORED_PROCEDURE'
                ,'SQL_TABLE_VALUED_FUNCTION'
                ,'SQL_TRIGGER'
                ,'USER_TABLE'
                ,'VIEW'
                ,'SEQUENCE_OBJECT'
                ,'TABLE_TYPE'
                ,'CLR_STORED_PROCEDURE'
                ,'CLR_SCALAR_FUNCTION'
                ,'CLR_TABLE_VALUED_FUNCTION'
                ,'CLR_TRIGGER'
                )
            AND (ao.NAME LIKE @objectNameSearch)
            AND (sch.NAME LIKE @objectSchemaSearch)
            AND (
                def.object_definition LIKE @objectDefinitionSearch
                OR c.MatchingColumnCount > 0
                );";

    // UDTs (both CLR-backed and plain CREATE TYPE ... FROM <base type> aliases) live only in
    // sys.types, not sys.objects - sys.types has no type_desc column of its own, so this can't just
    // extend ObjectsQuery's IN list; it's a separate query, run alongside it and merged into the
    // same result list. is_table_type = 0 excludes user-defined table types, which DO have a
    // sys.objects row (type_desc = 'TABLE_TYPE') and are already covered by ObjectsQuery - without
    // this exclusion they'd be listed twice. Deliberately ignores @objectDefinitionSearchParam______:
    // these types have no body/definition to search, so a definition-text filter simply doesn't
    // apply to them rather than hiding them entirely.
    private const string UserDefinedTypesQuery = @"
        DECLARE @objectNameSearch VARCHAR(max)
            ,@objectSchemaSearch VARCHAR(max);

        SET @objectNameSearch = '%' + ltrim(rtrim(isnull(@objectNameSearchParam______, ''))) + '%';
        SET @objectSchemaSearch = '%' + ltrim(rtrim(isnull(@objectSchemaSearchParam______, ''))) + '%';

        SELECT @@SERVERNAME AS server_name
            ,cast(db_name() AS SYSNAME) AS db_name
            ,CASE WHEN t.is_assembly_type = 1 THEN 'USER_DEFINED_TYPE' ELSE 'USER_DEFINED_DATA_TYPE' END AS type_desc
            ,sch.NAME AS schema_name
            ,t.NAME AS object_name
            ,CAST(NULL AS SYSNAME) AS parent_object_name
            ,CAST(0 AS BIT) AS is_encrypted
        FROM sys.types t
        LEFT OUTER JOIN sys.schemas sch ON sch.schema_id = t.schema_id
        WHERE t.is_user_defined = 1
            AND t.is_table_type = 0
            AND (t.NAME LIKE @objectNameSearch)
            AND (sch.NAME LIKE @objectSchemaSearch);";

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
    public async Task<SingleObjectScriptResult> ScriptObjectAsync(
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
                if (database == null) return new SingleObjectScriptResult("-- Database not found", null);

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
                    case "CLR_STORED_PROCEDURE":
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
                    case "CLR_SCALAR_FUNCTION":
                    case "CLR_TABLE_VALUED_FUNCTION":
                        if (database.UserDefinedFunctions.Contains(result.ObjectName, result.SchemaName))
                            urn = database.UserDefinedFunctions[result.ObjectName, result.SchemaName].Urn;
                        break;

                    case "SEQUENCE_OBJECT":
                        if (database.Sequences.Contains(result.ObjectName, result.SchemaName))
                            urn = database.Sequences[result.ObjectName, result.SchemaName].Urn;
                        break;

                    case "TABLE_TYPE":
                        if (database.UserDefinedTableTypes.Contains(result.ObjectName, result.SchemaName))
                            urn = database.UserDefinedTableTypes[result.ObjectName, result.SchemaName].Urn;
                        break;

                    case "USER_DEFINED_TYPE":
                        if (database.UserDefinedTypes.Contains(result.ObjectName, result.SchemaName))
                            urn = database.UserDefinedTypes[result.ObjectName, result.SchemaName].Urn;
                        break;

                    case "USER_DEFINED_DATA_TYPE":
                        if (database.UserDefinedDataTypes.Contains(result.ObjectName, result.SchemaName))
                            urn = database.UserDefinedDataTypes[result.ObjectName, result.SchemaName].Urn;
                        break;

                    case "SQL_TRIGGER":
                    case "CLR_TRIGGER":
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
                    return new SingleObjectScriptResult(notFoundMessage ?? "-- Object not found in SMO collections.", null);
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
                return new SingleObjectScriptResult($"-- Error scripting object: {ex.Message}\r\n/*\r\n{ex}\r\n*/", null);
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

            using (var command = new SqlCommand(UserDefinedTypesQuery, connection))
            {
                command.Parameters.AddWithValue("@objectNameSearchParam______", objectName ?? "");
                command.Parameters.AddWithValue("@objectSchemaSearchParam______", schemaName ?? "");

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