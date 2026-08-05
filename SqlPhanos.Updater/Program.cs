using System;
using System.Collections.Generic;
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
/// Extraction walks the zip entry-by-entry (instead of ZipFile.ExtractToDirectory) so a locked
/// file can be identified and retried individually rather than restarting the whole archive -
/// and so the generic "files may still be in use" message can be replaced with the actual
/// file(s) that failed. Per-file progress overwrites itself in place (CR only, no LF) so a
/// several-hundred-file update doesn't scroll the console; a file that fails after exhausting
/// its retries is printed permanently, in yellow, so it isn't lost when later files overwrite
/// the progress line. If anything failed, the process waits for a keypress instead of exiting so
/// the failure list stays on screen; only a fully clean extraction relaunches automatically.
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
    private static readonly TimeSpan ParentExitTimeout = TimeSpan.FromSeconds(60);

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
                Fail("Could not apply the update - see the failed file(s) highlighted above (files may still be in use).");
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
        Console.WriteLine("Press any key to exit.");
        try
        {
            Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            // Stdin redirected (no console input to read a key from) - fall back to a line read.
            Console.ReadLine();
        }
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
        using var archive = ZipFile.OpenRead(zipPath);

        var total = 0;
        foreach (var entry in archive.Entries)
        {
            if (!string.IsNullOrEmpty(entry.Name))
                total++;
        }

        // ZipFile.ExtractToDirectory refuses entries that would land outside targetDir; since
        // extraction is now manual, that guard has to be reimplemented here.
        var targetDirFull = Path.GetFullPath(targetDir + Path.DirectorySeparatorChar);
        var failures = new List<string>();
        var current = 0;

        foreach (var entry in archive.Entries)
        {
            var destPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
            if (!destPath.StartsWith(targetDirFull, StringComparison.OrdinalIgnoreCase))
            {
                UpdateLog.Write($"Skipped entry outside the target directory: {entry.FullName}");
                continue;
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                // Directory entry - just needs to exist for the files inside it.
                Directory.CreateDirectory(destPath);
                continue;
            }

            current++;
            if (!ExtractEntryWithRetry(entry, destPath, current, total))
                failures.Add(entry.FullName);
        }

        EndProgressLine();

        if (failures.Count > 0)
        {
            UpdateLog.Write($"Extraction finished with {failures.Count} failed file(s): {string.Join(", ", failures)}");
            return false;
        }

        UpdateLog.Write($"Extraction succeeded for all {total} file(s).");
        return true;
    }

    private static bool ExtractEntryWithRetry(ZipArchiveEntry entry, string destPath, int current, int total)
    {
        var delay = InitialExtractRetryDelay;
        for (var attempt = 1; attempt <= MaxExtractAttempts; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
                WriteProgress($"Extracting {current}/{total}: {entry.FullName}");
                return true;
            }
            catch (IOException ex) when (attempt < MaxExtractAttempts)
            {
                // SqlPhanos.exe/its DLLs, or something external (AV scan, indexer, backup agent),
                // may not have fully released their file handles yet.
                UpdateLog.Write($"Extracting '{entry.FullName}' attempt {attempt} failed, retrying in {delay.TotalSeconds:0.#}s: {ex.Message}");
                WriteProgress($"Extracting {current}/{total}: {entry.FullName} (files in use, retry {attempt}/{MaxExtractAttempts})...");
                Thread.Sleep(delay);
                delay = TimeSpan.FromMilliseconds(Math.Min(
                    delay.TotalMilliseconds * ExtractRetryBackoffFactor,
                    MaxExtractRetryDelay.TotalMilliseconds));
            }
            catch (IOException ex)
            {
                UpdateLog.Write($"Extracting '{entry.FullName}' failed after {MaxExtractAttempts} attempts: {ex}");
                WriteFailureLine($"FAILED: {entry.FullName} - {ex.Message}");
                return false;
            }
        }

        return false;
    }

    // Progress is drawn with CR only (never LF) so each successful file overwrites the previous
    // line in place instead of scrolling the console for every one of a few hundred files.
    private static int _progressLineLength;

    private static void WriteProgress(string text)
    {
        var padded = text.Length < _progressLineLength ? text.PadRight(_progressLineLength) : text;
        Console.Write('\r' + padded);
        _progressLineLength = text.Length;
    }

    // Moves past the in-place progress line so subsequent output (a failure, or the final
    // "Update applied.") starts on its own line instead of overwriting the last file shown.
    private static void EndProgressLine()
    {
        if (_progressLineLength > 0)
        {
            Console.WriteLine();
            _progressLineLength = 0;
        }
    }

    private static void WriteFailureLine(string text)
    {
        EndProgressLine();
        var previousColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(text);
        Console.ForegroundColor = previousColor;
    }
}
