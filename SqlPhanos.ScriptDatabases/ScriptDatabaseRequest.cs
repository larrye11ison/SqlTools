using System;
using System.Threading;

namespace SqlPhanos.ScriptDatabases;

public sealed record ScriptDatabaseRequest(
    string ConnectionString,
    string DatabaseName,
    string BaseOutputDirectory,
    int MaxConcurrentObjectScripts,
    ScriptingMode Mode,
    IProgress<ScriptingProgressReport> Progress,
    Action<string> OnOutputDirectoryResolved,
    CancellationToken CancellationToken,
    bool AllowEncryptedModuleDecrypt = false,
    // Deliberately just a text transform, not a reference to any specific formatter - keeps this
    // engine agnostic of (and not dependent on) SqlPhanos.CodeFormatting. Null means "write
    // scripts exactly as SMO/the decryptor produced them," which is also the default.
    Func<string, string>? FormatSqlText = null);

public sealed record ScriptDatabaseResult(
    string OutputDirectory,
    string ServerOutputDirectory);
