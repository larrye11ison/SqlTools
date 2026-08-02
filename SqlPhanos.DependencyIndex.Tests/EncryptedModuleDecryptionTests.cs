using SqlPhanos.ScriptDatabases;

namespace SqlPhanos.DependencyIndex.Tests;

public sealed class EncryptedModuleDecryptionTests
{
    [Fact]
    public async Task PromptIfNeededThrowsConsentExceptionBeforeAnyPromotion()
    {
        await using var index = await TestIndex.CreateAsync();
        var snapshot = TestIndex.Snapshot(
        [
            TestIndex.Object(1, "dbo", "EncProc", "SQL_STORED_PROCEDURE", null, encrypted: true),
        ]);

        var exception = await Assert.ThrowsAsync<EncryptedModulesConsentRequiredException>(
            () => index.Builder.BuildAsync(snapshot));

        Assert.Equal(1, exception.EncryptedCount);
        Assert.Empty(await index.Graph.SearchObjectsAsync("EncProc"));
    }

    [Fact]
    public async Task SkipModeCompletesWithoutDecryptingAndLeavesEncryptedStatus()
    {
        await using var index = await TestIndex.CreateAsync();
        var factory = new FakeEncryptedModuleDecryptorFactory((_, _) => "unused");
        var builder = new DependencyIndexBuilder(index.Store, factory);
        var snapshot = TestIndex.Snapshot(
        [
            TestIndex.Object(1, "dbo", "EncProc", "SQL_STORED_PROCEDURE", null, encrypted: true),
        ]);

        var result = await builder.BuildAsync(snapshot,
            new IndexBuildOptions(EncryptedModuleDecrypt: EncryptedModuleDecryptMode.Skip));

        Assert.Equal(ScanOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, factory.ConnectCount);
        var encProc = Assert.Single(await index.Graph.SearchObjectsAsync("EncProc"));
        Assert.Equal(AnalysisStatus.Encrypted, encProc.AnalysisStatus);
        Assert.True(encProc.Encrypted);
    }

    [Fact]
    public async Task AllowModeDecryptsAndProducesRealEdges()
    {
        await using var index = await TestIndex.CreateAsync();
        var factory = new FakeEncryptedModuleDecryptorFactory(
            (_, name) => $"CREATE PROCEDURE dbo.{name} AS SELECT * FROM dbo.PlainTable;");
        var builder = new DependencyIndexBuilder(index.Store, factory);
        var snapshot = TestIndex.Snapshot(
        [
            TestIndex.Object(1, "dbo", "EncProc", "SQL_STORED_PROCEDURE", null, encrypted: true),
            TestIndex.Object(2, "dbo", "PlainTable", "USER_TABLE", null),
        ]);

        var result = await builder.BuildAsync(snapshot,
            new IndexBuildOptions(EncryptedModuleDecrypt: EncryptedModuleDecryptMode.Allow),
            "Server=test;Database=test;");

        Assert.Equal(ScanOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, factory.ConnectCount);
        var encProc = Assert.Single(await index.Graph.SearchObjectsAsync("EncProc"));
        Assert.Equal(AnalysisStatus.Complete, encProc.AnalysisStatus);
        Assert.True(encProc.Encrypted);
        var edge = Assert.Single((await index.Graph.GetDirectAsync(encProc.ObjectKey, GraphDirection.Uses)).Edges);
        Assert.Equal("PlainTable", edge.Edge.TargetObjectName);
    }

    [Fact]
    public async Task UnchangedModifyDateReusesDecryptedEdgesWithoutReDecrypting()
    {
        await using var index = await TestIndex.CreateAsync();
        var factory = new FakeEncryptedModuleDecryptorFactory(
            (_, name) => $"CREATE PROCEDURE dbo.{name} AS SELECT * FROM dbo.PlainTable;");
        var builder = new DependencyIndexBuilder(index.Store, factory);
        var modifyDate = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
        var objects = new[]
        {
            TestIndex.Object(1, "dbo", "EncProc", "SQL_STORED_PROCEDURE", null, encrypted: true, modifyDate: modifyDate),
            TestIndex.Object(2, "dbo", "PlainTable", "USER_TABLE", null),
        };
        var options = new IndexBuildOptions(EncryptedModuleDecrypt: EncryptedModuleDecryptMode.Allow);
        const string connectionString = "Server=test;Database=test;";

        await builder.BuildAsync(TestIndex.Snapshot(objects), options, connectionString);
        Assert.Equal(1, factory.ConnectCount);

        // Second scan, same modify_date: no new decrypt attempt, prior edges retained.
        await builder.BuildAsync(TestIndex.Snapshot(objects), options, connectionString);

        Assert.Equal(1, factory.ConnectCount);
        var encProc = Assert.Single(await index.Graph.SearchObjectsAsync("EncProc"));
        Assert.Equal(AnalysisStatus.Complete, encProc.AnalysisStatus);
        var edge = Assert.Single((await index.Graph.GetDirectAsync(encProc.ObjectKey, GraphDirection.Uses)).Edges);
        Assert.Equal("PlainTable", edge.Edge.TargetObjectName);
    }

    [Fact]
    public async Task ChangedModifyDateTriggersReDecrypt()
    {
        await using var index = await TestIndex.CreateAsync();
        var factory = new FakeEncryptedModuleDecryptorFactory(
            (_, name) => $"CREATE PROCEDURE dbo.{name} AS SELECT * FROM dbo.PlainTable;");
        var builder = new DependencyIndexBuilder(index.Store, factory);
        var options = new IndexBuildOptions(EncryptedModuleDecrypt: EncryptedModuleDecryptMode.Allow);
        const string connectionString = "Server=test;Database=test;";
        var firstModify = DateTimeOffset.Parse("2026-03-01T00:00:00Z");
        var secondModify = DateTimeOffset.Parse("2026-03-02T00:00:00Z");

        await builder.BuildAsync(TestIndex.Snapshot(
        [
            TestIndex.Object(1, "dbo", "EncProc", "SQL_STORED_PROCEDURE", null, encrypted: true, modifyDate: firstModify),
            TestIndex.Object(2, "dbo", "PlainTable", "USER_TABLE", null),
        ]), options, connectionString);
        Assert.Equal(1, factory.ConnectCount);

        await builder.BuildAsync(TestIndex.Snapshot(
        [
            TestIndex.Object(1, "dbo", "EncProc", "SQL_STORED_PROCEDURE", null, encrypted: true, modifyDate: secondModify),
            TestIndex.Object(2, "dbo", "PlainTable", "USER_TABLE", null),
        ]), options, connectionString);

        Assert.Equal(2, factory.ConnectCount);
    }

    private sealed class FakeEncryptedModuleDecryptorFactory(Func<string, string, string> decrypt)
        : IEncryptedModuleDecryptorFactory
    {
        public int ConnectCount { get; private set; }

        public IDatabaseModuleDecryptor Connect(string connectionString, string databaseName)
        {
            ConnectCount++;
            return new FakeDecryptor(decrypt);
        }

        private sealed class FakeDecryptor(Func<string, string, string> decrypt) : IDatabaseModuleDecryptor
        {
            public string DecryptModule(string schema, string objectName) => decrypt(schema, objectName);
            public void Dispose()
            {
            }
        }
    }
}
