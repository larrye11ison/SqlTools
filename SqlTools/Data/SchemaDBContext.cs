using Caliburn.Micro;
using Microsoft.SqlServer.Management.Common;
using SqlTools.DatabaseConnections;
using SqlTools.Shell;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using smo = Microsoft.SqlServer.Management.Smo;

namespace SqlTools.Data
{
    /// <summary>
    /// Manages the interactions with a database to get objects and script them out.
    /// </summary>
    /// <remarks>
    /// There are some hard-coded queries below to get lists of databases, list of objects in a database, etc.
    /// I initially attempted to avoid this entirely by using SMO to handle all of this. However, it was often
    /// FAR too slow. This was in spite of my best efforts to use the stuff that SMO provides which theoretically
    /// allow you to make it perform better like SetDefaultInitFields. I just couldn't get SMO to perform
    /// adequately no matter what I did.
    /// </remarks>
    internal class SchemaDBContext
    {
        internal const string getDBsQuery = @"
                                            SELECT db.NAME AS db_name
	                                            ,QUOTENAME(db.NAME) AS quote_name
	                                            ,p.NAME AS owner_name
	                                            ,db.state_desc
	                                            ,db.compatibility_level
	                                            ,@@version
                                            FROM master.sys.databases db
                                            LEFT OUTER JOIN master.sys.server_principals p ON p.sid = db.owner_sid
                                            WHERE db.NAME NOT IN (
		                                            'master'
		                                            ,'msdb'
		                                            ,'tempdb'
		                                            ,'model'
		                                            )
                                            ORDER BY db.NAME";

        internal const string objectsQuery = @"
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
		                --,row_number() OVER (
			            --    PARTITION BY c.object_id ORDER BY c.column_id
			            --    ) AS row_num
	                FROM sys.columns c
	                WHERE
		                -- get the columns that are like our search param...
		                c.NAME LIKE @objectDefinitionSearch
		                -- but ONLY if a value was spec'd for the object definition search
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
		                );
                                        ";

        /// <summary>
        /// The options used for the CREATE script.
        /// </summary>
        private static readonly smo.ScriptingOptions scriptCreateOptions
            = new smo.ScriptingOptions()
            {
                DriAll = true,
                DriAllKeys = true,
                DriAllConstraints = true,
                SchemaQualify = true,
                Indexes = true,
                ScriptForCreateOrAlter = true
            };

        /// <summary>
        /// The options used for the DROP script.
        /// </summary>
        private static readonly smo.ScriptingOptions scriptDropOptions
            = new smo.ScriptingOptions()
            {
                IncludeIfNotExists = true,
                ScriptDrops = true
            };

        public SchemaDBContext(SqlConnectionViewModel vm)
        {
            ConnectionViewModel = vm;
        }

        public SqlConnectionViewModel ConnectionViewModel { get; private set; }

        public static async Task<IEnumerable<DatabaseViewModel>> GetDatabases(SqlConnectionViewModel vm)
        {
            using (var cn = new SqlConnection(vm.ConnectionString()))
            {
                await cn.OpenAsync();
                using (var cmd = cn.CreateCommand())
                {
                    cmd.CommandText = getDBsQuery;
                    cmd.CommandType = CommandType.Text;
                    using (var rdr = await cmd.ExecuteReaderAsync())
                    {
                        return DatabaseViewModel.FromDataReader(rdr).ToArray();
                    }
                }
            }
        }

        public async Task EnumerateObjectsInDatabases(
            SqlConnectionViewModel connection,
            IEnumerable<DatabaseViewModel> databases,
            string nameSearchString,
            string schemaSearchString,
            string definitionSearchString,
            IEventAggregator eventAggregator)
        {
            Contract.Requires(databases != null);
            //int databaseCount = databases.Count();

            var log = LogManager.GetLog(typeof(SchemaDBContext));

            var tasks = new List<Task>();

            _ = eventAggregator.PublishOnUIThreadAsync(new ObjectEnumerationStartingMessage());

            foreach (var db in databases)
            {
                var t = Task.Factory.StartNew(() =>
                    {
                        try
                        {
                            log.Info("Sch DB Ctx begin foreach DB {0}", db.db_name);
                            var connectionString = ConnectionViewModel.ConnectionString();
                            var cnStringBuilder = new SqlConnectionStringBuilder(connectionString);
                            cnStringBuilder.InitialCatalog = db.db_name;
                            using (var cn = new SqlConnection(cnStringBuilder.ConnectionString))
                            {
                                cn.Open();
                                var objectNameSearchParam = new SqlParameter("@objectNameSearchParam______", (nameSearchString ?? "").Trim());
                                var objectSchemaSearchParam = new SqlParameter("@objectSchemaSearchParam______", (schemaSearchString ?? "").Trim());
                                var objectDefinitionSearchParam = new SqlParameter("@objectDefinitionSearchParam______", (definitionSearchString ?? "").Trim());
                                var cmd = cn.CreateCommand();
                                cmd.CommandTimeout = 0;
                                cmd.CommandText = objectsQuery;
                                cmd.Parameters.Add(objectNameSearchParam);
                                cmd.Parameters.Add(objectSchemaSearchParam);
                                cmd.Parameters.Add(objectDefinitionSearchParam);
                                using (var result = cmd.ExecuteReader())
                                {
                                    var objects = SysObject.MapFrom(result).ToArray();

                                    var message = new EnumerateObjectsInDatabaseMessage(ConnectionViewModel, objects, db.db_name, connection.ServerAndInstance);
                                    eventAggregator.PublishOnUIThreadAsync(message);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            connection.ErrorMessage = ex.ToString();
                            eventAggregator.PublishOnUIThreadAsync(ex);
                        }
                    }, TaskCreationOptions.LongRunning);
                tasks.Add(t);
            }
            await Task.Factory.StartNew(() => Task.WaitAll(tasks.ToArray()));

            return;
        }

        public async Task<string> GetObjectDefinition(SysObject so)
        {
            var connectionString = ConnectionViewModel.ConnectionString();
            var csBuilder = new SqlConnectionStringBuilder(connectionString);

            // Create SqlConnectionInfo from the connection string to preserve all properties including protocol
            var connInfo = new SqlConnectionInfo();
            connInfo.ServerName = csBuilder.DataSource;
            connInfo.DatabaseName = csBuilder.InitialCatalog;
            connInfo.UseIntegratedSecurity = csBuilder.IntegratedSecurity;
            if (!csBuilder.IntegratedSecurity)
            {
                connInfo.UserName = csBuilder.UserID;
                connInfo.Password = csBuilder.Password;
            }
            connInfo.ConnectionTimeout = csBuilder.ConnectTimeout;

            var srvConnect = new ServerConnection(connInfo);
            var srv = new smo.Server(srvConnect);

            try
            {
                srv.Refresh();
                SetDefaultInitFields(srv);
                smo.Database database = null;
                StringBuilder sb = null;
                await Task.Factory.StartNew(() =>
                {
                    database = srv.Databases[so.db_name];

                    // SMO keeps different top level collections for each type of object. You can't go to the DB and say,
                    // "Give me the object with this URI," even though each object DOES have a URI property. In some cases,
                    // you have to supply a parent object (e.g. Triggers). So the switch block below figures out how to
                    // grab each object from the database based on its type.
                    //
                    // Worse yet, even though each of them does contain a method called Script() that takes a single parameter
                    // of type ScriptOptions, there's no base object or interface that defines these objects as something
                    // that can script itself. To get make this easier, we cheat and just use a dynamic type then call the
                    // Script() method on that dynamic object. This is pretty hokey, but this only gets called once each
                    // time the user requests that an object get scripted out, i.e. it's not running in a loop thousands of times.
                    dynamic scriptableObject = null;
                    switch (so.type_desc)
                    {
                        case "CHECK_CONSTRAINT":
                            scriptableObject = database.Tables[so.parent_object_name, so.parent_object_schema_name].Checks[so.object_name];
                            break;

                        case "DEFAULT_CONSTRAINT":
                            // We basically ignore default constraints - they get scripted out as part of scripting a table... I think that's why I did this...
                            //ob = db.Tables[so.parent_object_name, so.parent_object_schema_name]
                            break;

                        case "SQL_STORED_PROCEDURE":
                            scriptableObject = database.StoredProcedures[so.object_name, so.schema_name];
                            break;

                        case "USER_TABLE":
                            scriptableObject = database.Tables[so.object_name, so.schema_name];
                            break;

                        case "SQL_INLINE_TABLE_VALUED_FUNCTION":
                        case "SQL_SCALAR_FUNCTION":
                        case "SQL_TABLE_VALUED_FUNCTION":
                            scriptableObject = database.UserDefinedFunctions[so.object_name, so.schema_name];
                            break;

                        case "SQL_TRIGGER":
                            scriptableObject = database.Tables[so.parent_object_name, so.parent_object_schema_name].Triggers[so.object_name];
                            break;

                        case "VIEW":
                            scriptableObject = database.Views[so.object_name, so.schema_name];
                            break;

                        default:
                            throw new InvalidOperationException(String.Format("Unknown DB object type of «{0}» was encountered. " +
                                "I don't know what to do with objects of that type.", so.type_desc));
                    }

                    sb = new StringBuilder();
                    if (scriptableObject == null)
                    {
                        sb.Append("The specified object was not found on the server.");
                    }
                    else
                    {
                        sb.AppendFormat(@"-- {0} :: {1}.{2}.{3}{4}", so.server_name, so.db_name, so.schema_name, so.object_name, Environment.NewLine);
                        sb.AppendFormat(@"-- {0}{1}", so.type_desc, Environment.NewLine);
                        sb.AppendFormat(@"-- Script Created: {0:yyyy-MM-dd HH\:mm\:ss zz}{1}", DateTimeOffset.Now, Environment.NewLine);
                        sb.AppendFormat(@"-- Object Created: {0:yyyy-MM-dd HH\:mm\:ss}{1}", so.create_date, Environment.NewLine);
                        sb.AppendFormat(@"-- Object LastMod: {0:yyyy-MM-dd HH\:mm\:ss}{1}", so.modify_date, Environment.NewLine);

                        sb.AppendFormat("use {0}{1}go{1}", QuoteName(so.db_name), Environment.NewLine);
                        // Script the DROP surrounded by multiline comments
                        ProcessScriptResults(scriptableObject.Script(scriptDropOptions), sb, "/*", "*/");
                        // now script the actual body of the object
                        ProcessScriptResults(scriptableObject.Script(scriptCreateOptions), sb);
                    }
                });

                return sb.ToString();
            }
            finally
            {
                if (srvConnect != null && srvConnect.IsOpen)
                {
                    srvConnect.Disconnect();
                }
            }
        }

        private static void ProcessScriptResults(StringCollection createResults, StringBuilder sb, string surroundWithStart = "", string surroundWithEnd = "")
        {
            var resultsICareAbout =
                from line in createResults.Cast<string>()
                where (
                    line.StartsWith("SET ANSI_NULLS", StringComparison.CurrentCultureIgnoreCase) == false
                    && line.StartsWith("SET QUOTED_IDENTIFIER", StringComparison.CurrentCultureIgnoreCase) == false
                    )
                select line;
            foreach (var item in resultsICareAbout)
            {
                sb.AppendFormat("{3}{1}{0}{1}{4}{1}{2}{1}", item, Environment.NewLine, "go", surroundWithStart, surroundWithEnd);
            }
        }

        private static void SetDefaultInitFields(smo.Server srv)
        {
            var defaultProps = new string[] { "Name", "Owner", "DateLastModified", "CreateDate", "Schema" };
            var defaultCheckProps = new string[] { "Name", "DateLastModified", "CreateDate" };
            srv.SetDefaultInitFields(typeof(smo.Database), "Name", "Owner", "CreateDate");
            srv.SetDefaultInitFields(typeof(smo.Check), defaultCheckProps);
            srv.SetDefaultInitFields(typeof(smo.UserDefinedFunction), defaultProps);
            srv.SetDefaultInitFields(typeof(smo.StoredProcedure), defaultProps);
            srv.SetDefaultInitFields(typeof(smo.Trigger), defaultCheckProps);
            srv.SetDefaultInitFields(typeof(smo.View), defaultProps);
            srv.SetDefaultInitFields(typeof(smo.Table), defaultProps);
        }

        private string QuoteName(string s)
        {
            Contract.Requires(string.IsNullOrWhiteSpace(s) == false);
            return String.Format("[{0}]", s.Replace("]", "]]"));
        }
    }
}