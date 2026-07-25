using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SqlPhanos.ScriptDatabases;

public sealed record SqlServerCatalogue(
    string ActualServerName,
    IReadOnlyList<string> UserDatabases);

public interface IDatabaseCatalogueService
{
    Task<SqlServerCatalogue> LoadCatalogueAsync(
        string connectionString,
        CancellationToken cancellationToken);
}

/// <summary>
/// Lists a connection's user databases and resolves its actual server name (for output-folder
/// naming). Deliberately self-contained rather than reusing SqlPhanos's own
/// SqlSearchService.GetDatabasesAsync, which doesn't resolve @@SERVERNAME or take a
/// CancellationToken.
/// </summary>
public sealed class DatabaseCatalogueService : IDatabaseCatalogueService
{
    public async Task<SqlServerCatalogue> LoadCatalogueAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);

        string actualServerName = await ReadActualServerNameAsync(connection, cancellationToken);
        IReadOnlyList<string> databases = await ReadUserDatabasesAsync(connection, cancellationToken);
        return new SqlServerCatalogue(actualServerName, databases);
    }

    private static async Task<string> ReadActualServerNameAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = new(
            "SELECT CAST(@@SERVERNAME AS sysname);",
            connection);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value as string ?? string.Empty;
    }

    private static async Task<IReadOnlyList<string>> ReadUserDatabasesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqlCommand command = new(
            """
            SELECT name
            FROM sys.databases
            WHERE database_id > 4
            ORDER BY name;
            """,
            connection);

        List<string> names = [];
        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
