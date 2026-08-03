using Microsoft.Data.Sqlite;
using System.Globalization;

namespace SqlPhanos.DependencyIndex;

public sealed class DependencyIndexStore
{
    public const int CurrentSchemaVersion = 1;
    private readonly string _connectionString;

    public DependencyIndexStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
        }.ToString();
    }

    public string DatabasePath { get; }

    public static string GetDefaultPath(string applicationName = "SqlPhanos")
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            applicationName, "dependency-index.sqlite3");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);

        var version = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken),
            CultureInfo.InvariantCulture);
        if (version > CurrentSchemaVersion)
            throw new InvalidOperationException($"Dependency index schema {version} is newer than supported schema {CurrentSchemaVersion}.");
        if (version == CurrentSchemaVersion)
            return;

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, MigrationV1, cancellationToken, transaction);
            await ExecuteAsync(connection, $"PRAGMA user_version={CurrentSchemaVersion};", cancellationToken, transaction);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<long> UpsertServerAsync(
        ServerIdentity identity, DiscoveryStatus status = DiscoveryStatus.Connected,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var now = Utc(DateTimeOffset.UtcNow);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO server_endpoints
              (canonical_key,display_name,data_source,actual_server_name,machine_name,instance_name,
               engine_edition,product_version,discovery_status,first_observed_utc,last_observed_utc)
            VALUES ($key,$display,$source,$actual,$machine,$instance,$edition,$version,$status,$now,$now)
            ON CONFLICT(canonical_key) DO UPDATE SET
              display_name=excluded.display_name,data_source=excluded.data_source,
              actual_server_name=excluded.actual_server_name,machine_name=excluded.machine_name,
              instance_name=excluded.instance_name,engine_edition=excluded.engine_edition,
              product_version=excluded.product_version,discovery_status=excluded.discovery_status,
              last_observed_utc=excluded.last_observed_utc
            RETURNING server_id;
            """;
        Add(command, "$key", identity.CanonicalKey);
        Add(command, "$display", identity.DisplayName);
        Add(command, "$source", identity.DataSource);
        Add(command, "$actual", identity.ActualServerName);
        Add(command, "$machine", identity.MachineName);
        Add(command, "$instance", identity.InstanceName);
        Add(command, "$edition", identity.EngineEdition);
        Add(command, "$version", identity.ProductVersion);
        Add(command, "$status", status.ToString());
        Add(command, "$now", now);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task UpsertConnectionBindingAsync(
        long serverId, string connectionProfileId, bool isPreferred,
        DateTimeOffset? lastVerifiedUtc = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionProfileId);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO connection_bindings(server_id,connection_profile_id,is_preferred,last_verified_utc)
            VALUES($server,$profile,$preferred,$verified)
            ON CONFLICT(server_id,connection_profile_id) DO UPDATE SET
              is_preferred=excluded.is_preferred,last_verified_utc=excluded.last_verified_utc;
            """;
        Add(command, "$server", serverId);
        Add(command, "$profile", connectionProfileId);
        Add(command, "$preferred", isPreferred);
        Add(command, "$verified", lastVerifiedUtc is null ? null : Utc(lastVerifiedUtc.Value));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReplaceLinkedServersAsync(
        long sourceServerId, IReadOnlyList<CatalogLinkedServer> aliases,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var alias in aliases)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO linked_server_aliases
                  (source_server_id,alias_name,product,provider,data_source,location,default_catalog,
                   is_data_access_enabled,is_rpc_out_enabled,remote_server_id,captured_utc)
                VALUES($server,$alias,$product,$provider,$source,$location,$catalog,$data,$rpc,NULL,$captured)
                ON CONFLICT(source_server_id,alias_name) DO UPDATE SET
                  product=excluded.product,provider=excluded.provider,data_source=excluded.data_source,
                  location=excluded.location,default_catalog=excluded.default_catalog,
                  is_data_access_enabled=excluded.is_data_access_enabled,
                  is_rpc_out_enabled=excluded.is_rpc_out_enabled,captured_utc=excluded.captured_utc;
                """;
            Add(command, "$server", sourceServerId);
            Add(command, "$alias", alias.AliasName);
            Add(command, "$product", alias.Product);
            Add(command, "$provider", alias.Provider);
            Add(command, "$source", alias.DataSource);
            Add(command, "$location", alias.Location);
            Add(command, "$catalog", alias.DefaultCatalog);
            Add(command, "$data", alias.IsDataAccessEnabled);
            Add(command, "$rpc", alias.IsRpcOutEnabled);
            Add(command, "$captured", Utc(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<long> BeginScanAsync(long serverId, bool isFullRebuild,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO scan_runs(server_id,started_utc,outcome,is_full_rebuild)
            VALUES($server,$started,'Running',$full) RETURNING scan_id;
            """;
        Add(command, "$server", serverId);
        Add(command, "$started", Utc(DateTimeOffset.UtcNow));
        Add(command, "$full", isFullRebuild);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    public async Task CompleteScanAsync(long scanId, ScanOutcome outcome, int objects, int edges,
        int parseFailures, int inaccessibleDatabases, string? error = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE scan_runs SET completed_utc=$completed,outcome=$outcome,object_count=$objects,
              edge_count=$edges,parse_failure_count=$parse,inaccessible_database_count=$inaccessible,
              error_message=$error WHERE scan_id=$scan;
            """;
        Add(command, "$completed", Utc(DateTimeOffset.UtcNow));
        Add(command, "$outcome", outcome.ToString());
        Add(command, "$objects", objects);
        Add(command, "$edges", edges);
        Add(command, "$parse", parseFailures);
        Add(command, "$inaccessible", inaccessibleDatabases);
        Add(command, "$error", error);
        Add(command, "$scan", scanId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordDatabaseResultAsync(
        long scanId, int sqlDatabaseId, string databaseName, ScanOutcome outcome,
        int objects, int edges, int parseFailures, string? error,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO database_scan_results
              (scan_id,sql_database_id,database_name,started_utc,completed_utc,outcome,
               object_count,edge_count,parse_failure_count,error_message)
            VALUES($scan,$id,$name,$now,$now,$outcome,$objects,$edges,$parse,$error)
            ON CONFLICT(scan_id,sql_database_id) DO UPDATE SET completed_utc=excluded.completed_utc,
              outcome=excluded.outcome,object_count=excluded.object_count,edge_count=excluded.edge_count,
              parse_failure_count=excluded.parse_failure_count,error_message=excluded.error_message;
            """;
        Add(command, "$scan", scanId);
        Add(command, "$id", sqlDatabaseId);
        Add(command, "$name", databaseName);
        Add(command, "$now", Utc(DateTimeOffset.UtcNow));
        Add(command, "$outcome", outcome.ToString());
        Add(command, "$objects", objects);
        Add(command, "$edges", edges);
        Add(command, "$parse", parseFailures);
        Add(command, "$error", error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, string?>> GetDefinitionHashesAsync(
        long serverId, int sqlDatabaseId, CancellationToken cancellationToken = default)
        => (await GetObjectAnalysisAsync(serverId, sqlDatabaseId, cancellationToken))
            .ToDictionary(item => item.Key, item => item.Value.Hash);

    public async Task<IReadOnlyDictionary<int, ObjectAnalysisState>> GetObjectAnalysisAsync(
        long serverId, int sqlDatabaseId, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<int, ObjectAnalysisState>();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.sql_object_id,o.definition_hash,o.analysis_status,o.has_dynamic_sql,o.modify_date
            FROM schema_objects o
            JOIN databases d ON d.database_id=o.database_id
            WHERE d.server_id=$server AND d.sql_database_id=$database AND o.is_deleted=0;
            """;
        Add(command, "$server", serverId);
        Add(command, "$database", sqlDatabaseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            result.Add(reader.GetInt32(0), new ObjectAnalysisState(
                reader.IsDBNull(1) ? null : reader.GetString(1),
                Enum.Parse<AnalysisStatus>(reader.GetString(2)), reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture)));
        return result;
    }

    public async Task PromoteDatabaseSnapshotAsync(
        long serverId, long scanId, DatabaseCatalogSnapshot snapshot,
        IReadOnlyDictionary<int, ObjectAnalysisState> analyses,
        IReadOnlyCollection<int> analyzedSourceIds, IReadOnlyList<DependencyEdgeDraft> edges,
        bool fullRebuild, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var databaseId = await UpsertDatabaseAsync(connection, transaction, serverId, snapshot.Database, cancellationToken);
            var objectKeys = new Dictionary<int, long>();
            foreach (var item in snapshot.Objects)
            {
                var analysis = analyses[item.SqlObjectId];
                objectKeys[item.SqlObjectId] = await UpsertObjectAsync(
                    connection, transaction, databaseId, scanId, item, analysis, cancellationToken);
            }
            await using (var clearParents = connection.CreateCommand())
            {
                clearParents.Transaction = transaction;
                clearParents.CommandText = "UPDATE schema_objects SET parent_object_key=NULL WHERE database_id=$database;";
                Add(clearParents, "$database", databaseId);
                await clearParents.ExecuteNonQueryAsync(cancellationToken);
            }
            foreach (var item in snapshot.Objects.Where(item => item.ParentSqlObjectId is not null))
            {
                // objectKeys.GetValueOrDefault used to be used directly as the parameter here -
                // for a parent object_id this scan never captured (same read-query-drift the
                // structural-edge fix accounts for), that silently defaulted to 0, and 0 is not a
                // real object_key, so the UPDATE below violated schema_objects' own self-referencing
                // FK (parent_object_key REFERENCES schema_objects(object_key)). Leave
                // parent_object_key NULL (already cleared above) instead of writing an invalid key.
                if (!objectKeys.TryGetValue(item.ParentSqlObjectId!.Value, out var parentKey))
                {
                    continue;
                }

                await using var parent = connection.CreateCommand();
                parent.Transaction = transaction;
                parent.CommandText = """
                    UPDATE schema_objects SET parent_object_key=$parent
                    WHERE database_id=$database AND sql_object_id=$object;
                    """;
                Add(parent, "$parent", parentKey);
                Add(parent, "$database", databaseId);
                Add(parent, "$object", item.SqlObjectId);
                await parent.ExecuteNonQueryAsync(cancellationToken);
            }

            await MarkDeletedObjectsAsync(connection, transaction, databaseId, scanId, objectKeys.Keys, cancellationToken);
            await using (var detachDeletedTargets = connection.CreateCommand())
            {
                detachDeletedTargets.Transaction = transaction;
                detachDeletedTargets.CommandText = """
                    UPDATE dependency_edges SET target_object_key=NULL,resolution_status='NotFound'
                    WHERE target_object_key IN
                      (SELECT object_key FROM schema_objects WHERE database_id=$database AND is_deleted=1);
                    """;
                Add(detachDeletedTargets, "$database", databaseId);
                await detachDeletedTargets.ExecuteNonQueryAsync(cancellationToken);
            }

            if (fullRebuild)
                await DeleteAllOutgoingEdgesAsync(connection, transaction, databaseId, cancellationToken);
            else
                await DeleteChangedOutgoingEdgesAsync(connection, transaction, databaseId, analyzedSourceIds, cancellationToken);

            foreach (var edge in edges)
                await InsertEdgeAsync(connection, transaction, serverId, scanId, objectKeys, edge, cancellationToken);

            await using (var touch = connection.CreateCommand())
            {
                touch.Transaction = transaction;
                touch.CommandText = """
                    UPDATE dependency_edges SET last_seen_scan_id=$scan
                    WHERE source_object_key IN
                      (SELECT object_key FROM schema_objects WHERE database_id=$database AND is_deleted=0);
                    """;
                Add(touch, "$scan", scanId);
                Add(touch, "$database", databaseId);
                await touch.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var updateDatabase = connection.CreateCommand())
            {
                updateDatabase.Transaction = transaction;
                updateDatabase.CommandText = "UPDATE databases SET last_successful_scan_id=$scan WHERE database_id=$database;";
                Add(updateDatabase, "$scan", scanId);
                Add(updateDatabase, "$database", databaseId);
                await updateDatabase.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    internal async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);
        return connection;
    }

    private static async Task<long> UpsertDatabaseAsync(SqliteConnection connection,
        SqliteTransaction transaction, long serverId, CatalogDatabase database, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO databases(server_id,sql_database_id,name,collation_name,state_desc,is_accessible)
            VALUES($server,$id,$name,$collation,$state,$accessible)
            ON CONFLICT(server_id,sql_database_id) DO UPDATE SET name=excluded.name,
              collation_name=excluded.collation_name,state_desc=excluded.state_desc,
              is_accessible=excluded.is_accessible
            RETURNING database_id;
            """;
        Add(command, "$server", serverId);
        Add(command, "$id", database.SqlDatabaseId);
        Add(command, "$name", database.Name);
        Add(command, "$collation", database.CollationName);
        Add(command, "$state", database.StateDescription);
        Add(command, "$accessible", database.IsAccessible);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    private static async Task<long> UpsertObjectAsync(SqliteConnection connection,
        SqliteTransaction transaction, long databaseId, long scanId, CatalogObject item,
        ObjectAnalysisState analysis, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schema_objects
              (database_id,sql_object_id,schema_name,object_name,type_desc,parent_object_key,
               is_encrypted,create_date,modify_date,definition_hash,analysis_status,has_dynamic_sql,
               is_deleted,last_seen_scan_id)
            VALUES($database,$id,$schema,$name,$type,NULL,$encrypted,$create,$modify,$hash,$status,$dynamic,0,$scan)
            ON CONFLICT(database_id,sql_object_id) DO UPDATE SET
              schema_name=excluded.schema_name,object_name=excluded.object_name,type_desc=excluded.type_desc,
              is_encrypted=excluded.is_encrypted,create_date=excluded.create_date,
              modify_date=excluded.modify_date,definition_hash=excluded.definition_hash,
              analysis_status=excluded.analysis_status,has_dynamic_sql=excluded.has_dynamic_sql,
              is_deleted=0,last_seen_scan_id=excluded.last_seen_scan_id
            RETURNING object_key;
            """;
        Add(command, "$database", databaseId);
        Add(command, "$id", item.SqlObjectId);
        Add(command, "$schema", item.SchemaName);
        Add(command, "$name", item.Name);
        Add(command, "$type", item.TypeDescription);
        Add(command, "$encrypted", item.IsEncrypted);
        Add(command, "$create", item.CreateDate is null ? null : Utc(item.CreateDate.Value));
        Add(command, "$modify", item.ModifyDate is null ? null : Utc(item.ModifyDate.Value));
        Add(command, "$hash", analysis.Hash);
        Add(command, "$status", analysis.Status.ToString());
        Add(command, "$dynamic", analysis.Dynamic);
        Add(command, "$scan", scanId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    private static async Task MarkDeletedObjectsAsync(SqliteConnection connection, SqliteTransaction transaction,
        long databaseId, long scanId, IEnumerable<int> ids, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var idList = ids.ToArray();
        var placeholders = new List<string>(idList.Length);
        for (var i = 0; i < idList.Length; i++)
        {
            placeholders.Add($"$id{i}");
            Add(command, $"$id{i}", idList[i]);
        }
        command.CommandText = idList.Length == 0
            ? "UPDATE schema_objects SET is_deleted=1,last_seen_scan_id=$scan WHERE database_id=$database;"
            : $"UPDATE schema_objects SET is_deleted=1,last_seen_scan_id=$scan WHERE database_id=$database AND sql_object_id NOT IN ({string.Join(',', placeholders)});";
        Add(command, "$scan", scanId);
        Add(command, "$database", databaseId);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task DeleteAllOutgoingEdgesAsync(SqliteConnection connection,
        SqliteTransaction transaction, long databaseId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM dependency_edges WHERE source_object_key IN
              (SELECT object_key FROM schema_objects WHERE database_id=$database);
            """;
        Add(command, "$database", databaseId);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task DeleteChangedOutgoingEdgesAsync(SqliteConnection connection,
        SqliteTransaction transaction, long databaseId, IReadOnlyCollection<int> sourceIds,
        CancellationToken token)
    {
        if (sourceIds.Count == 0)
            return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var placeholders = sourceIds.Select((_, i) => $"$id{i}").ToArray();
        var index = 0;
        foreach (var id in sourceIds)
            Add(command, $"$id{index++}", id);
        command.CommandText = $"""
            DELETE FROM dependency_edges WHERE source_object_key IN
              (SELECT object_key FROM schema_objects WHERE database_id=$database
               AND (sql_object_id IN ({string.Join(',', placeholders)}) OR is_deleted=1));
            """;
        Add(command, "$database", databaseId);
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task InsertEdgeAsync(SqliteConnection connection, SqliteTransaction transaction,
        long serverId, long scanId, IReadOnlyDictionary<int, long> sourceObjectKeys,
        DependencyEdgeDraft edge, CancellationToken token)
    {
        if (!sourceObjectKeys.TryGetValue(edge.SourceSqlObjectId, out var sourceKey))
            throw new InvalidOperationException($"Source object {edge.SourceSqlObjectId} is not in the promoted snapshot.");

        long? targetKey = null;
        if (edge.TargetSqlObjectId is { } targetObjectId)
        {
            await using var target = connection.CreateCommand();
            target.Transaction = transaction;
            target.CommandText = """
                SELECT o.object_key FROM schema_objects o JOIN databases d ON d.database_id=o.database_id
                WHERE d.server_id=$server AND d.sql_database_id=$database
                  AND o.sql_object_id=$object AND o.is_deleted=0;
                """;
            Add(target, "$server", serverId);
            Add(target, "$database", edge.TargetSqlDatabaseId);
            Add(target, "$object", targetObjectId);
            var value = await target.ExecuteScalarAsync(token);
            if (value is null)
            {
                if (edge.TargetSqlDatabaseId == (await GetDatabaseSqlIdAsync(
                        connection, transaction, sourceKey, token)))
                    throw new InvalidOperationException($"Resolved target {edge.TargetSqlDatabaseId}/{targetObjectId} is not indexed.");
            }
            else
                targetKey = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        long? linkedId = null;
        if (edge.TargetLinkedAlias is not null)
        {
            await using var linked = connection.CreateCommand();
            linked.Transaction = transaction;
            linked.CommandText = """
                SELECT linked_server_id FROM linked_server_aliases
                WHERE source_server_id=$server AND alias_name=$alias COLLATE NOCASE;
                """;
            Add(linked, "$server", serverId);
            Add(linked, "$alias", edge.TargetLinkedAlias);
            var value = await linked.ExecuteScalarAsync(token);
            linkedId = value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dependency_edges
              (source_object_key,target_object_key,reference_kind,classification,resolution_status,
               target_server_id,target_linked_server_id,target_database_name,target_schema_name,
               target_object_name,raw_server_part,raw_database_part,raw_schema_part,raw_object_part,
               part_count,evidence_kind,first_seen_scan_id,last_seen_scan_id)
            VALUES($source,$target,$kind,$classification,$resolution,$server,$linked,$database,$schema,
               $object,$rawServer,$rawDatabase,$rawSchema,$rawObject,$parts,$evidence,$scan,$scan)
            RETURNING edge_id;
            """;
        Add(command, "$source", sourceKey);
        Add(command, "$target", targetKey);
        Add(command, "$kind", edge.ReferenceKind);
        Add(command, "$classification", edge.Classification.ToString());
        Add(command, "$resolution", edge.ResolutionStatus.ToString());
        Add(command, "$server", edge.TargetServerId);
        Add(command, "$linked", linkedId);
        Add(command, "$database", edge.TargetDatabaseName);
        Add(command, "$schema", edge.TargetSchemaName);
        Add(command, "$object", edge.TargetObjectName);
        Add(command, "$rawServer", edge.RawServerPart);
        Add(command, "$rawDatabase", edge.RawDatabasePart);
        Add(command, "$rawSchema", edge.RawSchemaPart);
        Add(command, "$rawObject", edge.RawObjectPart);
        Add(command, "$parts", edge.PartCount);
        Add(command, "$evidence", edge.EvidenceKind.ToString());
        Add(command, "$scan", scanId);
        var edgeId = Convert.ToInt64(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);

        foreach (var occurrence in edge.Occurrences)
        {
            await using var occurrenceCommand = connection.CreateCommand();
            occurrenceCommand.Transaction = transaction;
            occurrenceCommand.CommandText = """
                INSERT INTO dependency_occurrences
                  (edge_id,start_offset,length,start_line,start_column,reference_text)
                VALUES($edge,$offset,$length,$line,$column,$text);
                """;
            Add(occurrenceCommand, "$edge", edgeId);
            Add(occurrenceCommand, "$offset", occurrence.StartOffset);
            Add(occurrenceCommand, "$length", occurrence.Length);
            Add(occurrenceCommand, "$line", occurrence.StartLine);
            Add(occurrenceCommand, "$column", occurrence.StartColumn);
            Add(occurrenceCommand, "$text", occurrence.ReferenceText);
            await occurrenceCommand.ExecuteNonQueryAsync(token);
        }
    }

    private static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    public async Task ReconcileResolvedLocalEdgesAsync(long serverId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE dependency_edges AS e
            SET target_object_key=(
              SELECT o.object_key
              FROM schema_objects o
              JOIN databases d ON d.database_id=o.database_id
              WHERE d.server_id=$server AND d.name=e.target_database_name COLLATE NOCASE
                AND o.schema_name=e.target_schema_name COLLATE NOCASE
                AND o.object_name=e.target_object_name COLLATE NOCASE AND o.is_deleted=0
              LIMIT 1)
            WHERE e.classification='Local' AND e.resolution_status='Resolved'
              AND e.target_object_key IS NULL
              AND 1=(SELECT COUNT(*)
                FROM schema_objects o
                JOIN databases d ON d.database_id=o.database_id
                WHERE d.server_id=$server AND d.name=e.target_database_name COLLATE NOCASE
                  AND o.schema_name=e.target_schema_name COLLATE NOCASE
                  AND o.object_name=e.target_object_name COLLATE NOCASE AND o.is_deleted=0);
            """;
        Add(command, "$server", serverId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<int> GetDatabaseSqlIdAsync(SqliteConnection connection,
        SqliteTransaction transaction, long objectKey, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT d.sql_database_id FROM schema_objects o
            JOIN databases d ON d.database_id=o.database_id WHERE o.object_key=$object;
            """;
        Add(command, "$object", objectKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture);
    }

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql,
        CancellationToken token, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(token);
    }

    private const string MigrationV1 = """
        CREATE TABLE server_endpoints(
          server_id INTEGER PRIMARY KEY, canonical_key TEXT UNIQUE, display_name TEXT NOT NULL,
          data_source TEXT, actual_server_name TEXT, machine_name TEXT, instance_name TEXT,
          engine_edition INTEGER, product_version TEXT, discovery_status TEXT NOT NULL,
          first_observed_utc TEXT NOT NULL, last_observed_utc TEXT NOT NULL);
        CREATE TABLE connection_bindings(
          binding_id INTEGER PRIMARY KEY, server_id INTEGER NOT NULL REFERENCES server_endpoints(server_id) ON DELETE CASCADE,
          connection_profile_id TEXT NOT NULL, is_preferred INTEGER NOT NULL DEFAULT 0,
          last_verified_utc TEXT, UNIQUE(server_id,connection_profile_id));
        CREATE TABLE linked_server_aliases(
          linked_server_id INTEGER PRIMARY KEY, source_server_id INTEGER NOT NULL REFERENCES server_endpoints(server_id) ON DELETE CASCADE,
          alias_name TEXT NOT NULL COLLATE NOCASE, product TEXT, provider TEXT, data_source TEXT,
          location TEXT, default_catalog TEXT, is_data_access_enabled INTEGER NOT NULL,
          is_rpc_out_enabled INTEGER NOT NULL, remote_server_id INTEGER REFERENCES server_endpoints(server_id),
          captured_utc TEXT NOT NULL, UNIQUE(source_server_id,alias_name));
        CREATE TABLE scan_runs(
          scan_id INTEGER PRIMARY KEY, server_id INTEGER NOT NULL REFERENCES server_endpoints(server_id),
          started_utc TEXT NOT NULL, completed_utc TEXT, outcome TEXT NOT NULL,
          is_full_rebuild INTEGER NOT NULL, object_count INTEGER NOT NULL DEFAULT 0,
          edge_count INTEGER NOT NULL DEFAULT 0, parse_failure_count INTEGER NOT NULL DEFAULT 0,
          inaccessible_database_count INTEGER NOT NULL DEFAULT 0, error_message TEXT);
        CREATE TABLE database_scan_results(
          database_result_id INTEGER PRIMARY KEY, scan_id INTEGER NOT NULL REFERENCES scan_runs(scan_id) ON DELETE CASCADE,
          sql_database_id INTEGER NOT NULL, database_name TEXT NOT NULL, started_utc TEXT NOT NULL,
          completed_utc TEXT, outcome TEXT NOT NULL, object_count INTEGER NOT NULL DEFAULT 0,
          edge_count INTEGER NOT NULL DEFAULT 0, parse_failure_count INTEGER NOT NULL DEFAULT 0,
          error_message TEXT, UNIQUE(scan_id,sql_database_id));
        CREATE TABLE databases(
          database_id INTEGER PRIMARY KEY, server_id INTEGER NOT NULL REFERENCES server_endpoints(server_id) ON DELETE CASCADE,
          sql_database_id INTEGER NOT NULL, name TEXT NOT NULL COLLATE NOCASE, collation_name TEXT,
          state_desc TEXT NOT NULL, is_accessible INTEGER NOT NULL,
          last_successful_scan_id INTEGER REFERENCES scan_runs(scan_id),
          UNIQUE(server_id,sql_database_id));
        CREATE TABLE schema_objects(
          object_key INTEGER PRIMARY KEY, database_id INTEGER NOT NULL REFERENCES databases(database_id) ON DELETE CASCADE,
          sql_object_id INTEGER NOT NULL, schema_name TEXT NOT NULL COLLATE NOCASE,
          object_name TEXT NOT NULL COLLATE NOCASE, type_desc TEXT NOT NULL,
          parent_object_key INTEGER REFERENCES schema_objects(object_key), is_encrypted INTEGER NOT NULL,
          create_date TEXT, modify_date TEXT, definition_hash TEXT, analysis_status TEXT NOT NULL,
          has_dynamic_sql INTEGER NOT NULL DEFAULT 0, is_deleted INTEGER NOT NULL DEFAULT 0,
          last_seen_scan_id INTEGER NOT NULL REFERENCES scan_runs(scan_id),
          UNIQUE(database_id,sql_object_id));
        CREATE TABLE dependency_edges(
          edge_id INTEGER PRIMARY KEY, source_object_key INTEGER NOT NULL REFERENCES schema_objects(object_key) ON DELETE CASCADE,
          target_object_key INTEGER REFERENCES schema_objects(object_key), reference_kind TEXT NOT NULL,
          classification TEXT NOT NULL, resolution_status TEXT NOT NULL,
          target_server_id INTEGER REFERENCES server_endpoints(server_id),
          target_linked_server_id INTEGER REFERENCES linked_server_aliases(linked_server_id),
          target_database_name TEXT, target_schema_name TEXT, target_object_name TEXT NOT NULL,
          raw_server_part TEXT, raw_database_part TEXT, raw_schema_part TEXT, raw_object_part TEXT NOT NULL,
          part_count INTEGER NOT NULL CHECK(part_count BETWEEN 1 AND 4), evidence_kind TEXT NOT NULL,
          first_seen_scan_id INTEGER NOT NULL REFERENCES scan_runs(scan_id),
          last_seen_scan_id INTEGER NOT NULL REFERENCES scan_runs(scan_id));
        CREATE TABLE dependency_occurrences(
          occurrence_id INTEGER PRIMARY KEY, edge_id INTEGER NOT NULL REFERENCES dependency_edges(edge_id) ON DELETE CASCADE,
          start_offset INTEGER NOT NULL, length INTEGER NOT NULL, start_line INTEGER NOT NULL,
          start_column INTEGER NOT NULL, reference_text TEXT NOT NULL);
        CREATE INDEX ix_edges_target_source ON dependency_edges(target_object_key,source_object_key);
        CREATE INDEX ix_edges_source_target ON dependency_edges(source_object_key,target_object_key);
        CREATE INDEX ix_edges_linked_target ON dependency_edges(target_linked_server_id,target_database_name,target_schema_name,target_object_name);
        CREATE INDEX ix_objects_name ON schema_objects(database_id,schema_name,object_name);
        """;
}
