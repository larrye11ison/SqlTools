using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.Management.Smo;

namespace SqlPhanos.ScriptDatabases;

public static class SqlObjectFilters
{
    public static bool IsUserObject(string schema, string name)
    {
        if (EqualsIgnore(schema, "sys") || EqualsIgnore(name, "sys"))
        {
            return false;
        }

        if (EqualsIgnore(schema, "information_schema") || EqualsIgnore(name, "information_schema"))
        {
            return false;
        }

        if (EqualsIgnore(name, "dtproperties") || EqualsIgnore(name, "sysdiagrams"))
        {
            return false;
        }

        if (EqualsIgnore(schema, "guest") || EqualsIgnore(name, "guest"))
        {
            return false;
        }

        if (schema.StartsWith("db_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.StartsWith("db_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("dt_", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !EqualsIgnore(name, "fn_diagramobjects") && !Regex.IsMatch(name, "^sp.*diagram");
    }

    public static string AbbreviateObjectType(string objectTypeName)
    {
        Regex wordRegex = new("[A-Z][a-z]*");
        List<string> words = wordRegex.Matches(objectTypeName)
            .Select(match => EqualsIgnore(match.Value, "database") ? "Db" : match.Value)
            .ToList();

        if (words.Count == 0)
        {
            return "WTF";
        }

        if (words.Count == 1)
        {
            return TakePrefix(words[0], 4);
        }

        string prefix = string.Concat(words.Take(2).Select(word => TakePrefix(word, 2)));
        string suffix = words.Count > 2 ? TakePrefix(words[^1], 2) : string.Empty;
        return prefix + suffix;
    }

    public static DatabaseObjectTypes GetScriptedObjectTypes()
    {
        return DatabaseObjectTypes.ApplicationRole
            | DatabaseObjectTypes.Default
            | DatabaseObjectTypes.ExtendedStoredProcedure
            | DatabaseObjectTypes.FullTextCatalog
            | DatabaseObjectTypes.PartitionFunction
            | DatabaseObjectTypes.PartitionScheme
            | DatabaseObjectTypes.DatabaseRole
            | DatabaseObjectTypes.RemoteServiceBinding
            | DatabaseObjectTypes.Rule
            | DatabaseObjectTypes.Schema
            | DatabaseObjectTypes.StoredProcedure
            | DatabaseObjectTypes.Synonym
            | DatabaseObjectTypes.Table
            | DatabaseObjectTypes.User
            | DatabaseObjectTypes.UserDefinedAggregate
            | DatabaseObjectTypes.UserDefinedDataType
            | DatabaseObjectTypes.UserDefinedFunction
            | DatabaseObjectTypes.UserDefinedType
            | DatabaseObjectTypes.View
            | DatabaseObjectTypes.XmlSchemaCollection
            | DatabaseObjectTypes.SymmetricKey
            | DatabaseObjectTypes.Certificate
            | DatabaseObjectTypes.AsymmetricKey
            | DatabaseObjectTypes.UserDefinedTableTypes
            | DatabaseObjectTypes.PlanGuide
            | DatabaseObjectTypes.DatabaseEncryptionKey
            | DatabaseObjectTypes.DatabaseAuditSpecification
            | DatabaseObjectTypes.FullTextStopList
            | DatabaseObjectTypes.SearchPropertyList
            | DatabaseObjectTypes.Sequence
            | DatabaseObjectTypes.SecurityPolicy
            | DatabaseObjectTypes.ExternalDataSource
            | DatabaseObjectTypes.ExternalFileFormat
            | DatabaseObjectTypes.ColumnMasterKey
            | DatabaseObjectTypes.ColumnEncryptionKey
            | DatabaseObjectTypes.DatabaseScopedCredential
            | DatabaseObjectTypes.ExternalLibrary
            | DatabaseObjectTypes.ExternalLanguage
            | DatabaseObjectTypes.ExternalStream
            | DatabaseObjectTypes.ExternalStreamingJob;
    }

    private static bool EqualsIgnore(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string TakePrefix(string input, int length) =>
        input.Length <= length ? input : input[..length];
}
