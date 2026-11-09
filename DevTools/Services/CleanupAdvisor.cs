using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using DevTools.Infrastructure;
using DevTools.Models;

namespace DevTools.Services;

/// <summary>
/// Decides which cleanups are worth offering on this machine, how much each would free, and runs the one the
/// person picks. Every option only ever deletes inside the folders or Docker objects it names.
/// </summary>
public sealed class CleanupAdvisor
{
    public const string TempKey = "temp";
    public const string VsCodeKey = "vscode";
    public const string NuGetKey = "nuget";
    public const string PackageCachesKey = "package-caches";
    public const string DockerLeftoversKey = "docker-leftovers";
    public const string PlaywrightKey = "playwright";
    public const string DockerUnusedKey = "docker-unused";
    public const string DockerCompactKey = "docker-compact";
    public const string WorktreesKey = "worktrees";
    public const string RecycleBinKey = "recycle-bin";
    public const string DownloadsKey = "downloads";

    private static readonly TimeSpan DockerProbeTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DockerPruneTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NuGetClearTimeout = TimeSpan.FromMinutes(10);

    private readonly DiskUsageAnalyzer _diskUsageAnalyzer;
    private Lazy<Task<DockerFacts>> _dockerFacts;

    public CleanupAdvisor(DiskUsageAnalyzer diskUsageAnalyzer)
    {
        _diskUsageAnalyzer = diskUsageAnalyzer;
        _dockerFacts = new Lazy<Task<DockerFacts>>(() => ReadDockerFactsAsync(CancellationToken.None));
    }

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static string TempPath => Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string PlaywrightPath => Path.Combine(LocalAppData, "ms-playwright");

    private static string DownloadsPath => Path.Combine(UserProfile, "Downloads");

    private static string DockerCompactScriptPath => Path.Combine(ResolveRepositoryRootPath(), "scripts", "free-up-docker-space.ps1");

    private static IReadOnlyList<string> VsCodeCachePaths =>
    [
        Path.Combine(RoamingAppData, "Code", "User", "workspaceStorage"),
        Path.Combine(RoamingAppData, "Code - Insiders", "User", "workspaceStorage"),
        Path.Combine(RoamingAppData, "Code", "CachedExtensionVSIXs"),
        Path.Combine(RoamingAppData, "Code - Insiders", "CachedExtensionVSIXs")
    ];

    private static IReadOnlyList<string> NuGetCachePaths =>
    [
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured ? configured : Path.Combine(UserProfile, ".nuget", "packages"),
        Path.Combine(LocalAppData, "NuGet", "v3-cache"),
        Path.Combine(LocalAppData, "NuGet", "plugins-cache")
    ];

    private static IReadOnlyList<string> PackageCachePaths =>
    [
        Path.Combine(LocalAppData, "npm-cache"),
        Path.Combine(LocalAppData, "pip", "cache"),
        Path.Combine(LocalAppData, "Yarn", "Cache")
    ];

    /// <summary>The options in the order they are recommended: safe first, then careful, then the person's call.</summary>
    public IReadOnlyList<CleanupOption> CreateOptions()
    {
        // Docker facts are read once per check and shared by the three Docker options.
        _dockerFacts = new Lazy<Task<DockerFacts>>(() => ReadDockerFactsAsync(CancellationToken.None));
        var keepHours = CleanupRules.TempKeepWindow.TotalHours;

        return
        [
            new CleanupOption(
                TempKey,
                "Old temp files",
                CleanupVerdict.Safe,
                $"Installers, logs, and crash leftovers. Anything touched in the last {keepHours:0} hours is kept, so running apps and open AI sessions are not affected.",
                $"Deletes files in {TempPath} that have not changed in the last {keepHours:0} hours. Locked files are skipped.",
                "Clean",
                CleanupActionKind.Clean,
                TempPath),
            new CleanupOption(
                VsCodeKey,
                "VS Code project caches",
                CleanupVerdict.Safe,
                "Per-project search indexes and old extension installers. VS Code rebuilds them the next time you open a project. Close VS Code first for the best result.",
                "Empties workspaceStorage and CachedExtensionVSIXs for VS Code and VS Code Insiders. Settings, extensions, and recent projects are kept.",
                "Clean",
                CleanupActionKind.Clean),
            new CleanupOption(
                NuGetKey,
                "NuGet package cache",
                CleanupVerdict.Safe,
                "Every package version ever restored. The only cost is a slower first restore, which downloads what it needs again. Do not run it while a build or restore is running.",
                "Runs 'dotnet nuget locals all --clear'.",
                "Clean",
                CleanupActionKind.Clean),
            new CleanupOption(
                PackageCachesKey,
                "npm, pip, and Yarn caches",
                CleanupVerdict.Safe,
                "Download caches only. Installed tools and project node_modules folders are not touched; anything missing is downloaded again on first use.",
                "Empties the npm-cache, pip cache, and Yarn cache folders under Local AppData.",
                "Clean",
                CleanupActionKind.Clean),
            new CleanupOption(
                DockerLeftoversKey,
                "Docker build cache and dangling images",
                CleanupVerdict.Safe,
                "Untagged image layers and build cache that nothing uses. Running containers, tagged images, and volumes are kept. The space is freed inside Docker's disk; use 'Shrink Docker disk' afterwards to hand it back to Windows.",
                "Runs 'docker builder prune -f' and 'docker image prune -f'.",
                "Clean",
                CleanupActionKind.Clean),
            new CleanupOption(
                PlaywrightKey,
                "Older Playwright browsers",
                CleanupVerdict.Careful,
                "A newer copy of the same browser is already installed. If a tool still pins the old version, it downloads it again the first time it runs.",
                "Deletes the older browser folders under ms-playwright and keeps the newest of each browser and all Playwright MCP profiles.",
                "Clean",
                CleanupActionKind.Clean,
                PlaywrightPath),
            new CleanupOption(
                DockerUnusedKey,
                "Unused Docker images and stopped containers",
                CleanupVerdict.Careful,
                "Checking Docker...",
                "Runs 'docker container prune -f' and 'docker image prune -a -f'. Images used by running containers and all volumes are kept.",
                "Clean",
                CleanupActionKind.Clean),
            new CleanupOption(
                DockerCompactKey,
                "Shrink Docker disk",
                CleanupVerdict.YourCall,
                "Checking Docker...",
                $"Opens an administrator PowerShell window running {DockerCompactScriptPath}. It asks you to press Enter, prunes Docker leftovers, stops Docker and WSL, then compacts the disk file. Start Docker again when it finishes.",
                "Open admin window",
                CleanupActionKind.LaunchElevated,
                DockerCompactScriptPath),
            new CleanupOption(
                WorktreesKey,
                "Merged git worktrees",
                CleanupVerdict.YourCall,
                "Whole working copies whose branch is already merged. The Worktrees tab checks each one for uncommitted files before it lets you remove it.",
                "Opens the Worktrees tab filtered to 'Safe to remove'.",
                "Review worktrees",
                CleanupActionKind.Navigate),
            new CleanupOption(
                RecycleBinKey,
                "Recycle Bin",
                CleanupVerdict.YourCall,
                "Files you already deleted. Emptying it is permanent, so look inside first if you are not sure.",
                "Empties the Recycle Bin on every drive. This cannot be undone.",
                "Empty",
                CleanupActionKind.Clean),
            new CleanupOption(
                DownloadsKey,
                "Downloads folder",
                CleanupVerdict.YourCall,
                "Personal files, so DevTools never deletes them for you. Open the folder and sort by size.",
                "Opens the Downloads folder in Explorer.",
                "Open folder",
                CleanupActionKind.OpenFolder,
                DownloadsPath)
        ];
    }

    public async Task<CleanupEstimate> EstimateAsync(CleanupOption option, CancellationToken cancellationToken)
    {
        try
        {
            return option.Key switch
            {
                TempKey => await EstimateTempAsync(cancellationToken),
                VsCodeKey => await MeasureFoldersAsync(VsCodeCachePaths, cancellationToken),
                NuGetKey => await MeasureFoldersAsync(NuGetCachePaths, cancellationToken),
                PackageCachesKey => await MeasureFoldersAsync(PackageCachePaths, cancellationToken),
                PlaywrightKey => await EstimatePlaywrightAsync(cancellationToken),
                DockerLeftoversKey => await EstimateDockerLeftoversAsync(),
                DockerUnusedKey => await EstimateDockerUnusedAsync(),
                DockerCompactKey => await EstimateDockerCompactAsync(),
                RecycleBinKey => await EstimateRecycleBinAsync(cancellationToken),
                DownloadsKey => await MeasureFoldersAsync([DownloadsPath], cancellationToken),
                WorktreesKey => new CleanupEstimate(null, "See Worktrees tab"),
                _ => new CleanupEstimate(null, "Unknown", IsAvailable: false)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Cleanup estimate failed for {option.Key}.", ex);
            return new CleanupEstimate(null, "Unavailable", IsAvailable: false, Reason: $"Could not be checked: {ex.Message}");
        }
    }

    /// <summary>Runs one option and returns a plain sentence about what it did.</summary>
    public async Task<string> RunAsync(CleanupOption option, CancellationToken cancellationToken)
    {
        AppLog.Info($"Cleanup started: {option.Key}.");
        var result = option.Key switch
        {
            TempKey => await DeleteAsync([TempPath], OlderThanKeepWindow(), cancellationToken),
            VsCodeKey => await DeleteAsync(VsCodeCachePaths, _ => true, cancellationToken),
            NuGetKey => await ClearNuGetAsync(cancellationToken),
            PackageCachesKey => await DeleteAsync(PackageCachePaths, _ => true, cancellationToken),
            PlaywrightKey => await DeleteAsync(FindSupersededPlaywrightFolders(), _ => true, cancellationToken, removeRoots: true),
            DockerLeftoversKey => await RunDockerPrunesAsync([["builder", "prune", "-f"], ["image", "prune", "-f"]], cancellationToken),
            DockerUnusedKey => await RunDockerPrunesAsync([["container", "prune", "-f"], ["image", "prune", "-a", "-f"]], cancellationToken),
            DockerCompactKey => LaunchDockerCompaction(),
            RecycleBinKey => EmptyRecycleBin(),
            _ => throw new InvalidOperationException($"'{option.Title}' is not a delete action.")
        };
        AppLog.Info($"Cleanup finished: {option.Key}. {result}");
        return result;
    }

    private static Func<FileSystemInfo, bool> OlderThanKeepWindow()
    {
        var cutoffUtc = DateTime.UtcNow - CleanupRules.TempKeepWindow;
        return item => CleanupRules.IsOlderThan(item, cutoffUtc);
    }

    private static async Task<CleanupEstimate> EstimateTempAsync(CancellationToken cancellationToken)
    {
        var tally = await Task.Run(() => CleanupRules.DeleteFiles(TempPath, OlderThanKeepWindow(), dryRun: true, cancellationToken), cancellationToken);
        return new CleanupEstimate(tally.Bytes, CleanupRules.FormatBytes(tally.Bytes));
    }

    private async Task<CleanupEstimate> MeasureFoldersAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var existing = paths.Where(Directory.Exists).ToList();
        if (existing.Count == 0)
        {
            return new CleanupEstimate(0, "Nothing here", IsAvailable: false);
        }

        long total = 0;
        foreach (var path in existing)
        {
            total += await _diskUsageAnalyzer.MeasureDirectoryBytesAsync(path, cancellationToken) ?? 0;
        }

        return new CleanupEstimate(total, CleanupRules.FormatBytes(total), IsAvailable: total > 0);
    }

    private static IReadOnlyList<string> FindSupersededPlaywrightFolders()
    {
        if (!Directory.Exists(PlaywrightPath))
        {
            return [];
        }

        var names = Directory.EnumerateDirectories(PlaywrightPath).Select(Path.GetFileName).OfType<string>();
        return CleanupRules.FindSupersededBrowserFolders(names).Select(name => Path.Combine(PlaywrightPath, name)).ToList();
    }

    private async Task<CleanupEstimate> EstimatePlaywrightAsync(CancellationToken cancellationToken)
    {
        var folders = FindSupersededPlaywrightFolders();
        if (folders.Count == 0)
        {
            return new CleanupEstimate(0, "Nothing here", IsAvailable: false, Reason: "Only the newest copy of each Playwright browser is installed.");
        }

        var estimate = await MeasureFoldersAsync(folders, cancellationToken);
        return estimate with
        {
            WhatHappens = $"Deletes {string.Join(", ", folders.Select(Path.GetFileName))} under {PlaywrightPath}. The newest copy of each browser and all Playwright MCP profiles are kept."
        };
    }

    private static async Task<string> DeleteAsync(IEnumerable<string> roots, Func<FileSystemInfo, bool> shouldDelete, CancellationToken cancellationToken, bool removeRoots = false)
    {
        var total = new CleanupTally();
        await Task.Run(() =>
        {
            foreach (var root in roots)
            {
                var tally = CleanupRules.DeleteFiles(root, shouldDelete, dryRun: false, cancellationToken);
                total.Bytes += tally.Bytes;
                total.FileCount += tally.FileCount;
                total.SkippedCount += tally.SkippedCount;
                if (removeRoots && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                {
                    Directory.Delete(root);
                }
            }
        }, cancellationToken);

        var skipped = total.SkippedCount == 0 ? string.Empty : $" {total.SkippedCount:N0} locked or protected items were left in place.";
        return $"Removed {total.FileCount:N0} files ({CleanupRules.FormatBytes(total.Bytes)}).{skipped}";
    }

    private static async Task<string> ClearNuGetAsync(CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync("dotnet", ["nuget", "locals", "all", "--clear"], NuGetClearTimeout, cancellationToken);
        if (result.ExitCode == 0)
        {
            return "NuGet caches cleared.";
        }

        // dotnet reports which folders were in use; pass that on rather than claiming success.
        var detail = FirstLines(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error, 3);
        return $"NuGet could not clear everything (exit code {result.ExitCode}). Close Visual Studio and stop builds, then try again. {detail}".Trim();
    }

    private async Task<CleanupEstimate> EstimateDockerLeftoversAsync()
    {
        var facts = await _dockerFacts.Value;
        if (!facts.IsRunning)
        {
            return DockerNotRunning();
        }

        var bytes = facts.BuildCacheReclaimableBytes + facts.Images.Where(image => image.IsDangling).Sum(image => image.SizeBytes);
        return bytes > 0
            ? new CleanupEstimate(bytes, $"up to {CleanupRules.FormatBytes(bytes)}")
            : new CleanupEstimate(0, "Nothing here", IsAvailable: false);
    }

    private async Task<CleanupEstimate> EstimateDockerUnusedAsync()
    {
        var facts = await _dockerFacts.Value;
        if (!facts.IsRunning)
        {
            return DockerNotRunning();
        }

        var unusedImages = facts.Images.Where(image => !facts.RunningImages.Contains(image.Name) && !facts.RunningImages.Contains(image.Id)).ToList();
        var stopped = facts.StoppedContainers;
        if (unusedImages.Count == 0 && stopped.Count == 0)
        {
            return new CleanupEstimate(0, "Nothing here", IsAvailable: false, Reason: "Every image is used by a running container and there are no stopped containers.");
        }

        var parts = new List<string>();
        if (stopped.Count > 0)
        {
            parts.Add($"Removes {stopped.Count} stopped container{(stopped.Count == 1 ? string.Empty : "s")} ({string.Join(", ", stopped.Select(container => $"{container.Name}, {container.Status.ToLowerInvariant()}"))}).");
        }

        if (unusedImages.Count > 0)
        {
            parts.Add($"Removes {unusedImages.Count} image{(unusedImages.Count == 1 ? string.Empty : "s")} no running container uses ({string.Join(", ", unusedImages.Select(image => image.Name))}); they download again if you need them.");
        }

        var dataHolders = stopped.Where(container => CleanupRules.IsDataHoldingImage(container.Image)).ToList();
        if (dataHolders.Count > 0)
        {
            parts.Add($"Heads-up: {string.Join(", ", dataHolders.Select(container => container.Name))} may hold data (databases or uploaded files) that is lost with the container.");
        }

        parts.Add("Running containers and volumes are kept. The space is freed inside Docker's disk; use 'Shrink Docker disk' afterwards.");
        var bytes = unusedImages.Sum(image => image.SizeBytes) + facts.ContainersReclaimableBytes;
        return new CleanupEstimate(bytes, $"up to {CleanupRules.FormatBytes(bytes)}", Reason: string.Join(" ", parts));
    }

    private async Task<CleanupEstimate> EstimateDockerCompactAsync()
    {
        if (!File.Exists(DockerCompactScriptPath))
        {
            return new CleanupEstimate(null, "Script missing", IsAvailable: false, Reason: $"The compaction script was not found at {DockerCompactScriptPath}.");
        }

        var diskBytes = Directory.Exists(Path.Combine(LocalAppData, "Docker", "wsl"))
            ? Directory.EnumerateFiles(Path.Combine(LocalAppData, "Docker", "wsl"), "*.vhdx", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
            : 0;
        if (diskBytes == 0)
        {
            return new CleanupEstimate(null, "No Docker disk", IsAvailable: false, Reason: "No Docker Desktop disk file was found under Local AppData.");
        }

        var facts = await _dockerFacts.Value;
        if (!facts.IsRunning)
        {
            return new CleanupEstimate(null, "unknown", Reason: $"Docker's disk file is {CleanupRules.FormatBytes(diskBytes)}. Docker is stopped, so how much of it is free space cannot be checked; the script starts Docker to find out.");
        }

        var freeInside = Math.Max(0, diskBytes - facts.UsedBytes);
        var running = facts.RunningContainerCount;
        var stopsWhat = running == 0
            ? "Nothing is running in Docker right now, so this is a good time."
            : $"It stops Docker and WSL, so the {running} running container{(running == 1 ? string.Empty : "s")} go down until you start Docker again. Pick a moment when you are not testing.";
        return new CleanupEstimate(
            freeInside,
            $"about {CleanupRules.FormatBytes(freeInside)}",
            Reason: $"Docker's disk file is {CleanupRules.FormatBytes(diskBytes)} but Docker only uses {CleanupRules.FormatBytes(facts.UsedBytes)} of it; the file never shrinks by itself. {stopsWhat} Run the Docker cleanups above first to make this bigger.");
    }

    private static string LaunchDockerCompaction()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NoExit -ExecutionPolicy Bypass -File \"{DockerCompactScriptPath}\"",
                UseShellExecute = true,
                Verb = "runas"
            });
            return "Started in an administrator window. Follow it there, then press 'Check again' when it finishes.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Cancelled at the administrator prompt. Nothing was changed.";
        }
    }

    private static CleanupEstimate DockerNotRunning()
    {
        return new CleanupEstimate(null, "Docker stopped", IsAvailable: false, Reason: "Docker is not running, so this cannot be checked. Start Docker Desktop and press 'Check again'.");
    }

    private static async Task<string> RunDockerPrunesAsync(IReadOnlyList<string[]> commands, CancellationToken cancellationToken)
    {
        long reclaimed = 0;
        var problems = new List<string>();
        foreach (var arguments in commands)
        {
            var result = await RunCommandAsync("docker", arguments, DockerPruneTimeout, cancellationToken);
            if (result.ExitCode != 0)
            {
                problems.Add($"'docker {string.Join(' ', arguments)}' failed: {FirstLines(result.Error, 2)}");
                continue;
            }

            var reclaimedLine = result.Output.Split('\n').FirstOrDefault(line => line.StartsWith("Total reclaimed space:", StringComparison.OrdinalIgnoreCase)) ??
                                result.Output.Split('\n').FirstOrDefault(line => line.StartsWith("Total:", StringComparison.OrdinalIgnoreCase));
            reclaimed += CleanupRules.TryParseDockerSize(reclaimedLine?[(reclaimedLine.IndexOf(':') + 1)..]) ?? 0;
        }

        var summary = $"Docker reports {CleanupRules.FormatBytes(reclaimed)} reclaimed inside its disk. Use 'Shrink Docker disk' to hand it back to Windows.";
        return problems.Count == 0 ? summary : $"{summary} {string.Join(" ", problems)}";
    }

    private static async Task<CleanupEstimate> EstimateRecycleBinAsync(CancellationToken cancellationToken)
    {
        var tally = await Task.Run(() =>
        {
            var total = new CleanupTally();
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid is null)
            {
                return total;
            }

            foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady))
            {
                var binPath = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
                var driveTally = CleanupRules.DeleteFiles(binPath, _ => true, dryRun: true, cancellationToken);
                total.Bytes += driveTally.Bytes;
                total.FileCount += driveTally.FileCount;
            }

            return total;
        }, cancellationToken);

        return tally.Bytes > 0
            ? new CleanupEstimate(tally.Bytes, CleanupRules.FormatBytes(tally.Bytes))
            : new CleanupEstimate(0, "Empty", IsAvailable: false);
    }

    private static string EmptyRecycleBin()
    {
        const uint NoConfirmation = 0x1;
        const uint NoProgressUi = 0x2;
        const uint NoSound = 0x4;
        var result = SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirmation | NoProgressUi | NoSound);

        // S_OK, or E_UNEXPECTED when the bin was already empty.
        return result is 0 or unchecked((int)0x8000FFFF)
            ? "Recycle Bin emptied."
            : $"Windows could not empty the Recycle Bin (error 0x{result:X8}).";
    }

    [DllImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr windowHandle, string? rootPath, uint flags);

    private static async Task<DockerFacts> ReadDockerFactsAsync(CancellationToken cancellationToken)
    {
        var version = await RunCommandAsync("docker", ["version", "--format", "{{.Server.Version}}"], DockerProbeTimeout, cancellationToken);
        if (version.ExitCode != 0)
        {
            return DockerFacts.NotRunning;
        }

        var dfTask = RunCommandAsync("docker", ["system", "df", "--format", "{{json .}}"], DockerProbeTimeout, cancellationToken);
        var imagesTask = RunCommandAsync("docker", ["images", "--format", "{{.Repository}}:{{.Tag}}\t{{.ID}}\t{{.Size}}"], DockerProbeTimeout, cancellationToken);
        var runningTask = RunCommandAsync("docker", ["ps", "--format", "{{.Image}}"], DockerProbeTimeout, cancellationToken);
        var stoppedTask = RunCommandAsync("docker", ["ps", "-a", "--filter", "status=exited", "--filter", "status=created", "--filter", "status=dead", "--format", "{{.Names}}\t{{.Image}}\t{{.Status}}"], DockerProbeTimeout, cancellationToken);
        await Task.WhenAll(dfTask, imagesTask, runningTask, stoppedTask);

        long usedBytes = 0, buildCacheReclaimable = 0, containersReclaimable = 0;
        foreach (var line in SplitLines((await dfTask).Output))
        {
            using var row = JsonDocument.Parse(line);
            var type = row.RootElement.GetProperty("Type").GetString() ?? string.Empty;
            usedBytes += CleanupRules.TryParseDockerSize(row.RootElement.GetProperty("Size").GetString()) ?? 0;
            var reclaimable = CleanupRules.TryParseDockerSize(row.RootElement.GetProperty("Reclaimable").GetString()) ?? 0;
            if (type.Equals("Build Cache", StringComparison.OrdinalIgnoreCase))
            {
                buildCacheReclaimable = reclaimable;
            }
            else if (type.Equals("Containers", StringComparison.OrdinalIgnoreCase))
            {
                containersReclaimable = reclaimable;
            }
        }

        var images = SplitLines((await imagesTask).Output)
            .Select(line => line.Split('\t'))
            .Where(columns => columns.Length >= 3)
            .Select(columns => new DockerImage(columns[0], columns[1], CleanupRules.TryParseDockerSize(columns[2]) ?? 0))
            .ToList();
        var runningLines = SplitLines((await runningTask).Output);
        var stopped = SplitLines((await stoppedTask).Output)
            .Select(line => line.Split('\t'))
            .Where(columns => columns.Length >= 3)
            .Select(columns => new DockerContainer(columns[0], columns[1], columns[2]))
            .ToList();

        return new DockerFacts(
            true,
            usedBytes,
            buildCacheReclaimable,
            containersReclaimable,
            runningLines.Count,
            new HashSet<string>(runningLines, StringComparer.OrdinalIgnoreCase),
            images,
            stopped);
    }

    private static async Task<CommandResult> RunCommandAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex)
        {
            return new CommandResult(-1, string.Empty, $"{fileName} could not be started: {ex.Message}");
        }

        if (process is null)
        {
            return new CommandResult(-1, string.Empty, $"{fileName} could not be started.");
        }

        using (process)
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errorTask = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new CommandResult(-1, string.Empty, $"Timed out after {timeout.TotalSeconds:0} seconds.");
            }

            return new CommandResult(process.ExitCode, await outputTask, await errorTask);
        }
    }

    private static List<string> SplitLines(string text)
    {
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string FirstLines(string text, int count)
    {
        return string.Join(" ", SplitLines(text).Take(count));
    }

    private static string ResolveRepositoryRootPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("REPOS_ROOT");
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(UserProfile, "source", "repos")
            : configuredPath;
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);

    private sealed record DockerImage(string Name, string Id, long SizeBytes)
    {
        public bool IsDangling => Name.StartsWith("<none>", StringComparison.Ordinal);
    }

    private sealed record DockerContainer(string Name, string Image, string Status);

    private sealed record DockerFacts(
        bool IsRunning,
        long UsedBytes,
        long BuildCacheReclaimableBytes,
        long ContainersReclaimableBytes,
        int RunningContainerCount,
        IReadOnlySet<string> RunningImages,
        IReadOnlyList<DockerImage> Images,
        IReadOnlyList<DockerContainer> StoppedContainers)
    {
        public static DockerFacts NotRunning { get; } = new(false, 0, 0, 0, 0, new HashSet<string>(), [], []);
    }
}

/// <summary>What an option would free, plus any reason or detail that depends on what was found.</summary>
public sealed record CleanupEstimate(long? SizeBytes, string SizeText, bool IsAvailable = true, string? Reason = null, string? WhatHappens = null);
