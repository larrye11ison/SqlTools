using System.IO;
using SqlPhanos.ScriptDatabases;
using Xunit;

namespace SqlPhanos.ScriptDatabases.Tests;

public class OutputFolderInspectorTests
{
    [Fact]
    public void HasContents_ReturnsFalse_WhenMissingOrEmpty()
    {
        string path = CreateTempDirectory();
        try
        {
            Assert.False(OutputFolderInspector.HasContents(Path.Combine(path, "missing")));
            Assert.False(OutputFolderInspector.HasContents(path));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void CountContents_CountsTopLevelFilesAndFolders()
    {
        string path = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(path, "a.sql"), "x");
            File.WriteAllText(Path.Combine(path, "b.sql"), "y");
            Directory.CreateDirectory(Path.Combine(path, "server1"));
            Directory.CreateDirectory(Path.Combine(path, "server2"));
            File.WriteAllText(Path.Combine(path, "server1", "nested.sql"), "z");

            OutputFolderContents contents = OutputFolderInspector.CountContents(path);

            Assert.Equal(2, contents.FileCount);
            Assert.Equal(2, contents.FolderCount);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void ClearContents_RemovesFilesAndFolders_KeepsRoot()
    {
        string path = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(path, "a.sql"), "x");
            Directory.CreateDirectory(Path.Combine(path, "server1"));
            File.WriteAllText(Path.Combine(path, "server1", "nested.sql"), "z");

            OutputFolderInspector.ClearContents(path);

            Assert.True(Directory.Exists(path));
            Assert.False(OutputFolderInspector.HasContents(path));
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "SqlPhanosScriptFolderTests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }
}
