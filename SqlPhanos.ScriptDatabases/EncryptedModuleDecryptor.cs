using System;
using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlPhanos.ScriptDatabases;

/// <summary>
/// Reads encrypted module imageval over DAC and decrypts with RC4 (SQL Server 2019 compatible).
/// Does not ALTER or drop objects on the server.
/// </summary>
public sealed class EncryptedModuleDecryptor : IDisposable
{
    private readonly SqlConnection _connection;

    private EncryptedModuleDecryptor(SqlConnection connection)
    {
        _connection = connection;
    }

    public static EncryptedModuleDecryptor Connect(string connectionString, string databaseName)
    {
        // DAC must open against master first; opening a user DB as Initial Catalog while SMO
        // (or another session) still holds that DB causes error 924 on single-user/auto-close DBs.
        SqlConnection connection = new(BuildDacConnectionString(connectionString));
        try
        {
            connection.Open();
            connection.ChangeDatabase(databaseName);
            return new EncryptedModuleDecryptor(connection);
        }
        catch (SqlException ex)
        {
            connection.Dispose();
            throw new InvalidOperationException(
                BuildDacFailureMessage(connectionString, databaseName, ex),
                ex);
        }
    }

    public string DecryptModule(string schema, string objectName)
    {
        EncryptedModulePayload payload = LoadPayload(schema, objectName);
        byte[] key = SqlModuleRc4.DeriveInitializationKey(
            payload.FamilyGuid,
            payload.ObjectId,
            payload.SubObjectId);
        byte[] plainBytes = SqlModuleRc4.Transform(key, payload.ImageVal);
        return Encoding.Unicode.GetString(plainBytes);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }

    private EncryptedModulePayload LoadPayload(string schema, string objectName)
    {
        using SqlCommand command = _connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                CONVERT(uniqueidentifier, drs.family_guid) AS FamilyGuid,
                o.object_id AS ObjectId,
                CONVERT(smallint, sov.subobjid) AS SubObjectId,
                sov.imageval AS ImageVal
            FROM sys.objects AS o
            INNER JOIN sys.sysobjvalues AS sov
                ON sov.[objid] = o.object_id
                AND sov.valclass = 1
            INNER JOIN sys.database_recovery_status AS drs
                ON drs.database_id = DB_ID()
            WHERE o.schema_id = SCHEMA_ID(@schema)
              AND o.name = @name
              AND sov.imageval IS NOT NULL;
            """;
        command.Parameters.AddWithValue("@schema", schema);
        command.Parameters.AddWithValue("@name", objectName);

        using SqlDataReader reader = command.ExecuteReader(CommandBehavior.SingleRow);
        if (!reader.Read())
        {
            throw new InvalidOperationException(
                $"Encrypted definition for [{schema}].[{objectName}] was not found via DAC.");
        }

        return new EncryptedModulePayload(
            reader.GetGuid(0),
            reader.GetInt32(1),
            reader.GetInt16(2),
            (byte[])reader[3]);
    }

    private static string BuildDacConnectionString(string connectionString)
    {
        SqlConnectionStringBuilder source = new(connectionString);
        SqlConnectionStringBuilder builder = new()
        {
            DataSource = $"ADMIN:{source.DataSource}",
            InitialCatalog = "master",
            IntegratedSecurity = source.IntegratedSecurity,
            TrustServerCertificate = source.TrustServerCertificate,
            Encrypt = true,
            ConnectTimeout = 15,
            Pooling = false
        };

        if (!source.IntegratedSecurity)
        {
            builder.UserID = source.UserID;
            builder.Password = source.Password;
        }

        return builder.ConnectionString;
    }

    private static string BuildDacFailureMessage(
        string connectionString,
        string databaseName,
        SqlException ex)
    {
        string serverName = new SqlConnectionStringBuilder(connectionString).DataSource;
        return
            $"Unable to open DAC (ADMIN:) to '{serverName}' database '{databaseName}'. " +
            "Encrypted modules require a Dedicated Administrator Connection. " +
            "For remote instances enable 'remote admin connections'. " +
            $"Details: {ex.Message}";
    }

    private sealed record EncryptedModulePayload(
        Guid FamilyGuid,
        int ObjectId,
        short SubObjectId,
        byte[] ImageVal);
}
