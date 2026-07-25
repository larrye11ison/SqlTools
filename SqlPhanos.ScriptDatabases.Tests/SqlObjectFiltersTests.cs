using SqlPhanos.ScriptDatabases;
using Xunit;

namespace SqlPhanos.ScriptDatabases.Tests;

public class SqlObjectFiltersTests
{
    [Theory]
    [InlineData("sys", "objects", false)]
    [InlineData("dbo", "sysdiagrams", false)]
    [InlineData("dbo", "Customers", true)]
    [InlineData("guest", "anything", false)]
    [InlineData("dbo", "sp_helpdiagrams", false)]
    public void IsUserObject_FiltersSystemArtifacts(string schema, string name, bool expected)
    {
        Assert.Equal(expected, SqlObjectFilters.IsUserObject(schema, name));
    }

    [Theory]
    [InlineData("Table", "Tabl")]
    [InlineData("StoredProcedure", "StPr")]
    [InlineData("UserDefinedFunction", "UsDeFu")]
    [InlineData("DatabaseRole", "DbRo")]
    public void AbbreviateObjectType_MatchesLegacyRules(string input, string expected)
    {
        Assert.Equal(expected, SqlObjectFilters.AbbreviateObjectType(input));
    }
}
