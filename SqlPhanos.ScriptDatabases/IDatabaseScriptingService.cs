using System.Threading;
using System.Threading.Tasks;

namespace SqlPhanos.ScriptDatabases;

public interface IDatabaseScriptingService
{
    Task<ScriptDatabaseResult> ScriptDatabaseAsync(ScriptDatabaseRequest request);

    /// <summary>
    /// Counts encrypted, in-scope objects in a database without scripting anything - lets a caller
    /// check every selected database for encrypted modules up front, before any scripting begins,
    /// instead of discovering (and prompting for) them one database at a time as a bulk run reaches
    /// each one.
    /// </summary>
    Task<int> CountEncryptedObjectsAsync(string connectionString, string databaseName, CancellationToken cancellationToken);
}
