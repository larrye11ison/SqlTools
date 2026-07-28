using SqlPhanos.ScriptDatabases;
using Xunit;

namespace SqlPhanos.ScriptDatabases.Tests;

public class ScriptingOptionsFactoryTests
{
    [Fact]
    public void Create_SetsTheOptionsBothScriptingPathsRelyOn()
    {
        var options = ScriptingOptionsFactory.Create(includeTriggersInTableScript: true);

        Assert.True(options.DriAll);
        Assert.True(options.DriIncludeSystemNames);
        Assert.True(options.Indexes);
        Assert.True(options.ClusteredIndexes);
        // False, not true - ObjectScriptHeaderBuilder already writes its own USE/GO ahead of
        // every scripted object; leaving this true made SMO emit a second one (see
        // ScriptingOptionsFactory's comment on this setting).
        Assert.False(options.IncludeDatabaseContext);
        Assert.True(options.IncludeDatabaseRoleMemberships);
        Assert.True(options.Permissions);
        Assert.True(options.ScriptBatchTerminator);
        Assert.True(options.SchemaQualify);
        Assert.True(options.ScriptSchema);
        Assert.False(options.ScriptData);
        Assert.False(options.ScriptDrops);
        Assert.False(options.IncludeIfNotExists);
        Assert.True(options.ScriptForCreateOrAlter);
        Assert.True(options.EnforceScriptingOptions);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Create_TriggersFlagsMatchesTheRequestedValue(bool includeTriggers)
    {
        var options = ScriptingOptionsFactory.Create(includeTriggers);

        Assert.Equal(includeTriggers, options.Triggers);
    }
}
