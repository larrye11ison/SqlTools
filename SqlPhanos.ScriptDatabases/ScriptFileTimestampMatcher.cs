using System;
using System.Globalization;
using System.IO;

namespace SqlPhanos.ScriptDatabases;

/// <summary>
/// Delta matching uses a first-line metadata comment with full DateTime tick fidelity.
/// Filesystem Created/Modified times are still applied for explorer convenience.
/// </summary>
public static class ScriptFileTimestampMatcher
{
    private const string CommentPrefix = "-- ";

    public static bool IsUnchanged(string scriptFilePath, ObjectDates dates)
    {
        if (!dates.Created.HasValue || !dates.LastModified.HasValue)
        {
            return false;
        }

        if (!TryReadMetadataDates(scriptFilePath, out DateTime stampedCreated, out DateTime stampedModified))
        {
            return false;
        }

        return stampedCreated.Ticks == dates.Created.Value.Ticks
            && stampedModified.Ticks == dates.LastModified.Value.Ticks;
    }

    public static string? BuildMetadataComment(ObjectDates dates)
    {
        if (!dates.Created.HasValue || !dates.LastModified.HasValue)
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{CommentPrefix}{dates.Created.Value.Ticks}/{dates.LastModified.Value.Ticks}");
    }

    public static bool TryReadMetadataDates(
        string scriptFilePath,
        out DateTime created,
        out DateTime lastModified)
    {
        created = default;
        lastModified = default;
        if (!File.Exists(scriptFilePath))
        {
            return false;
        }

        using StreamReader reader = new(scriptFilePath);
        string? firstLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(firstLine)
            || !firstLine.StartsWith(CommentPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string payload = firstLine[CommentPrefix.Length..];
        int separatorIndex = payload.IndexOf('/');
        if (separatorIndex <= 0 || separatorIndex >= payload.Length - 1)
        {
            return false;
        }

        if (!long.TryParse(
                payload.AsSpan(0, separatorIndex),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long createdTicks)
            || !long.TryParse(
                payload.AsSpan(separatorIndex + 1),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long modifiedTicks))
        {
            return false;
        }

        try
        {
            created = new DateTime(createdTicks, DateTimeKind.Unspecified);
            lastModified = new DateTime(modifiedTicks, DateTimeKind.Unspecified);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public static void ApplyObjectDates(string scriptFilePath, ObjectDates dates)
    {
        if (!File.Exists(scriptFilePath))
        {
            return;
        }

        if (dates.Created.HasValue)
        {
            File.SetCreationTime(scriptFilePath, ToUnspecifiedSecond(dates.Created.Value));
        }

        if (dates.LastModified.HasValue)
        {
            File.SetLastWriteTime(scriptFilePath, ToUnspecifiedSecond(dates.LastModified.Value));
        }
    }

    public static DateTime RoundToSecond(DateTime value)
    {
        return ToUnspecifiedSecond(value);
    }

    private static DateTime ToUnspecifiedSecond(DateTime value)
    {
        DateTime wall = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        return new DateTime(
            wall.Year,
            wall.Month,
            wall.Day,
            wall.Hour,
            wall.Minute,
            wall.Second,
            DateTimeKind.Unspecified);
    }
}
