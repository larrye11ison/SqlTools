using System.IO;
using System.Linq;
using SqlPhanos.ScriptDatabases;
using Xunit;

namespace SqlPhanos.ScriptDatabases.Tests;

public class OutputPathCaseCorrectorTests
{
    [Fact]
    public void EnsureChildFolderCase_RenamesCaseOnlyMismatch()
    {
        string root = Path.Combine(Path.GetTempPath(), "SqlPhanosScriptCase-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);
        string wrongCase = Path.Combine(root, "foo");
        Directory.CreateDirectory(wrongCase);
        File.WriteAllText(Path.Combine(wrongCase, "marker.txt"), "x");

        try
        {
            string corrected = OutputPathCaseCorrector.EnsureChildFolderCase(root, "FOO");
            Assert.Equal("FOO", Path.GetFileName(corrected));
            Assert.True(File.Exists(Path.Combine(corrected, "marker.txt")));

            string listed = Directory.GetDirectories(root).Single();
            Assert.Equal("FOO", Path.GetFileName(listed));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
