using SqlTools.DatabaseConnections;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;

namespace SqlTools.Shell
{
    public class ObjectEnumerationStartingMessage
    { }

    /// <summary>
    /// Fired when a search comes back from a SQL Server containing the initial search results.
    /// </summary>
    public class EnumerateObjectsInDatabaseMessage
    {
        public EnumerateObjectsInDatabaseMessage(SqlConnectionViewModel vm, IEnumerable<Data.SysObject> dbObjects, string dbName, string serverInstanceName)
        {
            Contract.Requires(vm != null);
            Contract.Requires(dbObjects != null);
            Contract.Requires(dbObjects.Count() > 0);
            Contract.Requires(string.IsNullOrWhiteSpace(dbName) == false);
            Contract.Requires(string.IsNullOrWhiteSpace(serverInstanceName) == false);
            DatabaseName = dbName;
            ServerInstanceName = serverInstanceName;
            ConnectionViewModel = vm;
            DBObjects = dbObjects;
        }

        public SqlConnectionViewModel ConnectionViewModel { get; set; }

        public string DatabaseName { get; set; }

        public IEnumerable<Data.SysObject> DBObjects { get; set; }

        public string ServerInstanceName { get; set; }
    }
}