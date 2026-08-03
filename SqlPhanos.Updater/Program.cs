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
/// Runs as a visible console (not WinExe) on purpose: an update that silently fails to relaunch
/// - previously with zero UI and zero logging anywhere - just looked like SqlPhanos vanished,
/// with nothing to tell the user why. The happy path prints a handful of progress lines and
/// exits as soon as the new SqlPhanos window appears; any failure prints a short reason plus the
/// log file's path and waits for a keypress, so it stays on screen long enough to read.
///
/// Args: &lt;parentPid&gt; &lt;zipPath&gt; &lt;targetDir&gt; &lt;relaunchExePath&gt;
/// </summary>
internal static class Program
{
    // A fixed 300ms x 10 (≈2.7s total) proved far too short in practice - antivirus/EDR real-time
    // scanning of newly-written EXE/DLLs (or an indexer/backup agent briefly opening them) can
    // hold a lock for several seconds after SqlPhanos.exe itself has fully exited, well outside
    // that window, causing this to report "files in use" on every single update. Exponential
    // backoff up to a ~70s total budget gives that kind of external, invisible-to-the-user lock
    // realistic time to clear, while still failing eventually instead of hanging forever.
    private const int MaxExtractAttempts = 20;
    private static readonly TimeSpan InitialExtractRetryDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaxExtractRetryDelay = TimeSpan.FromSeconds(5);
    private const double ExtractRetryBackoffFactor = 1.5;
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

        try
        {
            UpdateLog.Write("Update started.");

            Console.WriteLine("Waiting for SqlPhanos to close...");
            WaitForParentExit(parentPid);

            Console.WriteLine("Applying update...");
            if (!ExtractWithRetry(zipPath, targetDir))
            {
                Fail("Could not apply the update (files may still be in use).");
                return;
            }

            Console.WriteLine("Update applied.");

            try
            {
                File.Delete(zipPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup only - a leftover temp zip isn't worth failing the update over.
            }

            Console.WriteLine("Relaunching SqlPhanos...");
            try
            {
                Process.Start(new ProcessStartInfo(relaunchExePath)
                {
                    WorkingDirectory = targetDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                UpdateLog.Write($"Relaunch failed: {ex}");
                Fail("Could not restart SqlPhanos.");
                return;
            }

            UpdateLog.Write("Update completed and SqlPhanos relaunched successfully.");
        }
        catch (Exception ex)
        {
            UpdateLog.Write($"Unexpected update failure: {ex}");
            Fail("Update failed unexpectedly.");
        }
    }

    private static void Fail(string reason)
    {
        Console.WriteLine($"Update failed: {reason}");
        Console.WriteLine($"See log for details: {UpdateLog.FilePath}");
        Console.WriteLine("Press Enter to close.");
        Console.ReadLine();
    }

    private static void WaitForParentExit(int parentPid)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            var exited = parent.WaitForExit((int)ParentExitTimeout.TotalMilliseconds);
            UpdateLog.Write(exited
                ? "Parent process exited."
                : $"Parent process did not exit within {ParentExitTimeout.TotalSeconds}s - continuing anyway.");
        }
        catch (ArgumentException)
        {
            // Already exited before this process could even look it up - nothing to wait for.
            UpdateLog.Write("Parent process had already exited.");
        }
    }

    private static bool ExtractWithRetry(string zipPath, string targetDir)
    {
        var delay = InitialExtractRetryDelay;
        for (var attempt = 1; attempt <= MaxExtractAttempts; attempt++)
        {
            try
            {
                ZipFile.ExtractToDirectory(zipPath, targetDir, overwriteFiles: true);
                UpdateLog.Write($"Extraction succeeded on attempt {attempt}.");
                return true;
            }
            catch (IOException ex) when (attempt < MaxExtractAttempts)
            {
                // SqlPhanos.exe/its DLLs, or something external (AV scan, indexer, backup agent),
                // may not have fully released their file handles yet.
                UpdateLog.Write($"Extraction attempt {attempt} failed, retrying in {delay.TotalSeconds:0.#}s: {ex.Message}");
                Console.WriteLine($"Files still in use, retrying ({attempt}/{MaxExtractAttempts})...");
                Thread.Sleep(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(
                    delay.TotalMilliseconds * ExtractRetryBackoffFactor,
                    MaxExtractRetryDelay.TotalMilliseconds));
            }
            catch (IOException ex)
            {
                UpdateLog.Write($"Extraction failed after {MaxExtractAttempts} attempts: {ex}");
                return false;
            }
        }

        return false;
    }
}
