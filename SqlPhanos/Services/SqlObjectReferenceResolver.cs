using Microsoft.Data.SqlClient;
using SqlPhanos.CodeFormatting;
using SqlPhanos.ViewModels;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SqlPhanos.Services;

public sealed record SqlObjectReferenceLookup(
    int Id,
    string? DatabaseName,
    string? SchemaName,
    string ObjectName,
    SqlObjectReferenceKind Kind);

public enum SqlObjectReferenceResolutionStatus
{
    Resolved,
    NotFound,
    Ambiguous,
    DatabaseUnavailable,
}

public sealed record SqlObjectReferenceResolution(
    SqlObjectReferenceResolutionStatus Status,
    SearchResultViewModel? Target,
    string? Detail = null);

internal sealed record SqlObjectReferenceMatch(
    SearchResultViewModel Result,
    bool IsDefaultSchema,
    bool IsDboSchema);

public sealed class SqlObjectReferenceResolver
{
    private const int MaxCandidatesPerQuery = 500;
    private const int MaxCacheEntries = 20_000;
    private static readonly TimeSpan FoundCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NotFoundCacheLifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<CatalogCacheKey, CatalogCacheEntry> _cache = new();
    private readonly SemaphoreSlim[] _databaseLocks = Enumerable
        .Range(0, 64)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();
    private readonly SemaphoreSlim _queryConcurrency = new(4, 4);
    private readonly Func<
        string,
        string,
        IReadOnlyList<SqlObjectReferenceLookup>,
        CancellationToken,
        Task<IReadOnlyDictionary<int, IReadOnlyList<SqlObjectReferenceMatch>>>> _queryDatabaseAsync;
    private readonly TimeProvider _timeProvider;

    private static readonly HashSet<string> SupportedObjectTypes = new(StringComparer.Ordinal)
    {
        "SQL_INLINE_TABLE_VALUED_FUNCTION",
        "SQL_SCALAR_FUNCTION",
        "SQL_STORED_PROCEDURE",
        "SQL_TABLE_VALUED_FUNCTION",
        "USER_TABLE",
        "VIEW",
        "SEQUENCE_OBJECT",
        "TABLE_TYPE",
        "CLR_STORED_PROCEDURE",
        "CLR_SCALAR_FUNCTION",
        "CLR_TABLE_VALUED_FUNCTION",
        "SQL_TRIGGER",
        "CLR_TRIGGER",
    };

    public SqlObjectReferenceResolver()
        : this(QueryDatabaseAsync, TimeProvider.System)
    {
    }

    internal SqlObjectReferenceResolver(
        Func<
            string,
            string,
            IReadOnlyList<SqlObjectReferenceLookup>,
            CancellationToken,
            Task<IReadOnlyDictionary<int, IReadOnlyList<SqlObjectReferenceMatch>>>> queryDatabaseAsync,
        TimeProvider timeProvider)
    {
        _queryDatabaseAsync = queryDatabaseAsync;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyDictionary<int, SqlObjectReferenceResolution>> ResolveAsync(
        string connectionString,
        string currentDatabase,
        IReadOnlyList<SqlObjectReferenceLookup> lookups,
        CancellationToken cancellationToken)
    {
        var resolutions = new Dictionary<int, SqlObjectReferenceResolution>();
        var connectionIdentity = CreateConnectionCacheIdentity(connectionString);
        var databaseTasks = lookups
            .Where(static lookup => !string.IsNullOrWhiteSpace(lookup.ObjectName))
            .GroupBy(
                lookup => string.IsNullOrWhiteSpace(lookup.DatabaseName)
                    ? currentDatabase
                    : lookup.DatabaseName!,
                StringComparer.Ordinal)
            .Select(group => ResolveDatabaseAsync(
                connectionString,
                connectionIdentity,
                group.Key,
                group.ToArray(),
                cancellationToken))
            .ToArray();

        foreach (var databaseResolutions in await Task.WhenAll(databaseTasks))
        {
            foreach (var resolution in databaseResolutions)
            {
                resolutions.Add(resolution.Key, resolution.Value);
            }
        }

        return resolutions;
    }

    private async Task<IReadOnlyDictionary<int, SqlObjectReferenceResolution>> ResolveDatabaseAsync(
        string connectionString,
        string connectionIdentity,
        string databaseName,
        IReadOnlyList<SqlObjectReferenceLookup> lookups,
        CancellationToken cancellationToken)
    {
        var uniqueLookups = lookups
            .GroupBy(CreateObjectLookupKey)
            .ToDictionary(group => group.Key, group => group.First());
        var databaseKey = new DatabaseCacheKey(connectionIdentity, databaseName);
        var databaseLock = _databaseLocks[
            (databaseKey.GetHashCode() & int.MaxValue) % _databaseLocks.Length];
        var failures = new Dictionary<ObjectLookupKey, string>();

        await databaseLock.WaitAsync(cancellationToken);
        try
        {
            var missing = uniqueLookups
                .Where(pair => !TryGetCached(
                    new CatalogCacheKey(databaseKey, pair.Key),
                    out _))
                .ToArray();

            for (var start = 0; start < missing.Length; start += MaxCandidatesPerQuery)
            {
                var chunk = missing
                    .Skip(start)
                    .Take(MaxCandidatesPerQuery)
                    .ToArray();
                var queryLookups = chunk
                    .Select(static pair => pair.Value)
                    .ToArray();

                try
                {
                    await _queryConcurrency.WaitAsync(cancellationToken);
                    IReadOnlyDictionary<int, IReadOnlyList<SqlObjectReferenceMatch>> matchesById;
                    try
                    {
                        matchesById = await _queryDatabaseAsync(
                            connectionString,
                            databaseName,
                            queryLookups,
                            cancellationToken);
                    }
                    finally
                    {
                        _queryConcurrency.Release();
                    }

                    foreach (var pair in chunk)
                    {
                        var matches = matchesById.TryGetValue(pair.Value.Id, out var found)
                            ? found
                            : Array.Empty<SqlObjectReferenceMatch>();
                        Cache(new CatalogCacheKey(databaseKey, pair.Key), matches);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"Reference lookup failed for database '{databaseName}': {ex}");
                    foreach (var pair in chunk)
                    {
                        failures[pair.Key] = ex.Message;
                    }
                }
            }
        }
        finally
        {
            databaseLock.Release();
        }

        var resolutions = new Dictionary<int, SqlObjectReferenceResolution>();
        foreach (var lookup in lookups)
        {
            var objectKey = CreateObjectLookupKey(lookup);
            if (!TryGetCached(new CatalogCacheKey(databaseKey, objectKey), out var matches))
            {
                resolutions[lookup.Id] = new SqlObjectReferenceResolution(
                    SqlObjectReferenceResolutionStatus.DatabaseUnavailable,
                    null,
                    failures.GetValueOrDefault(objectKey));
                continue;
            }

            var candidateMatches = matches
                .Where(match => IsCompatible(lookup.Kind, match.Result.TypeDesc))
                .ToArray();
            var target = SelectUnambiguousTarget(lookup, candidateMatches);
            resolutions[lookup.Id] = target is not null
                ? new SqlObjectReferenceResolution(
                    SqlObjectReferenceResolutionStatus.Resolved,
                    target)
                : new SqlObjectReferenceResolution(
                    !string.IsNullOrWhiteSpace(lookup.SchemaName) &&
                    candidateMatches.Length > 1
                        ? SqlObjectReferenceResolutionStatus.Ambiguous
                        : SqlObjectReferenceResolutionStatus.NotFound,
                    null);
        }

        return resolutions;
    }

    private bool TryGetCached(
        CatalogCacheKey key,
        out IReadOnlyList<SqlObjectReferenceMatch> matches)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > _timeProvider.GetUtcNow())
            {
                matches = entry.Matches;
                return true;
            }

            _cache.TryRemove(key, out _);
        }

        matches = Array.Empty<SqlObjectReferenceMatch>();
        return false;
    }

    private void Cache(
        CatalogCacheKey key,
        IReadOnlyList<SqlObjectReferenceMatch> matches)
    {
        var now = _timeProvider.GetUtcNow();
        _cache[key] = new CatalogCacheEntry(
            matches,
            now,
            now + (matches.Count == 0 ? NotFoundCacheLifetime : FoundCacheLifetime));

        if (_cache.Count <= MaxCacheEntries)
        {
            return;
        }

        foreach (var expired in _cache.Where(pair => pair.Value.ExpiresAt <= now))
        {
            _cache.TryRemove(expired.Key, out _);
        }

        var overflow = _cache.Count - MaxCacheEntries;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var oldest in _cache
                     .OrderBy(pair => pair.Value.CreatedAt)
                     .Take(overflow))
        {
            _cache.TryRemove(oldest.Key, out _);
        }
    }

    private static ObjectLookupKey CreateObjectLookupKey(SqlObjectReferenceLookup lookup) =>
        new(
            string.IsNullOrWhiteSpace(lookup.SchemaName) ? null : lookup.SchemaName,
            lookup.ObjectName);

    internal static string CreateConnectionCacheIdentity(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        builder.Remove("Password");
        builder.Remove("Initial Catalog");
        return builder.ConnectionString;
    }

    internal static SearchResultViewModel? SelectUnambiguousTarget(
        SqlObjectReferenceLookup lookup,
        IReadOnlyList<SqlObjectReferenceMatch> matches)
    {
        if (!string.IsNullOrWhiteSpace(lookup.SchemaName))
        {
            return matches.Count == 1 ? matches[0].Result : null;
        }

        if (matches.Count == 0)
        {
            return null;
        }

        var defaultSchemaMatches = matches
            .Where(static match => match.IsDefaultSchema)
            .ToArray();
        if (defaultSchemaMatches.Length == 1)
        {
            return defaultSchemaMatches[0].Result;
        }

        var dboMatches = matches
            .Where(static match => match.IsDboSchema)
            .ToArray();
        return dboMatches.Length == 1 ? dboMatches[0].Result : null;
    }

    internal static bool IsCompatible(SqlObjectReferenceKind kind, string typeDesc) =>
        kind switch
        {
            SqlObjectReferenceKind.Any =>
                SupportedObjectTypes.Contains(typeDesc) ||
                typeDesc is "USER_DEFINED_TYPE" or "USER_DEFINED_DATA_TYPE",
            SqlObjectReferenceKind.SchemaObject =>
                SupportedObjectTypes.Contains(typeDesc),
            SqlObjectReferenceKind.Executable =>
                typeDesc is "SQL_STORED_PROCEDURE"
                    or "CLR_STORED_PROCEDURE"
                    or "SQL_SCALAR_FUNCTION"
                    or "CLR_SCALAR_FUNCTION",
            SqlObjectReferenceKind.TableOrView =>
                typeDesc is "USER_TABLE" or "VIEW",
            SqlObjectReferenceKind.Rowset =>
                typeDesc is "USER_TABLE"
                    or "VIEW"
                    or "SQL_INLINE_TABLE_VALUED_FUNCTION"
                    or "SQL_TABLE_VALUED_FUNCTION"
                    or "CLR_TABLE_VALUED_FUNCTION",
            SqlObjectReferenceKind.Procedure =>
                typeDesc is "SQL_STORED_PROCEDURE" or "CLR_STORED_PROCEDURE",
            SqlObjectReferenceKind.Function =>
                typeDesc is "SQL_INLINE_TABLE_VALUED_FUNCTION"
                    or "SQL_SCALAR_FUNCTION"
                    or "SQL_TABLE_VALUED_FUNCTION"
                    or "CLR_SCALAR_FUNCTION"
                    or "CLR_TABLE_VALUED_FUNCTION",
            SqlObjectReferenceKind.Sequence =>
                typeDesc == "SEQUENCE_OBJECT",
            SqlObjectReferenceKind.Type =>
                typeDesc is "TABLE_TYPE" or "USER_DEFINED_TYPE" or "USER_DEFINED_DATA_TYPE",
            SqlObjectReferenceKind.Trigger =>
                typeDesc is "SQL_TRIGGER" or "CLR_TRIGGER",
            _ => false,
        };

    private static async Task<IReadOnlyDictionary<int, IReadOnlyList<SqlObjectReferenceMatch>>> QueryDatabaseAsync(
        string connectionString,
        string databaseName,
        IReadOnlyList<SqlObjectReferenceLookup> lookups,
        CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = databaseName
        };

        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        BuildResolutionCommand(command, lookups);

        var results = new List<ResolvedMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        if (!await reader.NextResultAsync(cancellationToken))
        {
            return new Dictionary<int, IReadOnlyList<SqlObjectReferenceMatch>>();
        }

        var lookupIdOrdinal = reader.GetOrdinal("lookup_id");
        var serverNameOrdinal = reader.GetOrdinal("server_name");
        var databaseNameOrdinal = reader.GetOrdinal("db_name");
        var typeDescOrdinal = reader.GetOrdinal("type_desc");
        var schemaNameOrdinal = reader.GetOrdinal("schema_name");
        var objectNameOrdinal = reader.GetOrdinal("object_name");
        var parentObjectNameOrdinal = reader.GetOrdinal("parent_object_name");
        var isEncryptedOrdinal = reader.GetOrdinal("is_encrypted");
        var isDefaultSchemaOrdinal = reader.GetOrdinal("is_default_schema");
        var isDboSchemaOrdinal = reader.GetOrdinal("is_dbo_schema");
        while (await reader.ReadAsync(cancellationToken))
        {
            var typeDesc = reader.GetString(typeDescOrdinal);
            if (!SupportedObjectTypes.Contains(typeDesc) &&
                typeDesc is not "USER_DEFINED_TYPE" and not "USER_DEFINED_DATA_TYPE")
            {
                continue;
            }

            results.Add(new ResolvedMatch(
                reader.GetInt32(lookupIdOrdinal),
                new SqlObjectReferenceMatch(
                    new SearchResultViewModel
                    {
                        ServerName = reader.GetString(serverNameOrdinal),
                        DbName = reader.GetString(databaseNameOrdinal),
                        TypeDesc = typeDesc,
                        SchemaName = reader.GetString(schemaNameOrdinal),
                        ObjectName = reader.GetString(objectNameOrdinal),
                        ParentFqName = reader.IsDBNull(parentObjectNameOrdinal)
                            ? ""
                            : reader.GetString(parentObjectNameOrdinal),
                        IsEncrypted = reader.GetBoolean(isEncryptedOrdinal)
                    },
                    reader.GetBoolean(isDefaultSchemaOrdinal),
                    reader.GetBoolean(isDboSchemaOrdinal))));
        }

        return results
            .GroupBy(static result => result.LookupId)
            .ToDictionary(
                static group => group.Key,
                static group => (IReadOnlyList<SqlObjectReferenceMatch>)group
                    .Select(static result => result.Match)
                    .ToArray());
    }

    private static void BuildResolutionCommand(
        SqlCommand command,
        IReadOnlyList<SqlObjectReferenceLookup> lookups)
    {
        var values = new StringBuilder();
        for (var index = 0; index < lookups.Count; index++)
        {
            if (index > 0)
            {
                values.Append(',');
            }

            values.Append($"(@id{index}, @schema{index}, @name{index})");
            command.Parameters.Add($"@id{index}", SqlDbType.Int).Value = lookups[index].Id;
            command.Parameters.Add($"@schema{index}", SqlDbType.NVarChar, 128).Value =
                string.IsNullOrWhiteSpace(lookups[index].SchemaName)
                    ? DBNull.Value
                    : lookups[index].SchemaName;
            command.Parameters.Add($"@name{index}", SqlDbType.NVarChar, 128).Value =
                lookups[index].ObjectName;
        }

        command.CommandText = $$"""
            DECLARE @defaultSchemaName sysname = COALESCE
                (
                    (
                        SELECT principals.default_schema_name
                        FROM sys.database_principals principals
                        WHERE principals.name = USER_NAME()
                    ),
                    N'dbo'
                );
            SELECT @defaultSchemaName AS default_schema_name;

            WITH requested(lookup_id, schema_name, object_name) AS
            (
                SELECT lookup_id, schema_name, object_name
                FROM (VALUES {{values}}) values_source(lookup_id, schema_name, object_name)
            )
            SELECT requested.lookup_id
                ,@@SERVERNAME AS server_name
                ,CAST(DB_NAME() AS sysname) AS db_name
                ,CASE WHEN objects.type = 'TT' THEN 'TABLE_TYPE' ELSE objects.type_desc END AS type_desc
                ,schemas.name AS schema_name
                ,objects.name AS object_name
                ,parents.name AS parent_object_name
                ,ISNULL(CAST(OBJECTPROPERTY(objects.object_id, 'IsEncrypted') AS bit), 0) AS is_encrypted
                ,CAST(CASE WHEN schemas.name = @defaultSchemaName THEN 1 ELSE 0 END AS bit) AS is_default_schema
                ,CAST(CASE WHEN schemas.name = N'dbo' THEN 1 ELSE 0 END AS bit) AS is_dbo_schema
            FROM requested
            INNER JOIN sys.objects objects
                ON objects.name = requested.object_name
            INNER JOIN sys.schemas schemas
                ON schemas.schema_id = objects.schema_id
                AND (requested.schema_name IS NULL OR schemas.name = requested.schema_name)
            LEFT JOIN sys.objects parents
                ON parents.object_id = objects.parent_object_id
            WHERE objects.is_ms_shipped = 0
                AND objects.type IN ('U', 'V', 'P', 'PC', 'FN', 'IF', 'TF', 'FS', 'FT', 'SO', 'TT', 'TR', 'TA')

            UNION ALL

            SELECT requested.lookup_id
                ,@@SERVERNAME AS server_name
                ,CAST(DB_NAME() AS sysname) AS db_name
                ,CASE WHEN types.is_assembly_type = 1
                    THEN 'USER_DEFINED_TYPE'
                    ELSE 'USER_DEFINED_DATA_TYPE'
                 END AS type_desc
                ,schemas.name AS schema_name
                ,types.name AS object_name
                ,CAST(NULL AS sysname) AS parent_object_name
                ,CAST(0 AS bit) AS is_encrypted
                ,CAST(CASE WHEN schemas.name = @defaultSchemaName THEN 1 ELSE 0 END AS bit) AS is_default_schema
                ,CAST(CASE WHEN schemas.name = N'dbo' THEN 1 ELSE 0 END AS bit) AS is_dbo_schema
            FROM requested
            INNER JOIN sys.types types
                ON types.name = requested.object_name
                AND types.is_user_defined = 1
                AND types.is_table_type = 0
            INNER JOIN sys.schemas schemas
                ON schemas.schema_id = types.schema_id
                AND (requested.schema_name IS NULL OR schemas.name = requested.schema_name);
            """;
    }

    private sealed record ResolvedMatch(int LookupId, SqlObjectReferenceMatch Match);
    private sealed record ObjectLookupKey(string? SchemaName, string ObjectName);
    private sealed record DatabaseCacheKey(string ConnectionIdentity, string DatabaseName);
    private sealed record CatalogCacheKey(DatabaseCacheKey Database, ObjectLookupKey Object);
    private sealed record CatalogCacheEntry(
        IReadOnlyList<SqlObjectReferenceMatch> Matches,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);
}
