using System.Collections.Generic;

namespace SqlPhanos.Services;

/// <summary>
/// Maps sys.objects.type_desc values (USER_TABLE, SQL_STORED_PROCEDURE, etc.) to a short,
/// human-friendly label for display in Search Results - the raw type_desc values are accurate
/// but nobody wants to read "SQL_INLINE_TABLE_VALUED_FUNCTION" in a results list.
/// </summary>
public static class SqlObjectTypeDisplayNames
{
    private static readonly Dictionary<string, string> FriendlyNamesByTypeDesc = new()
    {
        ["USER_TABLE"] = "Table",
        ["SYSTEM_TABLE"] = "Table",
        ["INTERNAL_TABLE"] = "Table (int)",
        ["VIEW"] = "View",
        ["SQL_STORED_PROCEDURE"] = "Procedure",
        ["CLR_STORED_PROCEDURE"] = "Procedure",
        ["EXTENDED_STORED_PROCEDURE"] = "Procedure",
        ["REPLICATION_FILTER_PROCEDURE"] = "Procedure",
        ["SQL_SCALAR_FUNCTION"] = "Function",
        ["SQL_TABLE_VALUED_FUNCTION"] = "Function",
        ["SQL_INLINE_TABLE_VALUED_FUNCTION"] = "Function",
        ["CLR_SCALAR_FUNCTION"] = "Function",
        ["CLR_TABLE_VALUED_FUNCTION"] = "Function",
        ["AGGREGATE_FUNCTION"] = "Function",
        ["SQL_TRIGGER"] = "Trigger",
        ["CLR_TRIGGER"] = "Trigger",
        ["CHECK_CONSTRAINT"] = "Constraint",
        ["DEFAULT_CONSTRAINT"] = "Constraint",
        ["FOREIGN_KEY_CONSTRAINT"] = "Constraint",
        ["PRIMARY_KEY_CONSTRAINT"] = "Constraint",
        ["UNIQUE_CONSTRAINT"] = "Constraint",
        ["RULE"] = "Rule",
        ["SYNONYM"] = "Synonym",
        ["SEQUENCE_OBJECT"] = "Sequence",
        ["SERVICE_QUEUE"] = "Service Broker Queue",
        ["PLAN_GUIDE"] = "Plan Guide",
        ["TABLE_TYPE"] = "UD Table Type",
        ["USER_DEFINED_TYPE"] = "CLR Type",
        ["USER_DEFINED_DATA_TYPE"] = "UD Data Type",
    };

    /// <summary>Returns the friendly label, or the raw type_desc unchanged if it isn't mapped.</summary>
    public static string GetFriendlyName(string typeDesc)
    {
        return FriendlyNamesByTypeDesc.TryGetValue(typeDesc, out var friendlyName)
            ? friendlyName
            : typeDesc;
    }
}
