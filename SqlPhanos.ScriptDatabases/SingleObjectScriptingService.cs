using System;
using System.Collections.Specialized;
using System.Text;
using Microsoft.SqlServer.Management.Sdk.Sfc;
using Microsoft.SqlServer.Management.Smo;

namespace SqlPhanos.ScriptDatabases;

/// <summary>
/// Scripts exactly one already-identified object to fully composed header+body text - the
/// shared primitive behind the single-object interactive scripting path. Script Databases'
/// whole-database bulk path deliberately does NOT route through this: it keeps its own
/// connection-pooled Scripter mechanism (see DatabaseScriptingService.CreateScripterPool),
/// since looping this type's connection handling over every object in a database would trade a
/// handful of reused connections for one per object.
/// </summary>
public static class SingleObjectScriptingService
{
    /// <summary>
    /// Scripts a non-encrypted object whose Urn has already been resolved against an
    /// already-connected Server. Deliberately takes the live Server rather than a connection
    /// string: resolving the Urn in the first place already required a connection, so this
    /// reuses it instead of opening a second one just to script what was already found.
    /// </summary>
    public static string ScriptResolvedObject(
        Server server,
        Urn urn,
        string objectName,
        string serverDisplayName,
        string databaseDisplayName,
        bool includeTriggersInTableScript)
    {
        var scripter = new Scripter(server)
        {
            Options = ScriptingOptionsFactory.Create(includeTriggersInTableScript)
        };

        UrnCollection urns = [urn];
        StringCollection batches = scripter.Script(urns);
        var body = new StringBuilder();
        foreach (var batch in batches)
        {
            body.AppendLine(batch);
            body.AppendLine("GO");
        }

        string header = ObjectScriptHeaderBuilder.Build(
            objectName,
            serverDisplayName,
            databaseDisplayName,
            DateTimeOffset.Now);

        return header + body;
    }

    /// <summary>
    /// Scripts an encrypted (WITH ENCRYPTION) object via the read-only RC4/DAC technique only -
    /// never via ALTER-and-rollback (see EncryptedModuleDecryptor's own doc comment for why).
    /// Owns its own DAC connection: that's a fundamentally different connection (ADMIN:-prefixed,
    /// master-scoped) than a caller's regular SMO Server connection, so there is no connection to
    /// usefully share here. Throws EncryptedModulesConsentRequiredException when consent hasn't
    /// been given yet - callers should catch it, ask the user, and retry with
    /// allowEncryptedModuleDecrypt: true, exactly like
    /// ScriptDatabasesDocumentViewModel.ScriptOneDatabaseWithConsentAsync already does for the
    /// bulk path.
    /// </summary>
    public static string ScriptEncryptedObject(
        string connectionString,
        string schema,
        string objectName,
        string serverDisplayName,
        string databaseDisplayName,
        bool allowEncryptedModuleDecrypt)
    {
        if (!allowEncryptedModuleDecrypt)
        {
            throw new EncryptedModulesConsentRequiredException($"{schema}.{objectName}", 1);
        }

        using EncryptedModuleDecryptor decryptor = EncryptedModuleDecryptor.Connect(
            connectionString,
            databaseDisplayName);
        string definition = decryptor.DecryptModule(schema, objectName);

        string header = ObjectScriptHeaderBuilder.Build(
            objectName,
            serverDisplayName,
            databaseDisplayName,
            DateTimeOffset.Now);

        return header + definition;
    }
}
