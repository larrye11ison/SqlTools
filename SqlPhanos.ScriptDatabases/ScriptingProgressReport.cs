namespace SqlPhanos.ScriptDatabases;

public sealed record ScriptingProgressReport(
    int CompletedObjects,
    int TotalObjects,
    int ParallelTasks,
    string DatabaseName);
