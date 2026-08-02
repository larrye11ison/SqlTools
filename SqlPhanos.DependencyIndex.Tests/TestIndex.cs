using Microsoft.Data.Sqlite;
using SqlPhanos.DependencyIndex;

namespace SqlPhanos.DependencyIndex.Tests;

internal sealed class TestIndex : IAsyncDisposable
{
    private readonly string _directory;

    private TestIndex(string directory, DependencyIndexStore store)
    {
        _directory = directory;
        Store = store;
        Builder = new DependencyIndexBuilder(store);
        Graph = new DependencyGraphQueryService(store);
    }

    internal sealed class RecordingProgress<T> : IProgress<T>
    {
        private readonly List<T> _values = [];
        public IReadOnlyList<T> Values
        {
            get
            {
                lock (_values)
                    return _values.ToArray();
            }
        }

        public void Report(T value)
        {
            lock (_values)
                _values.Add(value);
        }
    }

    internal sealed class ProgressCatalogCollector(ServerCatalogSnapshot snapshot) : ICatalogCollector
    {
        public Task<ServerCatalogSnapshot> CollectAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(snapshot);

        public Task<ServerCatalogSnapshot> CollectAsync(
            IProgress<IndexProgress>? progress, CancellationToken cancellationToken = default)
        {
            progress?.Report(new IndexProgress(IndexProgressPhase.DiscoveringServer,
                0, 0, null, 0, 0, "Test collector started."));
            progress?.Report(new IndexProgress(IndexProgressPhase.CollectionCompleted,
                snapshot.Databases.Count, snapshot.Databases.Count, null,
                snapshot.Databases.Sum(database => database.Objects.Count), 0,
                "Test collector completed."));
            return Task.FromResult(snapshot);
        }
    }

    public DependencyIndexStore Store { get; }
    public DependencyIndexBuilder Builder { get; }
    public DependencyGraphQueryService Graph { get; }

    public static async Task<TestIndex> CreateAsync()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "index-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = new TestIndex(directory,
            new DependencyIndexStore(Path.Combine(directory, "dependencies.sqlite3")));
        await result.Store.InitializeAsync();
        return result;
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={Store.DatabasePath};Foreign Keys=True");
        connection.Open();
        return connection;
    }

    public static ServerCatalogSnapshot Snapshot(
        IReadOnlyList<CatalogObject>? objects = null,
        IReadOnlyList<CatalogLinkedServer>? linked = null,
        IReadOnlyList<CatalogForeignKey>? foreignKeys = null,
        IReadOnlyList<DatabaseCatalogSnapshot>? databases = null)
    {
        var main = new CatalogDatabase(5, "MainDb", "Latin1_General_100_CI_AS", "ONLINE", true);
        objects ??=
        [
            Object(1, "dbo", "ViewA", "VIEW", "CREATE VIEW dbo.ViewA AS SELECT * FROM dbo.ViewB;"),
            Object(2, "dbo", "ViewB", "VIEW", "CREATE VIEW dbo.ViewB AS SELECT * FROM dbo.ViewC;"),
            Object(3, "dbo", "ViewC", "VIEW", "CREATE VIEW dbo.ViewC AS SELECT * FROM dbo.ViewA;"),
        ];
        databases ??=
        [
            new DatabaseCatalogSnapshot(main, objects, foreignKeys ?? [], [], [], ScanOutcome.Succeeded),
        ];
        return new ServerCatalogSnapshot(
            new ServerIdentity("server-key", "TestServer", "test-source", "TestServer",
                "TestMachine", null, 3, "17.0"),
            linked ?? [], databases);
    }

    public static CatalogObject Object(int id, string schema, string name, string type, string? definition,
        int? parentId = null, bool encrypted = false, DateTimeOffset? modifyDate = null)
        => new(id, schema, name, type, parentId, encrypted,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            modifyDate ?? DateTimeOffset.Parse("2026-01-02T00:00:00Z"), definition);

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await Task.Yield();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
