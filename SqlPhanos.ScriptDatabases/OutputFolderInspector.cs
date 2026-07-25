using System.IO;
using System.Linq;

namespace SqlPhanos.ScriptDatabases;

public static class OutputFolderInspector
{
    public static bool HasContents(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        return Directory.EnumerateFileSystemEntries(path).Any();
    }

    public static OutputFolderContents CountContents(string path)
    {
        if (!Directory.Exists(path))
        {
            return new OutputFolderContents(0, 0);
        }

        int fileCount = Directory.GetFiles(path).Length;
        int folderCount = Directory.GetDirectories(path).Length;
        return new OutputFolderContents(fileCount, folderCount);
    }

    public static void ClearContents(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (string filePath in Directory.GetFiles(path))
        {
            File.Delete(filePath);
        }

        foreach (string directoryPath in Directory.GetDirectories(path))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }
}

public sealed record OutputFolderContents(int FileCount, int FolderCount);
