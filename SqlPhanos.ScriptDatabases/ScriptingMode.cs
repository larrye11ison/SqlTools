namespace SqlPhanos.ScriptDatabases;

public enum ScriptingMode
{
    Full,
    Delta
}

public enum ScriptOutputConflictChoice
{
    Delta,
    Reset,
    Cancel
}
