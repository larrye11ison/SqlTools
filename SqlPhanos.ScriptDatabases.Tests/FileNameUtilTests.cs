using System.IO;
using SqlPhanos.ScriptDatabases;
using Xunit;

namespace SqlPhanos.ScriptDatabases.Tests;

public class FileNameUtilTests
{
    [Fact]
    public void SanitiseFileName_ReplacesNamedInstanceBackslashWithLookalike()
    {
        string result = FileNameUtil.SanitiseFileName(@"HOST\SQLEXPRESS");

        Assert.Equal("HOST⧵SQLEXPRESS", result);
        Assert.DoesNotContain('\\', result);
        Assert.True(result.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    [Fact]
    public void SanitiseFileName_ReplacesCommonInvalidCharactersWithLookalikes()
    {
        string result = FileNameUtil.SanitiseFileName(@"a:b*c?d""e<f>g|h/i");

        Assert.Equal("a꞉b∗c？d″e＜f＞g│h∕i", result);
        Assert.True(result.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    [Fact]
    public void SanitiseFileName_ReplacesControlCharactersWithControlPictures()
    {
        string result = FileNameUtil.SanitiseFileName("a\0b\tb");

        Assert.Equal("a␀b␉b", result);
        Assert.True(result.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    [Fact]
    public void SanitiseFileName_LeavesSafeNamesUnchanged()
    {
        const string input = "dbo.Customers";
        Assert.Equal(input, FileNameUtil.SanitiseFileName(input));
    }

    [Fact]
    public void SanitiseFileName_ProducesSafeNameForEveryInvalidFilenameCharacter()
    {
        string input = new(Path.GetInvalidFileNameChars());
        string result = FileNameUtil.SanitiseFileName(input);

        Assert.Equal(input.Length, result.Length);
        Assert.True(result.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
        Assert.All(result, ch => Assert.NotEqual('�', ch));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void SanitiseFileName_HandlesNullAndEmpty(string? input)
    {
        Assert.Equal(input ?? string.Empty, FileNameUtil.SanitiseFileName(input!));
    }
}
