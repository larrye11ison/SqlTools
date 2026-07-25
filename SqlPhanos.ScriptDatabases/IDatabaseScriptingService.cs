using System.Threading.Tasks;

namespace SqlPhanos.ScriptDatabases;

public interface IDatabaseScriptingService
{
    Task<ScriptDatabaseResult> ScriptDatabaseAsync(ScriptDatabaseRequest request);
}
