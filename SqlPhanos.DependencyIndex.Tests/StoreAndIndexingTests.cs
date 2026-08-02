using Microsoft.Data.Sqlite;

namespace SqlPhanos.DependencyIndex.Tests;

public sealed class StoreAndIndexingTests
{
    [Fact]
    public async Task MigrationCreatesVersionedWalForeignKeySchema()
    {
        await using var index = await TestIndex.CreateAsync();
        await using var connection = index.OpenConnection();

        Assert.Equal(1L, await ScalarAsync<long>(connection, "PRAGMA user_version;"));
        Assert.Equal("wal", await ScalarAsync<string>(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, await ScalarAsync<long>(connection, "PRAGMA foreign_keys;"));
        var tables = await ReadStringsAsync(connection,
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';");
        Assert.All(new[]
        {
            "server_endpoints", "connection_bindings", "linked_server_aliases", "databases",
            "schema_objects", "scan_runs", "database_scan_results", "dependency_edges",
            "dependency_occurrences",
        }, table => Assert.Contains(table, tables));
    }

    [Fact]
    public async Task PersistsLinkedPlaceholderAndRawFourPartTarget()
    {
        await using var index = await TestIndex.CreateAsync();
        var source = TestIndex.Object(1, "dbo", "SourceView", "VIEW",
            "CREATE VIEW dbo.SourceView AS SELECT * FROM [Link One].[Remote Db].[sales].[Order Item];");
        var snapshot = TestIndex.Snapshot([source],
            [new("Link One", "SQL Server", "MSOLEDBSQL", "remote-host", null, "Remote Db", true, false)]);

        await index.Builder.BuildAsync(snapshot);
        await index.Builder.BuildAsync(snapshot);
        var sourceDto = Assert.Single(await index.Graph.SearchObjectsAsync("SourceView"));
        var graph = await index.Graph.GetDirectAsync(sourceDto.ObjectKey, GraphDirection.Uses);
        var edge = Assert.Single(graph.Edges).Edge;

        Assert.Equal(ReferenceClassification.LinkedServer, edge.Classification);
        Assert.Equal(ResolutionStatus.ExternalUnmapped, edge.ResolutionStatus);
        Assert.NotNull(edge.TargetLinkedServerId);
        Assert.Null(edge.TargetServerId);
        Assert.Equal(("Link One", "Remote Db", "sales", "Order Item", 4),
            (edge.RawServerPart, edge.RawDatabasePart, edge.RawSchemaPart, edge.RawObjectPart, edge.PartCount));

        await using var connection = index.OpenConnection();
        Assert.Equal(0L, await ScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM linked_server_aliases WHERE remote_server_id IS NOT NULL;"));
    }

    [Fact]
    public async Task ResolvesAgainstCompleteCrossDatabaseSnapshotAndPreservesAmbiguity()
    {
        await using var index = await TestIndex.CreateAsync();
        var main = new CatalogDatabase(5, "MainDb", null, "ONLINE", true);
        var reporting = new CatalogDatabase(6, "Reporting", null, "ONLINE", true);
        var databases = new[]
        {
            new DatabaseCatalogSnapshot(main,
            [
                TestIndex.Object(1, "dbo", "SourceView", "SQL_STORED_PROCEDURE",
                    "CREATE PROCEDURE dbo.SourceView AS SELECT * FROM Reporting.sales.TargetView; SELECT * FROM SharedName;"),
                TestIndex.Object(2, "one", "SharedName", "VIEW", null),
                TestIndex.Object(3, "two", "SharedName", "VIEW", null),
            ], [], [], [], ScanOutcome.Succeeded),
            new DatabaseCatalogSnapshot(reporting,
            [
                TestIndex.Object(10, "sales", "TargetView", "VIEW", null),
            ], [], [], [], ScanOutcome.Succeeded),
        };

        await index.Builder.BuildAsync(TestIndex.Snapshot(databases: databases));
        var source = Assert.Single(await index.Graph.SearchObjectsAsync("SourceView"));
        var graph = await index.Graph.GetDirectAsync(source.ObjectKey, GraphDirection.Uses);

        Assert.Contains(graph.Edges, edge => edge.Edge.TargetDatabaseName == "Reporting" &&
            edge.Edge.ResolutionStatus == ResolutionStatus.Resolved);
        Assert.Contains(graph.Edges, edge => edge.Edge.TargetObjectName == "SharedName" &&
            edge.Edge.ResolutionStatus == ResolutionStatus.Ambiguous);
    }

    [Fact]
    public async Task IncrementalBuildRetainsEdgesForUnchangedDefinitions()
    {
        await using var index = await TestIndex.CreateAsync();
        var snapshot = TestIndex.Snapshot();
        var first = await index.Builder.BuildAsync(snapshot);
        var second = await index.Builder.BuildAsync(snapshot);
        var source = Assert.Single(await index.Graph.SearchObjectsAsync("ViewA"));
        var graph = await index.Graph.GetDirectAsync(source.ObjectKey, GraphDirection.Uses);

        Assert.Equal(0, second.EdgesIndexed);
        Assert.Single(graph.Edges);
        Assert.Equal("ViewB", graph.Edges[0].Edge.TargetObjectName);
        Assert.Equal(AnalysisStatus.Complete, source.AnalysisStatus);

        var rebuilt = await index.Builder.BuildAsync(snapshot, new IndexBuildOptions(FullRebuild: true));
        Assert.Equal(first.EdgesIndexed, rebuilt.EdgesIndexed);
    }

    [Fact]
    public async Task FailedOrCancelledPromotionPreservesLastGoodSnapshot()
    {
        await using var index = await TestIndex.CreateAsync();
        var snapshot = TestIndex.Snapshot();
        await index.Builder.BuildAsync(snapshot);
        var sourceBefore = Assert.Single(await index.Graph.SearchObjectsAsync("ViewA"));
        Assert.Equal("ViewB", Assert.Single(
            (await index.Graph.GetDirectAsync(sourceBefore.ObjectKey, GraphDirection.Uses)).Edges).Edge.TargetObjectName);

        var serverId = await index.Store.UpsertServerAsync(snapshot.Identity);
        var scanId = await index.Store.BeginScanAsync(serverId, false);
        var database = snapshot.Databases[0];
        var analyses = database.Objects.ToDictionary(item => item.SqlObjectId,
            item => new ObjectAnalysisState("changed", AnalysisStatus.Complete, false, item.ModifyDate));
        var invalidEdge = new DependencyEdgeDraft(1, 999, 5, "TableOrView",
            ReferenceClassification.Local, ResolutionStatus.Resolved, serverId, null,
            "MainDb", "dbo", "DoesNotExist", null, null, "dbo", "DoesNotExist", 2,
            EvidenceKind.ScriptDom, []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => index.Store.PromoteDatabaseSnapshotAsync(
            serverId, scanId, database, analyses, [1], [invalidEdge], false));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.Store.PromoteDatabaseSnapshotAsync(
            serverId, scanId, database, analyses, [1], [], false, cancelled.Token));

        var sourceAfter = Assert.Single(await index.Graph.SearchObjectsAsync("ViewA"));
        Assert.Equal("ViewB", Assert.Single(
            (await index.Graph.GetDirectAsync(sourceAfter.ObjectKey, GraphDirection.Uses)).Edges).Edge.TargetObjectName);
    }

    [Fact]
    public async Task ChangedDatabaseAtomicallyReplacesEdgesAndMarksMissingObjectsDeleted()
    {
        await using var index = await TestIndex.CreateAsync();
        await index.Builder.BuildAsync(TestIndex.Snapshot(
        [
            TestIndex.Object(1, "dbo", "ViewA", "VIEW",
                "CREATE VIEW dbo.ViewA AS SELECT * FROM dbo.ViewB;"),
            TestIndex.Object(2, "dbo", "ViewB", "VIEW", null),
            TestIndex.Object(3, "dbo", "ViewC", "VIEW", null),
        ]));

        await index.Builder.BuildAsync(TestIndex.Snapshot(
        [
            TestIndex.Object(1, "dbo", "ViewA", "VIEW",
                "CREATE VIEW dbo.ViewA AS SELECT * FROM dbo.ViewC;"),
            TestIndex.Object(3, "dbo", "ViewC", "VIEW", null),
        ]));

        var source = Assert.Single(await index.Graph.SearchObjectsAsync("ViewA"));
        var edge = Assert.Single((await index.Graph.GetDirectAsync(source.ObjectKey, GraphDirection.Uses)).Edges);
        Assert.Equal("ViewC", edge.Edge.TargetObjectName);
        Assert.Empty(await index.Graph.SearchObjectsAsync("ViewB"));
    }

    [Fact]
    public async Task PersistenceSchemaCannotStoreSecretsOrProviderStrings()
    {
        await using var index = await TestIndex.CreateAsync();
        _ = new SqlServerCatalogCollector(
            "Server=localhost;Database=master;User ID=sample;Password=HighlySecretValue;TrustServerCertificate=True");
        var snapshot = TestIndex.Snapshot(linked:
            [new("Alias", "SQL Server", "MSOLEDBSQL", "host", null, null, true, true)]);
        var serverId = await index.Store.UpsertServerAsync(snapshot.Identity);
        await index.Store.UpsertConnectionBindingAsync(serverId, "profile-id", true);
        await index.Store.ReplaceLinkedServersAsync(serverId, snapshot.LinkedServers);

        await using (var connection = index.OpenConnection())
        {
            var schema = await ScalarAsync<string>(connection,
                "SELECT group_concat(sql) FROM sqlite_master WHERE sql IS NOT NULL;");
            Assert.DoesNotContain("provider_string", schema, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", schema, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("connection_string", schema, StringComparison.OrdinalIgnoreCase);
        }
        SqliteConnection.ClearAllPools();
        Assert.DoesNotContain("HighlySecretValue", File.ReadAllText(index.Store.DatabasePath));
    }

    [Fact]
    public async Task CatalogStructuralFactsAndCoverageRemainVisible()
    {
        await using var index = await TestIndex.CreateAsync();
        var database = new CatalogDatabase(5, "MainDb", null, "ONLINE", true);
        var objects = new[]
        {
            TestIndex.Object(1, "dbo", "TableA", "USER_TABLE", null),
            TestIndex.Object(2, "dbo", "TableB", "USER_TABLE", null),
            TestIndex.Object(3, "dbo", "TriggerA", "SQL_TRIGGER", null, parentId: 1),
            TestIndex.Object(-4, "types", "TypeA", "USER_DEFINED_TYPE", null),
            TestIndex.Object(5, "dbo", "ProcedureA", "SQL_STORED_PROCEDURE",
                "CREATE PROCEDURE dbo.ProcedureA AS EXEC(@statement);"),
            TestIndex.Object(6, "dbo", "EncryptedProcedure", "SQL_STORED_PROCEDURE", null, encrypted: true),
            TestIndex.Object(7, "dbo", "BrokenProcedure", "SQL_STORED_PROCEDURE",
                "CREATE PROCEDURE dbo.BrokenProcedure AS SELECT * FROM ;"),
        };
        var databaseSnapshot = new DatabaseCatalogSnapshot(database, objects,
            [new CatalogForeignKey(1, 2, "FK_TableA_TableB")], [],
            [new CatalogTypeUse(5, -4, "TypeA")], ScanOutcome.Succeeded);

        await index.Builder.BuildAsync(TestIndex.Snapshot(databases: [databaseSnapshot]), new IndexBuildOptions(
            EncryptedModuleDecrypt: EncryptedModuleDecryptMode.Skip));
        var table = Assert.Single(await index.Graph.SearchObjectsAsync("TableA"));
        var trigger = Assert.Single(await index.Graph.SearchObjectsAsync("TriggerA"));
        var procedure = Assert.Single(await index.Graph.SearchObjectsAsync("ProcedureA"));
        var encrypted = Assert.Single(await index.Graph.SearchObjectsAsync("EncryptedProcedure"));
        var broken = Assert.Single(await index.Graph.SearchObjectsAsync("BrokenProcedure"));

        Assert.Equal(table.ObjectKey, trigger.ParentObjectKey);
        Assert.Equal("TableA", trigger.Parent);
        Assert.Equal(AnalysisStatus.DynamicSql, procedure.AnalysisStatus);
        Assert.True(procedure.HasDynamicSql);
        Assert.True(encrypted.Encrypted);
        Assert.Equal(AnalysisStatus.ParseError, broken.AnalysisStatus);
        Assert.Contains((await index.Graph.GetDirectAsync(table.ObjectKey, GraphDirection.Uses,
                new GraphFilter(IncludeDriDependencies: true))).Edges,
            edge => edge.Edge.EvidenceKind == EvidenceKind.ForeignKey &&
                    edge.Edge.TargetObjectName == "TableB");
        Assert.Contains((await index.Graph.GetDirectAsync(procedure.ObjectKey, GraphDirection.Uses)).Edges,
            edge => edge.Edge.EvidenceKind == EvidenceKind.Type &&
                    edge.Edge.TargetObjectName == "TypeA");
    }

    [Fact]
    public async Task InaccessibleAndFailedDatabasesReportTheirErrorMessages()
    {
        await using var index = await TestIndex.CreateAsync();
        var accessible = new CatalogDatabase(5, "MainDb", null, "ONLINE", true);
        var inaccessible = new CatalogDatabase(6, "LockedDb", null, "ONLINE", false);
        var databases = new[]
        {
            new DatabaseCatalogSnapshot(accessible,
                [TestIndex.Object(1, "dbo", "ViewA", "VIEW", null)], [], [], [], ScanOutcome.Succeeded),
            new DatabaseCatalogSnapshot(inaccessible, [], [], [], [], ScanOutcome.Inaccessible,
                "Database is not accessible to the current login."),
        };

        var result = await index.Builder.BuildAsync(TestIndex.Snapshot(databases: databases));

        Assert.Equal(1, result.DatabasesSucceeded);
        Assert.Equal(1, result.DatabasesFailed);
        var failure = Assert.Single(result.FailureDetails);
        Assert.Equal("LockedDb", failure.DatabaseName);
        Assert.Equal(ScanOutcome.Inaccessible, failure.Outcome);
        Assert.Equal("Database is not accessible to the current login.", failure.Error);
    }

    [Fact]
    public async Task BuildReportsUiProgressReturnsServerIdAndSupportsExactLookup()
    {
        await using var index = await TestIndex.CreateAsync();
        var progress = new TestIndex.RecordingProgress<IndexProgress>();
        var result = await index.Builder.BuildAsync(
            new TestIndex.ProgressCatalogCollector(TestIndex.Snapshot()), progress: progress);

        Assert.True(result.ServerId > 0);
        Assert.Contains(progress.Values, item => item.Phase == IndexProgressPhase.DiscoveringServer);
        Assert.Contains(progress.Values, item => item.Phase == IndexProgressPhase.CollectionCompleted);
        Assert.Contains(progress.Values, item => item.Phase == IndexProgressPhase.AnalyzingDatabase &&
                                                item.DatabaseName == "MainDb" &&
                                                item.TotalDatabases == 1);
        Assert.Contains(progress.Values, item => item.Phase == IndexProgressPhase.PromotingDatabase);
        Assert.Contains(progress.Values, item => item.Phase == IndexProgressPhase.Reconciling);
        Assert.Equal(IndexProgressPhase.Completed, progress.Values[^1].Phase);

        var exact = await index.Graph.FindObjectAsync(
            result.ServerId, "maindb", "DBO", "viewa", "View");
        var incompatible = await index.Graph.FindObjectAsync(
            result.ServerId, "MainDb", "dbo", "ViewA", "Procedure");

        Assert.NotNull(exact);
        Assert.Equal("VIEW", exact.Type);
        Assert.Null(incompatible);

        var cancelledProgress = new TestIndex.RecordingProgress<IndexProgress>();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.Builder.BuildAsync(
            TestIndex.Snapshot(), cancellationToken: cancellation.Token,
            progress: cancelledProgress));
        Assert.Equal(IndexProgressPhase.Cancelled, cancelledProgress.Values[^1].Phase);
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Scalar query returned null.");
        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static async Task<IReadOnlySet<string>> ReadStringsAsync(
        SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(reader.GetString(0));
        return result;
    }
}
