using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace SqlPhanos.Updater;

/// <summary>
/// Applies a SqlPhanos update in place. Exists as a separate process because a running .exe
/// (and its loaded DLLs) can't overwrite its own files on Windows - SqlPhanos downloads the new
/// build, launches this with the arguments below, and exits; this waits for that exit, unzips
/// the new build over the install directory, and relaunches SqlPhanos.
///
/// Args: &lt;parentPid&gt; &lt;zipPath&gt; &lt;targetDir&gt; &lt;relaunchExePath&gt;
/// </summary>
internal static class Program
{
    private const int MaxExtractAttempts = 10;
    private static readonly TimeSpan ExtractRetryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(15);

    private static void Main(string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[0], out var parentPid))
        {
            return;
        }

        var zipPath = args[1];
        var targetDir = args[2];
        var relaunchExePath = args[3];

        WaitForParentExit(parentPid);
        ExtractWithRetry(zipPath, targetDir);

        try
        {
            File.Delete(zipPath);
        }
        catch (IOException)
        {
            // Best-effort cleanup only - a leftover temp zip isn't worth failing the update over.
        }

        Process.Start(relaunchExePath);
    }

    private static void WaitForParentExit(int parentPid)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            parent.WaitForExit((int)ParentExitTimeout.TotalMilliseconds);
        }
        catch (ArgumentException)
        {
            // Already exited before this process could even look it up - nothing to wait for.
        }
    }

    private static void ExtractWithRetry(string zipPath, string targetDir)
    {
        for (var attempt = 1; attempt <= MaxExtractAttempts; attempt++)
        {
            try
            {
                ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);
                return;
            }
            catch (IOException) when (attempt < MaxExtractAttempts)
            {
                // SqlPhanos.exe/its DLLs may not have fully released their file handles yet.
                Thread.Sleep(ExtractRetryDelay);
            }
        }
    }
}
