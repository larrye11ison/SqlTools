using System.IO;

namespace SqlPhanos.ScriptDatabases;

public static class DatabaseOutputPathResolver
{
    public static string ResolveDatabaseOutputDirectory(
        string baseOutputDirectory,
        string actualServerName,
        string actualDatabaseName)
    {
        string serverFolderName = FileNameUtil.SanitiseFileName(actualServerName);
        string databaseFolderName = FileNameUtil.SanitiseFileName(actualDatabaseName);
        string? existingServer = OutputPathCaseCorrector.FindExistingChildFolder(
            baseOutputDirectory,
            serverFolderName);
        string serverDirectory = existingServer
            ?? Path.Combine(baseOutputDirectory, serverFolderName);
        string? existingDatabase = OutputPathCaseCorrector.FindExistingChildFolder(
            serverDirectory,
            databaseFolderName);
        return existingDatabase ?? Path.Combine(serverDirectory, databaseFolderName);
    }
}
