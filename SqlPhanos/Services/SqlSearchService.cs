using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Linq;
using System.Text;
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

    public async Task<string> ScriptObjectAsync(string connectionString, SearchResultViewModel result)
    {
        return await Task.Run(() =>
        {
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
                var database = server.Databases[result.DbName];
                if (database == null) return "-- Database not found";

                ScriptingOptions options = new ScriptingOptions
                {
                    ScriptDrops = false,
                    IncludeIfNotExists = true,
                    ClusteredIndexes = true,
                    DriAll = true,
                    Indexes = true,
                    Triggers = true,
                    ScriptSchema = true,
                    ScriptData = false
                };

                var sb = new StringBuilder();
                sb.AppendLine($"-- Scripting object: {result.SchemaName}.{result.ObjectName}");
                sb.AppendLine($"-- Type: {result.TypeDesc}");
                sb.AppendLine($"-- Server: {result.ServerName}");
                sb.AppendLine($"-- Database: {result.DbName}");
                sb.AppendLine("GO");

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

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"-- Error scripting object: {ex.Message}\r\n/*\r\n{ex}\r\n*/";
            }
            finally
            {
                serverConnection.Disconnect();
            }
        });
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