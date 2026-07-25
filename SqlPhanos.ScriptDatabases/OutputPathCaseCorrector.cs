using System;
using System.IO;
using System.Linq;

namespace SqlPhanos.ScriptDatabases;

public static class OutputPathCaseCorrector
{
    public static string? FindExistingChildFolder(string parentDirectory, string desiredFolderName)
    {
        if (!Directory.Exists(parentDirectory))
        {
            return null;
        }

        return Directory.EnumerateDirectories(parentDirectory)
            .FirstOrDefault(path =>
                string.Equals(
                    Path.GetFileName(path),
                    desiredFolderName,
                    StringComparison.OrdinalIgnoreCase));
    }

    public static string EnsureChildFolderCase(string parentDirectory, string desiredFolderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredFolderName);

        Directory.CreateDirectory(parentDirectory);
        string desiredPath = Path.Combine(parentDirectory, desiredFolderName);
        string? existingPath = FindExistingChildFolder(parentDirectory, desiredFolderName);

        if (existingPath is null)
        {
            Directory.CreateDirectory(desiredPath);
            return desiredPath;
        }

        string existingName = Path.GetFileName(existingPath);
        if (string.Equals(existingName, desiredFolderName, StringComparison.Ordinal))
        {
            return existingPath;
        }

        return RenameWithInterimFolder(existingPath, desiredPath, parentDirectory);
    }

    private static string RenameWithInterimFolder(
        string existingPath,
        string desiredPath,
        string parentDirectory)
    {
        string interimPath = Path.Combine(parentDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.Move(existingPath, interimPath);
            Directory.Move(interimPath, desiredPath);
            return desiredPath;
        }
        catch (Exception ex)
        {
            TryRestoreInterim(interimPath, existingPath);
            throw new InvalidOperationException(
                $"Unable to correct folder case from '{Path.GetFileName(existingPath)}' to '{Path.GetFileName(desiredPath)}'.",
                ex);
        }
    }

    private static void TryRestoreInterim(string interimPath, string originalPath)
    {
        if (!Directory.Exists(interimPath) || Directory.Exists(originalPath))
        {
            return;
        }

        try
        {
            Directory.Move(interimPath, originalPath);
        }
        catch
        {
            // Best effort restore after a failed case correction.
        }
    }
}
