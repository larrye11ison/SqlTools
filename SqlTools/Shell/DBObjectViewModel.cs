using Caliburn.Micro;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlTools.DatabaseConnections;
using SqlTools.Scripting;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Threading.Tasks;

namespace SqlTools.Shell
{
    /// <summary>
    /// Represents a single DB Object that was returned by the initial search on the SQL Server.
    /// </summary>
    public class DBObjectViewModel : PropertyChangedBase
    {
        private readonly IShell shell;

        public DBObjectViewModel(Data.SysObject dbObject, SqlConnectionViewModel cnvm, IEventAggregator eventAgg, IShell shell)
        {
            Contract.Requires(dbObject != null);
            Contract.Requires(cnvm != null);
            Contract.Requires(eventAgg != null);

            this.shell = shell;
            EventAggregator = eventAgg;
            SearchText =
                string.Format("{0} {1} {2} {3} {4}",
                dbObject.server_name,
                dbObject.db_name,
                dbObject.type_desc,
                dbObject.full_name,
                dbObject.parent_fq_name);
            ConnectionViewModel = cnvm;
            SysObject = dbObject;
        }

        public bool CanScriptObject
        {
            get
            {
                return !SysObject.is_encrypted;
            }
        }

        public SqlConnectionViewModel ConnectionViewModel { get; set; }

        public IEventAggregator EventAggregator { get; set; }

        /// <summary>
        /// A string containing a combination of object name, owner, type description,
        /// server name and the parent's fully qualified name.
        /// </summary>
        public string SearchText { get; set; }

        public Data.SysObject SysObject { get; private set; }

        public override bool Equals(object obj)
        {
            if ((obj is DBObjectViewModel) == false)
            {
                return false;
            }
            var co = (DBObjectViewModel)obj;
            return co.SysObject.Equals(SysObject);
        }

        public override int GetHashCode()
        {
            return ToString().GetHashCode();
        }

        public async void ScriptObject()
        {
            var vm = this;

            var firstNew = IoC.Get<ScriptedObjectDocumentViewModel>();
            string initialObjectDescription = string.Format("-- Loading object:{0}" +
                "-- Server:   {1}{0}" +
                "-- Database: {2}{0}" +
                "-- Object:   {3}{0}",
                Environment.NewLine,
                SysObject.server_name,
                SysObject.db_name,
                SysObject.full_name);
            try
            {
                // Publish the scripted object twice - first time, it will appear in the UI
                // as "empty." After the await, the fully populated version will be re-published
                // and the tab will be updated with the results.
                firstNew.CanonicalName = SysObject.LongDescription;
                firstNew.DisplayName = SysObject.full_name + " (loading...)";
                firstNew.SqlText = initialObjectDescription;

                firstNew.IsLoadingDefinition = true;
                firstNew.SetSqlFormat();
                EventAggregator.PublishOnUIThreadAsync(firstNew);
                //shell.OpenDocument(firstNew);

                var so = await GetScriptedObject(vm);

                // "Reset" the viewmodel with the actual object
                firstNew.Initialize(so);
                //firstNew.SqlText = StripCommentsFromSQL(firstNew.SqlText);
                firstNew.SqlText = firstNew.SqlText;
                firstNew.IsLoadingDefinition = false;
                firstNew.SetSqlFormat();
                EventAggregator.PublishOnUIThreadAsync(firstNew);
                //shell.ActivateDocument(firstNew);
            }
            catch (Exception ex)
            {
                firstNew.DisplayName = "ERROR - " + firstNew.DisplayName;
                firstNew.SqlText += string.Format("Error scripting object:{0}{1}", Environment.NewLine, ex);
                EventAggregator.PublishOnUIThreadAsync(new ShellMessage
                {
                    Severity = Severity.Warning,
                    MessageText = string.Format("Error scripting object {0}:{1}{2}", vm.SysObject.full_name, Environment.NewLine, ex)
                });
            }
        }

        private static async Task<ScriptedObjectInfo> GetScriptedObject(DBObjectViewModel vm)
        {
            var ctx = new Data.SchemaDBContext(vm.ConnectionViewModel);
            var objectDefinition = await ctx.GetObjectDefinition(vm.SysObject);

            var scriptedInfo = new ScriptedObjectInfo(objectDefinition, vm.SysObject);

            return scriptedInfo;
        }

        private string StripCommentsFromSQL(string SQL)
        {
            // Later on, we'll use this method to strip comments when doing comparisons

            TSql130Parser parser = new TSql130Parser(true);
            IList<ParseError> errors;
            var fragments = parser.Parse(new System.IO.StringReader(SQL), out errors);

            // clear comments
            string result = string.Join(
              string.Empty,
              fragments.ScriptTokenStream
                  .Where(x => x.TokenType != TSqlTokenType.MultilineComment)
                  .Where(x => x.TokenType != TSqlTokenType.SingleLineComment)
                  .Select(x => x.Text));

            return result;
        }
    }
}