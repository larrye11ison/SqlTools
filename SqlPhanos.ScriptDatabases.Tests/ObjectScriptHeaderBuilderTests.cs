using System;
using SqlPhanos.ScriptDatabases;
using Xunit;

namespace SqlPhanos.ScriptDatabases.Tests;

public class ObjectScriptHeaderBuilderTests
{
    private static readonly DateTimeOffset ScriptedOn = new(2024, 6, 15, 13, 45, 30, TimeSpan.Zero);

    [Fact]
    public void Build_WithoutDates_OmitsCreatedAndLastModButKeepsScripted()
    {
        string header = ObjectScriptHeaderBuilder.Build(
            "MyProc",
            "MYSERVER",
            "MyDatabase",
            ScriptedOn);

        Assert.Contains("Object:     MyProc", header);
        Assert.Contains("Server:     MYSERVER", header);
        Assert.Contains("Database:   MyDatabase", header);
        Assert.Contains("Scripted:   ", header);
        Assert.DoesNotContain("Created:", header);
        Assert.DoesNotContain("Last Mod:", header);
        Assert.Contains("USE [MyDatabase]", header);
        Assert.Contains("GO", header);
    }

    [Fact]
    public void Build_WithDates_IncludesCreatedAndLastMod()
    {
        var dates = new ObjectDates(
            new DateTime(2023, 1, 2, 3, 4, 5),
            new DateTime(2023, 2, 3, 4, 5, 6));

        string header = ObjectScriptHeaderBuilder.Build(
            "MyTable",
            "MYSERVER",
            "MyDatabase",
            ScriptedOn,
            dates);

        Assert.Contains("Created:    2023-01-02 03:04:05", header);
        Assert.Contains("Last Mod:   2023-02-03 04:05:06", header);
    }

    [Fact]
    public void Build_EscapesRightBracketsInDatabaseNameForUseStatement()
    {
        string header = ObjectScriptHeaderBuilder.Build(
            "MyTable",
            "MYSERVER",
            "Weird]DbName",
            ScriptedOn);

        Assert.Contains("USE [Weird]]DbName]", header);
    }
}
