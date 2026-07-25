using System;
using System.IO;
using SqlPhanos.ScriptDatabases;
using Xunit;

namespace SqlPhanos.ScriptDatabases.Tests;

public class ScriptFileTimestampMatcherTests
{
    [Fact]
    public void BuildAndReadMetadataComment_RoundTripsFullTickFidelity()
    {
        DateTime created = new DateTime(2023, 1, 2, 3, 4, 5, 123, DateTimeKind.Unspecified)
            .AddTicks(4567);
        DateTime modified = new DateTime(2023, 2, 3, 4, 5, 6, 789, DateTimeKind.Unspecified)
            .AddTicks(1);
        ObjectDates dates = new(created, modified);

        string? comment = ScriptFileTimestampMatcher.BuildMetadataComment(dates);
        Assert.Equal($"-- {created.Ticks}/{modified.Ticks}", comment);

        string path = Path.Combine(Path.GetTempPath(), "SqlPhanosScriptTs-" + Guid.NewGuid().ToString("N") + ".sql");
        File.WriteAllText(path, comment + Environment.NewLine + "CREATE PROC dbo.X AS RETURN;");

        try
        {
            Assert.True(ScriptFileTimestampMatcher.IsUnchanged(path, dates));
            Assert.False(ScriptFileTimestampMatcher.IsUnchanged(
                path,
                new ObjectDates(created, modified.AddTicks(1))));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void IsUnchanged_FalseWhenMetadataCommentMissing()
    {
        string path = Path.Combine(Path.GetTempPath(), "SqlPhanosScriptTs-" + Guid.NewGuid().ToString("N") + ".sql");
        File.WriteAllText(path, "CREATE PROC dbo.X AS RETURN;");

        try
        {
            DateTime created = new(2023, 1, 2, 3, 4, 5, DateTimeKind.Unspecified);
            DateTime modified = new(2023, 2, 3, 4, 5, 6, DateTimeKind.Unspecified);
            ScriptFileTimestampMatcher.ApplyObjectDates(path, new ObjectDates(created, modified));

            Assert.False(ScriptFileTimestampMatcher.IsUnchanged(path, new ObjectDates(created, modified)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RoundToSecond_StillUsedForFilesystemTimestamps()
    {
        DateTime input = new(2024, 6, 15, 13, 45, 30, 850, DateTimeKind.Local);
        DateTime rounded = ScriptFileTimestampMatcher.RoundToSecond(input);

        Assert.Equal(30, rounded.Second);
        Assert.Equal(0, rounded.Millisecond);
        Assert.Equal(DateTimeKind.Unspecified, rounded.Kind);
    }
}
