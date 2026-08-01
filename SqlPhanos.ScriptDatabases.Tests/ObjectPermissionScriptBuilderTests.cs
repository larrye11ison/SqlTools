using System.Collections.Generic;
using SqlPhanos.ScriptDatabases;
using Xunit;

namespace SqlPhanos.ScriptDatabases.Tests;

public class ObjectPermissionScriptBuilderTests
{
    // ObjectPermissionInfo (the real SMO type BuildPermissionScript enumerates) has an internal
    // constructor and internal property setters - it can only be populated from a live
    // connection, so these tests exercise BuildGrantStatements directly against hand-built
    // GrantedObjectPermission values instead, covering everything the SMO-facing wrapper feeds
    // into it.

    [Fact]
    public void BuildGrantStatements_NoPermissions_ReturnsEmptyString()
    {
        var result = ObjectPermissionScriptBuilder.BuildGrantStatements([], "[dbo].[A]");

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void BuildGrantStatements_SinglePermission_ProducesOneGrantStatement()
    {
        var permissions = new List<GrantedObjectPermission>
        {
            new("public", PermissionGrantState.Grant, null, ["SELECT"])
        };

        var result = ObjectPermissionScriptBuilder.BuildGrantStatements(permissions, "[dbo].[A]");

        Assert.Equal("GRANT SELECT ON [dbo].[A] TO [public];" + System.Environment.NewLine + "GO" + System.Environment.NewLine, result);
    }

    [Fact]
    public void BuildGrantStatements_MultiplePermissionsForSameGrantee_AreListedTogetherInOneStatement()
    {
        var permissions = new List<GrantedObjectPermission>
        {
            new("public", PermissionGrantState.Grant, null, ["SELECT", "UPDATE", "INSERT"])
        };

        var result = ObjectPermissionScriptBuilder.BuildGrantStatements(permissions, "[dbo].[A]");

        Assert.Equal("GRANT SELECT, UPDATE, INSERT ON [dbo].[A] TO [public];" + System.Environment.NewLine + "GO" + System.Environment.NewLine, result);
    }

    [Fact]
    public void BuildGrantStatements_DenyState_UsesDenyKeywordNotGrant()
    {
        var permissions = new List<GrantedObjectPermission>
        {
            new("BlockedRole", PermissionGrantState.Deny, null, ["DELETE"])
        };

        var result = ObjectPermissionScriptBuilder.BuildGrantStatements(permissions, "[dbo].[A]");

        Assert.Equal("DENY DELETE ON [dbo].[A] TO [BlockedRole];" + System.Environment.NewLine + "GO" + System.Environment.NewLine, result);
    }

    [Fact]
    public void BuildGrantStatements_GrantWithGrantOption_AppendsWithGrantOptionClause()
    {
        var permissions = new List<GrantedObjectPermission>
        {
            new("db_owner", PermissionGrantState.GrantWithGrantOption, null, ["EXECUTE"])
        };

        var result = ObjectPermissionScriptBuilder.BuildGrantStatements(permissions, "[dbo].[usp_Sample]");

        Assert.Equal(
            "GRANT EXECUTE ON [dbo].[usp_Sample] TO [db_owner] WITH GRANT OPTION;" + System.Environment.NewLine + "GO" + System.Environment.NewLine,
            result);
    }

    [Fact]
    public void BuildGrantStatements_ColumnLevelGrant_IncludesColumnNameInParens()
    {
        var permissions = new List<GrantedObjectPermission>
        {
            new("ReportingRole", PermissionGrantState.Grant, "SSN", ["SELECT"])
        };

        var result = ObjectPermissionScriptBuilder.BuildGrantStatements(permissions, "[dbo].[Person]");

        Assert.Equal(
            "GRANT SELECT ON [dbo].[Person] ([SSN]) TO [ReportingRole];" + System.Environment.NewLine + "GO" + System.Environment.NewLine,
            result);
    }

    [Fact]
    public void BuildGrantStatements_MultipleGrantees_AreOrderedAlphabeticallyByGrantee()
    {
        var permissions = new List<GrantedObjectPermission>
        {
            new("ZebraRole", PermissionGrantState.Grant, null, ["SELECT"]),
            new("AlphaRole", PermissionGrantState.Grant, null, ["SELECT"])
        };

        var result = ObjectPermissionScriptBuilder.BuildGrantStatements(permissions, "[dbo].[A]");

        var expected =
            "GRANT SELECT ON [dbo].[A] TO [AlphaRole];" + System.Environment.NewLine + "GO" + System.Environment.NewLine +
            "GRANT SELECT ON [dbo].[A] TO [ZebraRole];" + System.Environment.NewLine + "GO" + System.Environment.NewLine;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildGrantStatements_PermissionWithNoKeywords_IsSkipped()
    {
        var permissions = new List<GrantedObjectPermission>
        {
            new("public", PermissionGrantState.Grant, null, [])
        };

        var result = ObjectPermissionScriptBuilder.BuildGrantStatements(permissions, "[dbo].[A]");

        Assert.Equal(string.Empty, result);
    }
}
