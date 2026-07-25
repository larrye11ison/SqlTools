using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.SqlServer.Management.Smo;

namespace SqlPhanos.ScriptDatabases;

public sealed class ObjectCatalogue
{
    private readonly Dictionary<string, ObjectDates> _datesBySchemaAndName;

    private ObjectCatalogue(
        string actualServerName,
        string actualDatabaseName,
        Dictionary<string, ObjectDates> datesBySchemaAndName)
    {
        ActualServerName = actualServerName;
        ActualDatabaseName = actualDatabaseName;
        _datesBySchemaAndName = datesBySchemaAndName;
    }

    public string ActualServerName { get; }

    public string ActualDatabaseName { get; }

    public static ObjectCatalogue LoadFromDatabase(Database database)
    {
        // OBJECTPROPERTY IsEncrypted works across SQL Server editions; sys.sql_modules.is_encrypted does not.
        DataSet results = database.ExecuteWithResults(
            """
            SELECT
                CAST(@@SERVERNAME AS sysname) AS ActualServerName,
                CAST(DB_NAME() AS sysname) AS ActualDatabaseName,
                OBJECT_SCHEMA_NAME(o.object_id) AS SchemaName,
                o.name AS ObjectName,
                o.create_date AS CreateDate,
                o.modify_date AS ModifyDate,
                CAST(ISNULL(OBJECTPROPERTY(o.object_id, 'IsEncrypted'), 0) AS bit) AS IsEncrypted
            FROM sys.objects AS o
            WHERE o.is_ms_shipped = 0;
            """);

        Dictionary<string, ObjectDates> datesBySchemaAndName = new(StringComparer.OrdinalIgnoreCase);
        string actualServerName = string.Empty;
        string actualDatabaseName = string.Empty;

        if (results.Tables.Count > 0 && results.Tables[0].Rows.Count > 0)
        {
            foreach (DataRow row in results.Tables[0].Rows)
            {
                if (string.IsNullOrEmpty(actualServerName))
                {
                    actualServerName = row["ActualServerName"] as string ?? string.Empty;
                    actualDatabaseName = row["ActualDatabaseName"] as string ?? string.Empty;
                }

                string schema = row["SchemaName"] as string ?? string.Empty;
                string objectName = row["ObjectName"] as string ?? string.Empty;
                if (string.IsNullOrWhiteSpace(objectName))
                {
                    continue;
                }

                datesBySchemaAndName[BuildLookupKey(schema, objectName)] = new ObjectDates(
                    ReadDate(row["CreateDate"]),
                    ReadDate(row["ModifyDate"]),
                    ReadBoolean(row["IsEncrypted"]));
            }
        }

        if (string.IsNullOrEmpty(actualServerName) || string.IsNullOrEmpty(actualDatabaseName))
        {
            (actualServerName, actualDatabaseName) = LoadServerIdentity(database);
        }

        return new ObjectCatalogue(actualServerName, actualDatabaseName, datesBySchemaAndName);
    }

    public ObjectDates LookupDates(string schema, string objectName)
    {
        return _datesBySchemaAndName.TryGetValue(BuildLookupKey(schema, objectName), out ObjectDates? dates)
            ? dates
            : ObjectDates.Empty;
    }

    public bool IsEncrypted(string schema, string objectName)
    {
        return LookupDates(schema, objectName).IsEncrypted;
    }

    public static string FormatScriptedDate(DateTimeOffset value)
    {
        return value.ToString("yyyy-MM-dd HH:mm:ss zzz");
    }

    public static string FormatCatalogueDate(DateTime? value)
    {
        return value.HasValue
            ? $"{value.Value:yyyy-MM-dd HH:mm:ss} (timezone unknown)"
            : "(none)";
    }

    public static string BuildLookupKey(string schema, string objectName)
    {
        return $"{schema}{objectName}";
    }

    private static (string ActualServerName, string ActualDatabaseName) LoadServerIdentity(Database database)
    {
        DataSet identity = database.ExecuteWithResults(
            """
            SELECT
                CAST(@@SERVERNAME AS sysname) AS ActualServerName,
                CAST(DB_NAME() AS sysname) AS ActualDatabaseName;
            """);

        if (identity.Tables.Count == 0 || identity.Tables[0].Rows.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        DataRow row = identity.Tables[0].Rows[0];
        return (
            row["ActualServerName"] as string ?? string.Empty,
            row["ActualDatabaseName"] as string ?? string.Empty);
    }

    private static DateTime? ReadDate(object value)
    {
        if (value is DateTime dateTime && dateTime > DateTime.MinValue)
        {
            return DateTime.SpecifyKind(dateTime, DateTimeKind.Unspecified);
        }

        return null;
    }

    private static bool ReadBoolean(object value)
    {
        if (value is bool flag)
        {
            return flag;
        }

        if (value is null or DBNull)
        {
            return false;
        }

        return Convert.ToBoolean(value);
    }
}

public sealed record ObjectDates(DateTime? Created, DateTime? LastModified, bool IsEncrypted = false)
{
    public static ObjectDates Empty { get; } = new(null, null, false);
}
