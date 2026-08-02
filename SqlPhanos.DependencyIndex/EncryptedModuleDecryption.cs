using SqlPhanos.ScriptDatabases;

namespace SqlPhanos.DependencyIndex;

/// <summary>
/// Decrypts one WITH ENCRYPTION module's definition. Instances are scoped to a single database
/// and hold a live Dedicated Administrator Connection (DAC) - dispose promptly, and never hold
/// more than one at a time (SQL Server permits exactly one DAC session per instance).
/// </summary>
public interface IDatabaseModuleDecryptor : IDisposable
{
    string DecryptModule(string schema, string objectName);
}

/// <summary>
/// Opens an IDatabaseModuleDecryptor for a given server/database. Exists so
/// DependencyIndexBuilder can be exercised in tests without a live SQL Server DAC connection.
/// </summary>
public interface IEncryptedModuleDecryptorFactory
{
    IDatabaseModuleDecryptor Connect(string connectionString, string databaseName);
}

/// <summary>
/// Default factory backed by SqlPhanos.ScriptDatabases' RC4/DAC decryptor - the same technique
/// already used to script individual encrypted objects and bulk database exports elsewhere in
/// SqlPhanos. Never ALTERs or drops anything on the server.
/// </summary>
public sealed class SqlServerEncryptedModuleDecryptorFactory : IEncryptedModuleDecryptorFactory
{
    public IDatabaseModuleDecryptor Connect(string connectionString, string databaseName)
        => new Adapter(EncryptedModuleDecryptor.Connect(connectionString, databaseName));

    private sealed class Adapter(EncryptedModuleDecryptor inner) : IDatabaseModuleDecryptor
    {
        public string DecryptModule(string schema, string objectName)
            => inner.DecryptModule(schema, objectName);

        public void Dispose() => inner.Dispose();
    }
}
