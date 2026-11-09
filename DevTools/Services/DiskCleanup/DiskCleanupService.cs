using System.Diagnostics;
using DevTools.Infrastructure;

namespace DevTools.Services.DiskCleanup;

/// <summary>
/// Runs the reviewed laptop cleanup at C:\dev\ai-tools\scripts\free-up-disk-space.ps1.
/// The PowerShell script is the single implementation; the app only previews and executes it,
/// so the Storage button and the daily scheduled task can never drift apart.
/// </summary>
public sealed class DiskCleanupService
{
    public static readonly string DefaultScriptPath =
        Path.Combine(@"C:\dev\ai-tools\scripts", "free-up-disk-space.ps1");

    private static readonly string[] LaneSwitches = ["-Caches", "-BuildArtifacts", "-PublishedVideos", "-GitDebris"];

    private readonly string? _powerShellPath;
    private readonly string? _scriptPath;

    public DiskCleanupService(string? powerShellPath = null, string? scriptPath = null)
    {
        _powerShellPath = powerShellPath ?? ResolvePowerShellPath();
        _scriptPath = scriptPath ?? DefaultScriptPath;
    }

    /// <summary>Finds PowerShell 7, which the cleanup script needs for its ternary syntax.</summary>
    public static string? ResolvePowerShellPath()
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "PowerShell", "7", "pwsh.exe"),
            @"C:\Program Files\PowerShell\7\pwsh.exe"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(entry.Trim(), "pwsh.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not fatal; keep looking.
            }
        }

        return null;
    }

    public string? ScriptPath => _scriptPath;

    public bool IsAvailable => _powerShellPath is not null && File.Exists(_scriptPath);

    /// <summary>Explains why the button is disabled. Returns null when the cleanup can run.</summary>
    public string? GetUnavailableReason()
    {
        if (_powerShellPath is null)
        {
            return "PowerShell 7 (pwsh.exe) was not found. Install it from https://aka.ms/powershell.";
        }

        if (!File.Exists(_scriptPath))
        {
            return $"Cleanup script not found: {_scriptPath}";
        }

        return null;
    }

    /// <summary>
    /// Runs the cleanup. <paramref name="execute"/> false is a dry run that deletes nothing.
    /// Report lines stream to <paramref name="progress"/> as they arrive.
    /// </summary>
    public async Task<DiskCleanupResult> RunAsync(
        bool execute,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var unavailable = GetUnavailableReason();
        if (_powerShellPath is null || unavailable is not null)
        {
            return new DiskCleanupResult(false, false, false, -1, unavailable ?? "Cleanup is unavailable.", DiskCleanupReport.Empty, []);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _powerShellPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(_scriptPath!);
        foreach (var laneSwitch in LaneSwitches)
        {
            startInfo.ArgumentList.Add(laneSwitch);
        }

        if (execute)
        {
            startInfo.ArgumentList.Add("-Execute");
        }

        var lines = new List<string>();
        var effectiveTimeout = timeout ?? TimeSpan.FromHours(4);
        var timedOut = false;

        AppLog.Info($"Disk cleanup starting. Execute={execute}, script={_scriptPath}.");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new DiskCleanupResult(false, false, false, -1, "Could not start PowerShell.", DiskCleanupReport.Empty, lines);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Disk cleanup could not start.", ex);
            return new DiskCleanupResult(false, false, false, -1, ex.Message, DiskCleanupReport.Empty, lines);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(effectiveTimeout);

        var stderrTask = ReadLinesAsync(process.StandardError, lines, progress, CancellationToken.None);
        var stdoutTask = ReadLinesAsync(process.StandardOutput, lines, progress, CancellationToken.None);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                AppLog.Info($"Disk cleanup kill failed: {ex.Message}");
            }
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        var exitCode = timedOut || process.HasExited is false ? -1 : process.ExitCode;
        var report = DiskCleanupReport.Parse(lines);
        var succeeded = exitCode == 0 && !timedOut;

        string message;
        if (timedOut)
        {
            message = $"Cleanup stopped after {effectiveTimeout.TotalHours:0.#} hours.";
        }
        else if (cancellationToken.IsCancellationRequested)
        {
            message = "Cleanup canceled.";
        }
        else if (succeeded)
        {
            message = execute
                ? $"Cleanup finished. {FormatSize(report.TotalReclaimedBytes)} reclaimed."
                : $"Preview finished. {FormatSize(report.TotalReclaimableBytes)} reclaimable.";
        }
        else
        {
            message = $"Cleanup failed with exit code {exitCode}.";
        }

        AppLog.Info($"Disk cleanup finished. ExitCode={exitCode}, TimedOut={timedOut}, Lines={lines.Count}.");
        return new DiskCleanupResult(true, succeeded, timedOut, exitCode, message, report, lines);
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        List<string> sink,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                lock (sink)
                {
                    sink.Add(line);
                }

                progress?.Report(line);
            }
        }
        catch (Exception ex)
        {
            AppLog.Info($"Disk cleanup stream ended: {ex.Message}");
        }
    }

    public static string FormatSize(long? bytes)
    {
        return bytes is null ? "an unknown amount" : ByteFormatter.Format(bytes.Value);
    }
}
