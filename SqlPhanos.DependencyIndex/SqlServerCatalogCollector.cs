using Microsoft.Data.SqlClient;
using System.Collections.Concurrent;
using System.Data;
using System.Security.Cryptography;
using System.Text;

namespace SqlPhanos.DependencyIndex;

public interface ICatalogCollector
{
    Task<ServerCatalogSnapshot> CollectAsync(CancellationToken cancellationToken = default);

    Task<ServerCatalogSnapshot> CollectAsync(
        IProgress<IndexProgress>? progress, CancellationToken cancellationToken = default)
        => CollectAsync(cancellationToken);
}

public sealed class SqlServerCatalogCollector : ICatalogCollector
{
    private readonly string _connectionString;
    private readonly int _maxConcurrentDatabaseScans;
    private readonly IProgress<IndexProgress>? _progress;

    public SqlServerCatalogCollector(
        string connectionString, int maxConcurrentDatabaseScans = 3,
        IProgress<IndexProgress>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (maxConcurrentDatabaseScans is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentDatabaseScans), "The index collector permits one to three concurrent database scans.");
        _connectionString = connectionString;
        _maxConcurrentDatabaseScans = maxConcurrentDatabaseScans;
        _progress = progress;
    }

    public Task<ServerCatalogSnapshot> CollectAsync(CancellationToken cancellationToken = default)
        => CollectAsync(_progress, cancellationToken);

    public async Task<ServerCatalogSnapshot> CollectAsync(
        IProgress<IndexProgress>? progress, CancellationToken cancellationToken = default)
    {
        try
        {
            return await CollectCoreAsync(progress ?? _progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            (progress ?? _progress)?.Report(new IndexProgress(IndexProgressPhase.Cancelled,
                0, 0, null, 0, 0, "Server catalog collection was cancelled."));
            throw;
        }
    }

    private async Task<ServerCatalogSnapshot> CollectCoreAsync(
        IProgress<IndexProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new IndexProgress(IndexProgressPhase.DiscoveringServer, 0, 0,
            null, 0, 0, "Connecting and reading server identity."));
        var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var identity = await ReadIdentityAsync(connection, builder.DataSource, cancellationToken);
        progress?.Report(new IndexProgress(IndexProgressPhase.EnumeratingDatabases, 0, 0,
            null, 0, 0, "Enumerating accessible user databases and linked-server aliases."));
        var databases = await ReadDatabasesAsync(connection, cancellationToken);
        var linkedServers = await ReadLinkedServersAsync(connection, cancellationToken);
        var snapshots = new ConcurrentDictionary<int, DatabaseCatalogSnapshot>();
        using var gate = new SemaphoreSlim(_maxConcurrentDatabaseScans);
        var completed = 0;
        var objectCount = 0;
        var edgeCount = 0;

        await Task.WhenAll(databases.Select(async database =>
        {
            progress?.Report(new IndexProgress(IndexProgressPhase.CollectingDatabase,
                Volatile.Read(ref completed), databases.Count, database.Name,
                Volatile.Read(ref objectCount), Volatile.Read(ref edgeCount),
                database.IsAccessible ? "Collecting catalog metadata." : "Database is inaccessible."));
            if (!database.IsAccessible)
            {
                snapshots[database.SqlDatabaseId] = new DatabaseCatalogSnapshot(
                    database, [], [], [], [], ScanOutcome.Inaccessible, "Database is not accessible to the current login.");
                var current = Interlocked.Increment(ref completed);
                progress?.Report(new IndexProgress(IndexProgressPhase.DatabaseFailed,
                    current, databases.Count, database.Name, Volatile.Read(ref objectCount),
                    Volatile.Read(ref edgeCount), "Database is inaccessible."));
                return;
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                var snapshot = await ReadDatabaseAsync(database, cancellationToken);
                snapshots[database.SqlDatabaseId] = snapshot;
                Interlocked.Add(ref objectCount, snapshot.Objects.Count);
                Interlocked.Add(ref edgeCount, CountCatalogEdges(snapshot));
                var current = Interlocked.Increment(ref completed);
                progress?.Report(new IndexProgress(IndexProgressPhase.CollectingDatabase,
                    current, databases.Count, database.Name, Volatile.Read(ref objectCount),
                    Volatile.Read(ref edgeCount), "Catalog collection completed."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                snapshots[database.SqlDatabaseId] = new DatabaseCatalogSnapshot(
                    database, [], [], [], [], ScanOutcome.Failed, exception.Message);
                var current = Interlocked.Increment(ref completed);
                progress?.Report(new IndexProgress(IndexProgressPhase.DatabaseFailed,
                    current, databases.Count, database.Name, Volatile.Read(ref objectCount),
                    Volatile.Read(ref edgeCount), exception.Message));
            }
            finally
            {
                gate.Release();
            }
        }));

        var result = new ServerCatalogSnapshot(
            identity, linkedServers,
            databases.Select(database => snapshots[database.SqlDatabaseId]).ToArray());
        progress?.Report(new IndexProgress(IndexProgressPhase.CollectionCompleted,
            databases.Count, databases.Count, null, objectCount, edgeCount,
            "Server catalog collection completed."));
        return result;
    }

    private static int CountCatalogEdges(DatabaseCatalogSnapshot snapshot)
        => snapshot.ForeignKeys.Count + snapshot.Synonyms.Count + snapshot.TypeUses.Count +
           snapshot.Objects.Count(item => item.ParentSqlObjectId is not null);

    private async Task<DatabaseCatalogSnapshot> ReadDatabaseAsync(
        CatalogDatabase database, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = database.Name };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var objects = await ReadObjectsAsync(connection, cancellationToken);
        var synonyms = await ReadSynonymsAsync(connection, cancellationToken);
        var foreignKeys = await ReadForeignKeysAsync(connection, cancellationToken);
        var typeUses = await ReadTypesAsync(connection, objects, cancellationToken);
        return new DatabaseCatalogSnapshot(database, objects, foreignKeys, synonyms, typeUses, ScanOutcome.Succeeded);
    }

    private static async Task<ServerIdentity> ReadIdentityAsync(
        SqlConnection connection, string dataSource, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              CONVERT(nvarchar(256),SERVERPROPERTY('ServerName')),
              CONVERT(nvarchar(256),SERVERPROPERTY('MachineName')),
              CONVERT(nvarchar(256),SERVERPROPERTY('InstanceName')),
              CONVERT(int,SERVERPROPERTY('EngineEdition')),
              CONVERT(nvarchar(128),SERVERPROPERTY('ProductVersion'));
            """;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, token);
        if (!await reader.ReadAsync(token))
            throw new InvalidOperationException("SQL Server did not return server identity properties.");
        var actual = reader.IsDBNull(0) ? dataSource : reader.GetString(0);
        var machine = reader.IsDBNull(1) ? actual : reader.GetString(1);
        var instance = reader.IsDBNull(2) ? null : reader.GetString(2);
        var edition = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
        var version = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
        var fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{actual}\n{machine}\n{instance}\n{edition}")));
        return new ServerIdentity(fingerprint, actual, dataSource, actual, machine, instance, edition, version);
    }

    private static async Task<IReadOnlyList<CatalogDatabase>> ReadDatabasesAsync(
        SqlConnection connection, CancellationToken token)
    {
        var result = new List<CatalogDatabase>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT database_id,name,collation_name,state_desc,
                   CONVERT(bit,CASE WHEN HAS_DBACCESS(name)=1 THEN 1 ELSE 0 END)
            FROM sys.databases
            WHERE database_id > @system_database_max_id
            ORDER BY database_id;
            """;
        command.Parameters.Add("@system_database_max_id", SqlDbType.Int).Value = 4;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token))
            result.Add(new CatalogDatabase(reader.GetInt32(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetBoolean(4)));
        return result;
    }

    private static async Task<IReadOnlyList<CatalogLinkedServer>> ReadLinkedServersAsync(
        SqlConnection connection, CancellationToken token)
    {
        var result = new List<CatalogLinkedServer>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name,product,provider,data_source,location,catalog,
                   is_data_access_enabled,is_rpc_out_enabled
            FROM sys.servers WHERE is_linked=1 ORDER BY name;
            """;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token))
            result.Add(new CatalogLinkedServer(reader.GetString(0), Text(reader, 1), Text(reader, 2),
                Text(reader, 3), Text(reader, 4), Text(reader, 5), reader.GetBoolean(6), reader.GetBoolean(7)));
        return result;
    }

    private static async Task<List<CatalogObject>> ReadObjectsAsync(
        SqlConnection connection, CancellationToken token)
    {
        var result = new List<CatalogObject>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.object_id,s.name,o.name,o.type_desc,o.parent_object_id,
                   CONVERT(bit,ISNULL(OBJECTPROPERTYEX(o.object_id,'IsEncrypted'),0)),
                   o.create_date,o.modify_date,m.definition
            FROM sys.objects o
            JOIN sys.schemas s ON s.schema_id=o.schema_id
            LEFT JOIN sys.sql_modules m ON m.object_id=o.object_id
            WHERE o.is_ms_shipped=@is_ms_shipped
              AND o.type IN ('U','V','P','PC','FN','IF','TF','FS','FT','TR','TA','SN','SO','R','D','C')
            ORDER BY o.object_id;
            """;
        command.Parameters.Add("@is_ms_shipped", SqlDbType.Bit).Value = false;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token))
        {
            var objectId = reader.GetInt32(0);
            var schemaName = reader.GetString(1);
            var name = reader.GetString(2);
            var typeDescription = reader.GetString(3);
            var parentObjectId = reader.GetInt32(4);
            var isEncrypted = reader.GetBoolean(5);
            var createDate = ToDateTimeOffset(reader.GetDateTime(6));
            var modifyDate = ToDateTimeOffset(reader.GetDateTime(7));
            var definition = Text(reader, 8);
            result.Add(new CatalogObject(objectId, schemaName, name, typeDescription,
                parentObjectId is 0 ? null : parentObjectId, isEncrypted, createDate, modifyDate, definition));
        }
        return result;
    }

    private static async Task<IReadOnlyList<CatalogSynonym>> ReadSynonymsAsync(
        SqlConnection connection, CancellationToken token)
    {
        var result = new List<CatalogSynonym>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT object_id,base_object_name FROM sys.synonyms ORDER BY object_id;";
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token))
            result.Add(new CatalogSynonym(reader.GetInt32(0), reader.GetString(1)));
        return result;
    }

    private static async Task<IReadOnlyList<CatalogForeignKey>> ReadForeignKeysAsync(
        SqlConnection connection, CancellationToken token)
    {
        var result = new List<CatalogForeignKey>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT parent_object_id,referenced_object_id,name
            FROM sys.foreign_keys WHERE is_ms_shipped=@is_ms_shipped ORDER BY object_id;
            """;
        command.Parameters.Add("@is_ms_shipped", SqlDbType.Bit).Value = false;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token))
            result.Add(new CatalogForeignKey(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2)));
        return result;
    }

    private static async Task<IReadOnlyList<CatalogTypeUse>> ReadTypesAsync(
        SqlConnection connection, List<CatalogObject> objects, CancellationToken token)
    {
        var result = new List<CatalogTypeUse>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.user_type_id,s.name,t.name,t.is_table_type,
                   COALESCE(tt.type_table_object_id,0)
            FROM sys.types t
            JOIN sys.schemas s ON s.schema_id=t.schema_id
            LEFT JOIN sys.table_types tt ON tt.user_type_id=t.user_type_id
            WHERE t.is_user_defined=1 ORDER BY t.user_type_id;
            SELECT DISTINCT c.object_id,c.user_type_id,t.name
            FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id
            WHERE t.is_user_defined=1
            UNION
            SELECT DISTINCT p.object_id,p.user_type_id,t.name
            FROM sys.parameters p JOIN sys.types t ON t.user_type_id=p.user_type_id
            WHERE t.is_user_defined=1;
            """;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token);
        while (await reader.ReadAsync(token))
        {
            var syntheticId = -reader.GetInt32(0);
            objects.Add(new CatalogObject(syntheticId, reader.GetString(1), reader.GetString(2),
                reader.GetBoolean(3) ? "TABLE_TYPE" : "USER_DEFINED_TYPE", null, false, null, null, null));
        }
        await reader.NextResultAsync(token);
        while (await reader.ReadAsync(token))
            result.Add(new CatalogTypeUse(reader.GetInt32(0), -reader.GetInt32(1), reader.GetString(2)));
        return result;
    }

    private static string? Text(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset ToDateTimeOffset(DateTime value)
        => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
