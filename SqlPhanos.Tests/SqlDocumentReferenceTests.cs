using SqlPhanos.Services;
using SqlPhanos.ViewModels;
using SqlPhanos.CodeFormatting;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using SqlPhanos.Models;
using Xunit;

namespace SqlPhanos.Tests;

public class SqlDocumentReferenceTests
{
    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(14, true)]
    [InlineData(15, false)]
    public void ContainsUsesHalfOpenTextSpan(int offset, bool expected)
    {
        var reference = new SqlDocumentReference(10, 5, "dbo.Table1", null, false);

        Assert.Equal(expected, reference.Contains(offset));
    }

    public sealed class ConnectionProfileStoreServiceTests
    {
        [Fact]
        public void LegacyProfilesReceiveStablePersistedIdsWithoutPersistingPasswords()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                $"SqlPhanos-profile-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "connections.json");

            try
            {
                File.WriteAllText(
                    path,
                    """
                    {
                      "Connections": [
                        {
                          "ServerAndInstance": "Server1",
                          "UseWindowsAuth": false,
                          "UserName": "User1",
                          "TrustServerCertificate": true
                        }
                      ]
                    }
                    """);
                var service = new ConnectionProfileStoreService(path);

                var firstLoad = Assert.Single(service.LoadConnections());
                firstLoad.Password = "NotPersisted";
                service.SaveConnections(new[] { firstLoad });
                var secondLoad = Assert.Single(service.LoadConnections());
                var persisted = JsonSerializer.Deserialize<ConnectionProfileStore>(
                    File.ReadAllText(path));

                Assert.NotEqual(Guid.Empty, firstLoad.ProfileId);
                Assert.Equal(firstLoad.ProfileId, secondLoad.ProfileId);
                Assert.Equal(firstLoad.ProfileId, Assert.Single(persisted!.Connections).Id);
                Assert.DoesNotContain("NotPersisted", File.ReadAllText(path), StringComparison.Ordinal);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void UnresolvedLocalReferenceExplainsThatTheScriptMayFail()
    {
        var reference = new SqlDocumentReference(
            0,
            16,
            "dbo.MissingView",
            target: null,
            isLinkedServer: false,
            "No scriptable object was found.");

        Assert.True(reference.IsUnresolved);
        Assert.False(reference.IsClickable);
        Assert.Contains("No scriptable object was found.", reference.ToolTipText);
    }

    [Fact]
    public void ResolverPrefersConnectedUsersDefaultSchemaForUnqualifiedAmbiguousName()
    {
        var lookup = Lookup(schema: null);
        var matches = new[]
        {
            Match("dbo", isDboSchema: true),
            Match("sales", isDefaultSchema: true)
        };

        var resolved = SqlObjectReferenceResolver.SelectUnambiguousTarget(
            lookup,
            matches);

        Assert.Same(matches[1].Result, resolved);
    }

    [Fact]
    public void ResolverUsesDboWhenDefaultSchemaHasNoMatch()
    {
        var lookup = Lookup(schema: null);
        var matches = new[]
        {
            Match("dbo", isDboSchema: true),
            Match("archive")
        };

        var resolved = SqlObjectReferenceResolver.SelectUnambiguousTarget(
            lookup,
            matches);

        Assert.Same(matches[0].Result, resolved);
    }

    [Fact]
    public void ResolverDoesNotGuessAnExplicitAmbiguousSchema()
    {
        var lookup = Lookup(schema: "sales");
        var matches = new[]
        {
            Match("sales"),
            Match("sales")
        };

        var resolved = SqlObjectReferenceResolver.SelectUnambiguousTarget(
            lookup,
            matches);

        Assert.Null(resolved);
    }

    [Fact]
    public void ResolverDoesNotSelectAnUnqualifiedObjectOutsideDefaultOrDboSchema()
    {
        var lookup = Lookup(schema: null);
        var matches = new[] { Match("archive") };

        var resolved = SqlObjectReferenceResolver.SelectUnambiguousTarget(
            lookup,
            matches);

        Assert.Null(resolved);
    }

    [Theory]
    [InlineData(SqlObjectReferenceKind.TableOrView, "USER_TABLE", true)]
    [InlineData(SqlObjectReferenceKind.TableOrView, "SQL_STORED_PROCEDURE", false)]
    [InlineData(SqlObjectReferenceKind.Procedure, "SQL_STORED_PROCEDURE", true)]
    [InlineData(SqlObjectReferenceKind.Procedure, "USER_TABLE", false)]
    [InlineData(SqlObjectReferenceKind.Type, "TABLE_TYPE", true)]
    [InlineData(SqlObjectReferenceKind.Executable, "SQL_SCALAR_FUNCTION", true)]
    [InlineData(SqlObjectReferenceKind.Executable, "VIEW", false)]
    [InlineData(SqlObjectReferenceKind.Rowset, "VIEW", true)]
    [InlineData(SqlObjectReferenceKind.Rowset, "SQL_TABLE_VALUED_FUNCTION", true)]
    [InlineData(SqlObjectReferenceKind.Rowset, "SQL_SCALAR_FUNCTION", false)]
    [InlineData(SqlObjectReferenceKind.Trigger, "SQL_TRIGGER", true)]
    public void ResolverRequiresCompatibleReferenceAndObjectKinds(
        SqlObjectReferenceKind kind,
        string typeDesc,
        bool expected)
    {
        Assert.Equal(expected, SqlObjectReferenceResolver.IsCompatible(kind, typeDesc));
    }

    [Fact]
    public void CurrentObjectIdentityKeepsSeparateSqlNamespacesDistinct()
    {
        var table = Result("dbo", "NavCollision");
        table.TypeDesc = "USER_TABLE";
        var type = Result("dbo", "NavCollision");
        type.TypeDesc = "USER_DEFINED_DATA_TYPE";

        Assert.False(SqlDocumentViewModel.IsSameObject(type, table));
        Assert.True(SqlDocumentViewModel.IsSameObject(table, table));
    }

    [Fact]
    public async Task ResolverCachesCatalogMatchesAcrossCallsAndReferenceKinds()
    {
        var queryCount = 0;
        var resolver = new SqlObjectReferenceResolver(
            (connectionString, databaseName, lookups, cancellationToken) =>
            {
                Interlocked.Increment(ref queryCount);
                IReadOnlyDictionary<int, IReadOnlyList<SqlObjectReferenceMatch>> result =
                    lookups.ToDictionary(
                        lookup => lookup.Id,
                        lookup => (IReadOnlyList<SqlObjectReferenceMatch>)new[]
                        {
                            Match("dbo", isDboSchema: true)
                        });
                return Task.FromResult(result);
            },
            TimeProvider.System);

        var first = await resolver.ResolveAsync(
            "Server=Server1;Database=Database1;Integrated Security=true;TrustServerCertificate=true",
            "Database1",
            new[] { Lookup(schema: "dbo") },
            CancellationToken.None);
        var second = await resolver.ResolveAsync(
            "Server=Server1;Database=OtherStartDatabase;Integrated Security=true;TrustServerCertificate=true",
            "Database1",
            new[]
            {
                new SqlObjectReferenceLookup(
                    2,
                    null,
                    "dbo",
                    "Table1",
                    SqlObjectReferenceKind.SchemaObject)
            },
            CancellationToken.None);

        Assert.Equal(1, queryCount);
        Assert.Equal(SqlObjectReferenceResolutionStatus.Resolved, first[1].Status);
        Assert.Equal(SqlObjectReferenceResolutionStatus.Resolved, second[2].Status);
    }

    [Fact]
    public async Task ResolverCachesNotFoundCatalogResults()
    {
        var queryCount = 0;
        var resolver = new SqlObjectReferenceResolver(
            (connectionString, databaseName, lookups, cancellationToken) =>
            {
                Interlocked.Increment(ref queryCount);
                return Task.FromResult<IReadOnlyDictionary<int, IReadOnlyList<SqlObjectReferenceMatch>>>(
                    new Dictionary<int, IReadOnlyList<SqlObjectReferenceMatch>>());
            },
            TimeProvider.System);

        await resolver.ResolveAsync(
            "Server=Server1;Database=Database1;Integrated Security=true;TrustServerCertificate=true",
            "Database1",
            new[] { Lookup(schema: "dbo") },
            CancellationToken.None);
        var second = await resolver.ResolveAsync(
            "Server=Server1;Database=Database1;Integrated Security=true;TrustServerCertificate=true",
            "Database1",
            new[] { Lookup(schema: "dbo") },
            CancellationToken.None);

        Assert.Equal(1, queryCount);
        Assert.Equal(SqlObjectReferenceResolutionStatus.NotFound, second[1].Status);
    }

    [Fact]
    public async Task ResolverCoalescesConcurrentLookupsForTheSameDatabase()
    {
        var queryCount = 0;
        var queryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseQuery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new SqlObjectReferenceResolver(
            async (connectionString, databaseName, lookups, cancellationToken) =>
            {
                Interlocked.Increment(ref queryCount);
                queryStarted.TrySetResult();
                await releaseQuery.Task.WaitAsync(cancellationToken);
                return new Dictionary<int, IReadOnlyList<SqlObjectReferenceMatch>>();
            },
            TimeProvider.System);
        const string connectionString =
            "Server=Server1;Database=Database1;Integrated Security=true;TrustServerCertificate=true";

        var first = resolver.ResolveAsync(
            connectionString,
            "Database1",
            new[] { Lookup(schema: "dbo") },
            CancellationToken.None);
        await queryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = resolver.ResolveAsync(
            connectionString,
            "Database1",
            new[] { Lookup(schema: "dbo") },
            CancellationToken.None);
        releaseQuery.TrySetResult();

        await Task.WhenAll(first, second);

        Assert.Equal(1, queryCount);
    }

    [Fact]
    public async Task ResolverQueriesIndependentDatabasesConcurrently()
    {
        var startedDatabases = new ConcurrentDictionary<string, byte>();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new SqlObjectReferenceResolver(
            async (connectionString, databaseName, lookups, cancellationToken) =>
            {
                startedDatabases.TryAdd(databaseName, 0);
                if (startedDatabases.Count == 2)
                {
                    bothStarted.TrySetResult();
                }

                await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                return new Dictionary<int, IReadOnlyList<SqlObjectReferenceMatch>>();
            },
            TimeProvider.System);

        await resolver.ResolveAsync(
            "Server=Server1;Database=Database1;Integrated Security=true;TrustServerCertificate=true",
            "Database1",
            new[]
            {
                Lookup(schema: "dbo"),
                new SqlObjectReferenceLookup(
                    2,
                    "Database2",
                    "dbo",
                    "Table2",
                    SqlObjectReferenceKind.TableOrView)
            },
            CancellationToken.None);

        Assert.Equal(2, startedDatabases.Count);
    }

    [Fact]
    public void CacheIdentityExcludesPasswordAndStartingDatabase()
    {
        var first = SqlObjectReferenceResolver.CreateConnectionCacheIdentity(
            "Server=Server1;Database=Database1;User ID=User1;Password=Secret1;TrustServerCertificate=true");
        var second = SqlObjectReferenceResolver.CreateConnectionCacheIdentity(
            "Server=Server1;Database=Database2;User ID=User1;Password=Secret2;TrustServerCertificate=true");

        Assert.Equal(first, second);
        Assert.DoesNotContain("Secret", first, StringComparison.Ordinal);
    }

    private static SearchResultViewModel Result(string schema, string name) =>
        new()
        {
            ServerName = "Server1",
            DbName = "Database1",
            SchemaName = schema,
            ObjectName = name,
            TypeDesc = "USER_TABLE"
        };

    private static SqlObjectReferenceMatch Match(
        string schema,
        bool isDefaultSchema = false,
        bool isDboSchema = false,
        string typeDesc = "USER_TABLE")
    {
        var result = Result(schema, "Table1");
        result.TypeDesc = typeDesc;
        return new SqlObjectReferenceMatch(
            result,
            isDefaultSchema,
            isDboSchema);
    }

    private static SqlObjectReferenceLookup Lookup(string? schema) =>
        new(1, null, schema, "Table1", SqlObjectReferenceKind.TableOrView);
}
