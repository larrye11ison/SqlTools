using System;
using System.IO;

namespace SqlPhanos.Updater;

/// <summary>
/// Minimal file-append logger - no logging framework exists anywhere in this codebase, and one
/// isn't worth pulling in just for this. Writes to the one existing app-data convention this app
/// already has (see ConnectionProfileStoreService's "connections.json" in the same folder),
/// rather than the install directory (which an update in progress is actively rewriting) or the
/// temp folder (which the zip's own cleanup already touches).
/// </summary>
internal static class UpdateLog
{
    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SqlPhanos",
        "updater.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Logging must never itself take down the update - if the log can't be written
            // (permissions, disk full, whatever), the console output the user is watching still
            // carries the short version of what happened.
        }
    }
}
