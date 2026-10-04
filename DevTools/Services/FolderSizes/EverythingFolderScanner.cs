using System.Diagnostics;
using System.Text;
using DevTools.Infrastructure;

namespace DevTools.Services.FolderSizes;

/// <summary>What a quick Everything (voidtools) health check found for one drive.</summary>
public sealed record EverythingProbe(
    bool IsAvailable,
    bool IndexesDrive,
    bool HasFolderSizes,
    string? EsPath,
    string Reason);

/// <summary>
/// Folder sizes from the Everything index through its command-line client <c>es.exe</c>.
/// Everything already holds every file name and size in memory, so nothing on the disk is read.
/// Every call has a hard timeout and kills only the <c>es.exe</c> process it started.
/// </summary>
public sealed class EverythingFolderScanner
{
    public const string EngineName = "Everything";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StallWindow = TimeSpan.FromSeconds(60);
    private const long StallMinimumLines = 50_000;
    // The drive check is a real search over the whole index; when Everything has been paged out
    // under memory pressure the first search can take several seconds (measured 4.2 s).
    private static readonly TimeSpan DriveCheckTimeout = TimeSpan.FromSeconds(15);

    public static string? ResolveEsPath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "es.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Everything", "es.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Everything", "es.exe"),
            Path.Combine(profile, ".local", "bin", "es.exe"),
            Path.Combine(profile, "bin", "es.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links", "es.exe")
        };

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                candidates.Add(Path.Combine(directory.Trim().Trim('"'), "es.exe"));
            }
            catch (ArgumentException)
            {
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Checks, within a few seconds, that Everything answers, that it indexes the drive, and
    /// whether it keeps folder sizes ("Index folder size" in Everything's options).
    /// </summary>
    public async Task<EverythingProbe> ProbeAsync(string driveRoot, CancellationToken cancellationToken)
    {
        var root = FolderTree.NormalizeRoot(driveRoot);
        var esPath = ResolveEsPath();
        if (esPath is null)
        {
            return new EverythingProbe(false, false, false, null, "es.exe (the Everything command-line client) was not found.");
        }

        var version = await RunCaptureAsync(esPath, ["-get-everything-version"], ProbeTimeout, cancellationToken)
            ?? await RunCaptureAsync(esPath, ["-get-everything-version"], ProbeTimeout, cancellationToken);
        if (version is null || version.ExitCode != 0 || string.IsNullOrWhiteSpace(version.Output))
        {
            return new EverythingProbe(false, false, false, esPath,
                $"Everything did not answer within {ProbeTimeout.TotalSeconds:0} s (is the Everything app running?). {version?.Output.Trim()}".Trim());
        }

        // One folder at the drive root proves the drive is in the index.
        var folder = await RunCaptureAsync(
            esPath,
            ["-n", "1", "-size", "-no-digit-grouping", "-size-format", "1", "/ad", "-parent", root],
            DriveCheckTimeout,
            cancellationToken);
        if (folder is null || folder.ExitCode != 0 || string.IsNullOrWhiteSpace(folder.Output))
        {
            return new EverythingProbe(true, false, false, esPath, $"Everything {version.Output.Trim()} is running but does not index {root}.");
        }

        var hasFolderSizes = TryParseLine(folder.Output.Split('\n')[0], out var size, out _) && size >= 0;
        var reason = hasFolderSizes
            ? $"Everything {version.Output.Trim()} indexes {root} with folder sizes."
            : $"Everything {version.Output.Trim()} indexes {root} without folder sizes (turn on Tools > Options > Indexes > Index folder size for an instant scan).";
        return new EverythingProbe(true, true, hasFolderSizes, esPath, reason);
    }

    /// <summary>
    /// Streams every folder (with folder sizes) or every file (without) on the drive from
    /// Everything and builds the folder tree. Throws <see cref="TimeoutException"/> after
    /// <paramref name="timeout"/>.
    /// </summary>
    public Task<FolderTree> ScanAsync(
        string driveRoot,
        EverythingProbe probe,
        IProgress<FolderScanProgress>? progress,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!probe.IndexesDrive || probe.EsPath is null)
        {
            throw new InvalidOperationException("Everything does not index this drive. Call ProbeAsync first.");
        }

        return Task.Run(() => ScanCore(driveRoot, probe, progress, timeout, cancellationToken), cancellationToken);
    }

    private static FolderTree ScanCore(
        string driveRoot,
        EverythingProbe probe,
        IProgress<FolderScanProgress>? progress,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var root = FolderTree.NormalizeRoot(driveRoot);
        var stopwatch = Stopwatch.StartNew();
        var builder = new FolderTreeBuilder(root);
        var startInfo = new ProcessStartInfo
        {
            FileName = probe.EsPath!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in new[] { "-size", "-no-digit-grouping", "-size-format", "1", "-sort", "path", probe.HasFolderSizes ? "/ad" : "/a-d", "-path", root })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("es.exe did not start.");
        TrySetLowPriority(process);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var registration = timeoutCts.Token.Register(() => TryKill(process));
        _ = process.StandardError.ReadToEndAsync();

        // Stall guard: when Everything is paged out (low RAM) the listing can crawl at a few
        // hundred lines a second. Give up if a whole minute adds fewer than StallMinimumLines.
        long lines = 0;
        long linesAtLastCheck = 0;
        var stalled = false;
        using var stallTimer = new Timer(_ =>
        {
            var now = Interlocked.Read(ref lines);
            if (now - linesAtLastCheck < StallMinimumLines)
            {
                stalled = true;
                TryKill(process);
            }

            linesAtLastCheck = now;
        }, null, StallWindow * 2, StallWindow);
        var lastReport = stopwatch.Elapsed;
        string? line;
        while ((line = process.StandardOutput.ReadLine()) is not null)
        {
            if (!TryParseLine(line, out var size, out var path))
            {
                continue;
            }

            if (probe.HasFolderSizes)
            {
                builder.AddFolder(path, Math.Max(0, size));
            }
            else
            {
                builder.AddFile(path, size);
            }

            Interlocked.Increment(ref lines);
            if (stopwatch.Elapsed - lastReport > TimeSpan.FromMilliseconds(400))
            {
                lastReport = stopwatch.Elapsed;
                progress?.Report(new FolderScanProgress(root, EngineName, $"Reading the Everything index: {lines:N0} {(probe.HasFolderSizes ? "folders" : "files")}.", lines, stopwatch.Elapsed));
            }
        }

        process.WaitForExit();
        if (stalled)
        {
            throw new TimeoutException($"Everything listed only {lines:N0} results for {root} and slowed below {StallMinimumLines:N0} a minute (Everything is probably short of memory).");
        }

        if (timeoutCts.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"Everything did not finish listing {root} within {timeout.TotalSeconds:0} s.");
        }

        if (process.ExitCode != 0 || lines == 0)
        {
            throw new InvalidOperationException($"es.exe returned exit code {process.ExitCode} with {lines} results for {root}.");
        }

        stopwatch.Stop();
        AppLog.Info($"Everything listed {lines:N0} {(probe.HasFolderSizes ? "folders" : "files")} on {root} in {stopwatch.Elapsed.TotalSeconds:F1}s.");
        return builder.Build(EngineName, stopwatch.Elapsed);
    }

    /// <summary>Parses one es.exe line: right-aligned size, one space, full path.</summary>
    internal static bool TryParseLine(string line, out long size, out string path)
    {
        size = -1;
        path = string.Empty;
        var trimmed = line.TrimStart().TrimEnd('\r', '\n');
        var space = trimmed.IndexOf(' ');
        if (space <= 0)
        {
            return false;
        }

        if (!long.TryParse(trimmed.AsSpan(0, space), out size))
        {
            size = -1;
            return false;
        }

        path = trimmed[(space + 1)..];
        return path.Length >= 2 && path[1] == ':';
    }

    private static async Task<CapturedOutput?> RunCaptureAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process? process = null;
        try
        {
            process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return new CapturedOutput(process.ExitCode, string.IsNullOrWhiteSpace(output) ? error : output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (process is not null)
            {
                TryKill(process);
            }

            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Info($"es.exe could not run: {ex.Message}");
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static void TrySetLowPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
        }
    }

    private sealed record CapturedOutput(int ExitCode, string Output);
}
