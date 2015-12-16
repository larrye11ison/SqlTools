using Caliburn.Micro;
using SqlTools.DatabaseConnections;
using SqlTools.Shell;
using System.ComponentModel.Composition;

namespace SqlTools.ObjectSearch
{
    /// <summary>
    /// Manages the data used for searching for objects in various DB connections as well as the results of those searches.
    /// </summary>
    [Export(typeof(ObjectSearchViewModel))]
    public class ObjectSearchViewModel : Screen
    {
        public DatabaseConnectionsViewModel Connections { get; set; }

        public DBSearchResultsViewModel SearchResults { get; set; }

        public void InitializeNewObjectSearchForActiveDatabaseConnection()
        {
            if (Connections.ActiveItem != null)
            {
                Connections.ActiveItem.InitiateNewObjectSearchOnDatabase(false);
            }
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();

            // set up the child viewmodels
            SearchResults = IoC.Get<DBSearchResultsViewModel>();
            Connections = IoC.Get<DatabaseConnectionsViewModel>();
            SearchResults.ConductWith(this);
            Connections.ConductWith(this);
        }
    }
}