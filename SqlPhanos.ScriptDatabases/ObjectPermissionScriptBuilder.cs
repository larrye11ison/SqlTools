using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.Management.Smo;

namespace SqlPhanos.ScriptDatabases;

/// <summary>
/// A lightweight, publicly-constructible mirror of the pieces of SMO's ObjectPermissionInfo this
/// needs - ObjectPermissionInfo/PermissionInfo's own constructor and property setters are all
/// internal to SMO (only populated from a live connection), so real instances can't be built or
/// asserted against in a test without one. This DTO exists purely so the GRANT/DENY text
/// generation below can be unit tested without a live database.
/// </summary>
public readonly record struct GrantedObjectPermission(
    string Grantee,
    PermissionGrantState State,
    string? ColumnName,
    IReadOnlyList<string> PermissionKeywords);

public enum PermissionGrantState
{
    Grant,
    GrantWithGrantOption,
    Deny
}

/// <summary>
/// Builds explicit GRANT/DENY statements for a scriptable object's own permissions.
///
/// ScriptingOptions.Permissions (see ScriptingOptionsFactory) is set on every Scripter this app
/// creates, but it's a documented no-op for the per-object Scripter.Script(Urn[]) path both
/// scripting entry points use: a permission isn't a named, independently identifiable object -
/// it has no Urn of its own - and Scripter only knows how to script Urn-identified objects.
/// The option only actually produces GRANT text when scripting an entire database via a
/// different SMO code path (Database.Script / Transfer) that this app doesn't use.
///
/// This enumerates the object's own permissions directly instead, via the IObjectPermission
/// surface every scriptable object type (Table, View, StoredProcedure, UserDefinedFunction, ...)
/// implements, and builds the equivalent GRANT/DENY text by hand - the same information SMO's
/// own (internal, unreachable from here) PermissionWorker.Script uses, just re-derived from the
/// public ObjectPermissionInfo/ObjectPermissionSet surface.
/// </summary>
public static class ObjectPermissionScriptBuilder
{
    /// <summary>
    /// Returns the extra script text (one GRANT/DENY statement per line, each followed by its
    /// own "GO" batch separator) for smoObject's own explicitly granted/denied permissions, or
    /// an empty string if it has none, isn't a permission-scriptable type, or enumerating its
    /// permissions fails for any reason - this is a best-effort addition to the object's script,
    /// and must never be the reason scripting the object itself fails.
    /// </summary>
    public static string BuildPermissionScript(SqlSmoObject smoObject)
    {
        if (smoObject is not IObjectPermission permissionSource)
        {
            return string.Empty;
        }

        string? qualifiedName = QualifiedNameOf(smoObject);
        if (qualifiedName is null)
        {
            return string.Empty;
        }

        ObjectPermissionInfo[] infos;
        try
        {
            infos = permissionSource.EnumObjectPermissions();
        }
        catch (Exception)
        {
            return string.Empty;
        }

        if (infos.Length == 0)
        {
            return string.Empty;
        }

        var permissions = infos
            .Where(info => info.PermissionState != PermissionState.Revoke)
            .GroupBy(info => (info.Grantee, info.PermissionState, info.ColumnName))
            .Select(group => new GrantedObjectPermission(
                group.Key.Grantee,
                ToGrantState(group.Key.PermissionState),
                group.Key.ColumnName,
                CollectPermissionKeywords(group)))
            .ToList();

        return BuildGrantStatements(permissions, qualifiedName);
    }

    private static string? QualifiedNameOf(SqlSmoObject smoObject) => smoObject switch
    {
        ScriptSchemaObjectBase schemaObject => $"[{schemaObject.Schema}].[{schemaObject.Name}]",
        NamedSmoObject named => $"[{named.Name}]",
        _ => null
    };

    private static PermissionGrantState ToGrantState(PermissionState state) => state switch
    {
        PermissionState.GrantWithGrant => PermissionGrantState.GrantWithGrantOption,
        PermissionState.Deny => PermissionGrantState.Deny,
        _ => PermissionGrantState.Grant
    };

    private static List<string> CollectPermissionKeywords(IEnumerable<ObjectPermissionInfo> infos)
    {
        var keywords = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var info in infos)
        {
            foreach (var keyword in PermissionKeywordsOf(info.PermissionType))
            {
                keywords.Add(keyword);
            }
        }

        return keywords.ToList();
    }

    // Alphabetical by property, matching the "add new values here, in alphabetical order"
    // convention SMO's own ObjectPermissionSet source follows for its property list.
    private static IEnumerable<string> PermissionKeywordsOf(ObjectPermissionSet set)
    {
        if (set.Alter)
        {
            yield return "ALTER";
        }

        if (set.Connect)
        {
            yield return "CONNECT";
        }

        if (set.Control)
        {
            yield return "CONTROL";
        }

        if (set.CreateSequence)
        {
            yield return "CREATE SEQUENCE";
        }

        if (set.Delete)
        {
            yield return "DELETE";
        }

        if (set.Execute)
        {
            yield return "EXECUTE";
        }

        if (set.ExecuteExternalScript)
        {
            yield return "EXECUTE EXTERNAL SCRIPT";
        }

        if (set.Impersonate)
        {
            yield return "IMPERSONATE";
        }

        if (set.Insert)
        {
            yield return "INSERT";
        }

        if (set.Receive)
        {
            yield return "RECEIVE";
        }

        if (set.References)
        {
            yield return "REFERENCES";
        }

        if (set.Select)
        {
            yield return "SELECT";
        }

        if (set.Send)
        {
            yield return "SEND";
        }

        if (set.TakeOwnership)
        {
            yield return "TAKE OWNERSHIP";
        }

        if (set.Unmask)
        {
            yield return "UNMASK";
        }

        if (set.Update)
        {
            yield return "UPDATE";
        }

        if (set.ViewChangeTracking)
        {
            yield return "VIEW CHANGE TRACKING";
        }

        if (set.ViewDefinition)
        {
            yield return "VIEW DEFINITION";
        }
    }

    /// <summary>
    /// The actual text-generation logic, kept separate from SMO enumeration above so it can be
    /// unit tested with hand-built GrantedObjectPermission values - real ObjectPermissionInfo
    /// instances can't be constructed outside a live SMO connection (see that type's own doc
    /// comment).
    /// </summary>
    public static string BuildGrantStatements(IReadOnlyList<GrantedObjectPermission> permissions, string qualifiedObjectName)
    {
        if (permissions.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var permission in permissions
            .Where(p => p.PermissionKeywords.Count > 0)
            .OrderBy(p => p.Grantee, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ColumnName, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(permission.State == PermissionGrantState.Deny ? "DENY " : "GRANT ");
            sb.Append(string.Join(", ", permission.PermissionKeywords));
            sb.Append(" ON ");
            sb.Append(qualifiedObjectName);

            if (!string.IsNullOrEmpty(permission.ColumnName))
            {
                sb.Append(" ([");
                sb.Append(permission.ColumnName);
                sb.Append("])");
            }

            sb.Append(" TO [");
            sb.Append(permission.Grantee);
            sb.Append(']');

            if (permission.State == PermissionGrantState.GrantWithGrantOption)
            {
                sb.Append(" WITH GRANT OPTION");
            }

            sb.AppendLine(";");
            sb.AppendLine("GO");
        }

        return sb.ToString();
    }
}
