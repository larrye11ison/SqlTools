using System;

namespace SqlPhanos.ScriptDatabases;

public sealed class EncryptedModulesConsentRequiredException : Exception
{
    public EncryptedModulesConsentRequiredException(string databaseName, int encryptedCount)
        : base(
            $"{databaseName} has {encryptedCount} encrypted module(s). Consent is required before DAC decrypt.")
    {
        DatabaseName = databaseName;
        EncryptedCount = encryptedCount;
    }

    public string DatabaseName { get; }

    public int EncryptedCount { get; }
}
