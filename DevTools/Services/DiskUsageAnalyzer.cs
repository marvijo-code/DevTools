using System.Diagnostics;
using System.Text.Json;
using DevTools.Infrastructure;
using DevTools.Models;

namespace DevTools.Services;

public sealed class DiskUsageAnalyzer
{
    private const int DefaultTopCount = 10;
    private const int MaxVisibleMeasuringEntries = 3;
    private const int GduWorkerLimit = 2;
    private const int GduCoreLimitPerScan = 2;
    private static readonly Lazy<string?> GduExecutablePath = new(ResolveGduExecutablePath);
    private static readonly Lazy<string?> DustExecutablePath = new(ResolveDustExecutablePath);
    private readonly Dictionary<string, Task<DustNode?>> _dustCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RootBreakdownResult> _rootBreakdownCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<DiskUsageSnapshot> CaptureAsync(IProgress<DiskUsageProgress>? progress, CancellationToken cancellationToken)
    {
        _dustCache.Clear();
        _rootBreakdownCache.Clear();
        var capturedAt = DateTimeOffset.Now;
        var systemDriveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localLowPath = Path.Combine(userProfilePath, "AppData", "LocalLow");
        var devRootPath = ResolveRepositoryRootPath();
        var programFilesPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86Path = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programDataPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var windowsPath = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var tempPath = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var dockerPath = Path.Combine(localAppDataPath, "Docker");
        var drive = new DriveInfo(systemDriveRoot);
        var percentUsed = drive.TotalSize <= 0 ? 0 : (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100d;
        var preferredTool = ResolvePreferredToolName();
        var headline =
            $"{drive.Name} snapshot captured on {capturedAt:yyyy-MM-dd HH:mm:ss}. " +
            $"Used {FormatBytes(drive.TotalSize - drive.AvailableFreeSpace)} of {FormatBytes(drive.TotalSize)} " +
            $"({percentUsed:0.00}%), leaving {FormatBytes(drive.AvailableFreeSpace)} free. Analyzer: {preferredTool}.";

        var sectionBuilders = new List<(string Title, Func<Action<DiskUsageSection, string>, CancellationToken, Task<DiskUsageSection>> Build)>
        {
            ("Docker", (reportSection, token) => BuildDockerSectionAsync(dockerPath, reportSection, token)),
            ("User Profile", (reportSection, token) => BuildLargestChildrenSectionAsync("User Profile", "Largest direct children under the current user profile.", userProfilePath, DefaultTopCount, reportSection, token)),
            ("AppData", (reportSection, token) => BuildAppDataSectionAsync(localAppDataPath, roamingAppDataPath, localLowPath, reportSection, token)),
            ($"{devRootPath} Repositories", (reportSection, token) => BuildLargestChildrenSectionAsync($"{devRootPath} Repositories", $"Largest direct children under {devRootPath}.", devRootPath, DefaultTopCount, reportSection, token)),
            ("Installed Software", (reportSection, token) => BuildInstalledSoftwareSectionAsync(programFilesPath, programDataPath, reportSection, token)),
            ("Temp Hotspots", (reportSection, token) => BuildLargestChildrenSectionAsync("Temp Hotspots", "Largest direct children under the current temp folder.", tempPath, DefaultTopCount, reportSection, token)),
            ("Drive Summary", (reportSection, token) => BuildDriveSummarySectionAsync(
                drive,
                [userProfilePath, programFilesPath, programFilesX86Path, programDataPath, windowsPath, devRootPath],
                reportSection,
                token))
        };

        var sections = sectionBuilders.Select(builder => CreatePendingSection(builder.Title)).ToList();
        progress?.Report(new DiskUsageProgress(headline, $"Starting disk usage refresh with {preferredTool}...", "Scanning disk usage and streaming results as sections complete.", sections.ToList(), false));

        for (var i = 0; i < sectionBuilders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new DiskUsageProgress(headline, $"Scanning section {i + 1} of {sectionBuilders.Count}: {sectionBuilders[i].Title}...", BuildAnalysis(sections.Where(section => !IsPendingSection(section))), sections.ToList(), false));
            var section = await sectionBuilders[i].Build(
                (partialSection, statusText) =>
                {
                    sections[i] = partialSection;
                    progress?.Report(new DiskUsageProgress(headline, statusText, BuildAnalysis(sections.Where(candidate => !IsPendingSection(candidate))), sections.ToList(), false));
                },
                cancellationToken);
            sections[i] = section;
            progress?.Report(new DiskUsageProgress(headline, $"Loaded {sections.Count} of {sectionBuilders.Count} sections.", BuildAnalysis(sections.Where(candidate => !IsPendingSection(candidate))), sections.ToList(), false));
        }

        var analysis = BuildAnalysis(sections);
        progress?.Report(new DiskUsageProgress(headline, $"Last disk refresh: {capturedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}", analysis, sections.ToList(), true));
        return new DiskUsageSnapshot(capturedAt, headline, analysis, sections);
    }

    private async Task<DiskUsageSection> BuildDriveSummarySectionAsync(DriveInfo drive, IEnumerable<string> paths, Action<DiskUsageSection, string> reportSection, CancellationToken cancellationToken)
    {
        var driveEntry = new DiskUsageEntry(drive.Name, $"{FormatBytes(drive.TotalSize - drive.AvailableFreeSpace)} used / {FormatBytes(drive.TotalSize)} total", $"{FormatBytes(drive.AvailableFreeSpace)} free");
        var entries = new List<DiskUsageEntry> { driveEntry };
        var distinctPaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var completed = 0;
        foreach (var currentPath in distinctPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var breakdown = await MeasureRootBreakdownAsync(currentPath, DefaultTopCount, null, cancellationToken);
            entries.Add(breakdown.RootEntry);
            completed++;
            reportSection(
                new DiskUsageSection("Drive Summary", $"Rolled up {completed} of {distinctPaths.Count} root paths from prior subtree scans.", entries.ToList()),
                $"Scanning drive summary: {completed}/{distinctPaths.Count} root paths rolled up. Current: {currentPath}");
        }

        return new DiskUsageSection("Drive Summary", "Root-level measurements assembled from direct-child scans where possible.", entries);
    }

    private async Task<DiskUsageSection> BuildAppDataSectionAsync(string localAppDataPath, string roamingAppDataPath, string localLowPath, Action<DiskUsageSection, string> reportSection, CancellationToken cancellationToken)
    {
        var localBreakdown = await MeasureRootBreakdownAsync(localAppDataPath, 10, (partialEntries, completed, total, currentPath) =>
        {
            reportSection(
                new DiskUsageSection("AppData", $"Measured {completed} of {total} Local children.", partialEntries),
                $"Scanning AppData Local: {completed}/{total} children measured. Current: {currentPath}");
        }, cancellationToken);
        var localEntries = SortEntriesForDisplay(localBreakdown.ChildEntries, 10);

        var roamingBreakdown = await MeasureRootBreakdownAsync(roamingAppDataPath, 8, (partialEntries, completed, total, currentPath) =>
        {
            reportSection(
                new DiskUsageSection("AppData", $"Measured {completed} of {total} Roaming children.", BuildAppDataEntries(localBreakdown.RootEntry, null, null, localEntries, partialEntries)),
                $"Scanning AppData Roaming: {completed}/{total} children measured. Current: {currentPath}");
        }, cancellationToken);
        var roamingEntries = SortEntriesForDisplay(roamingBreakdown.ChildEntries, 8);

        var localLowMeasurement = await MeasurePathAsync(localLowPath, cancellationToken);
        var localLowEntry = CreateStaticMeasuredEntry(localLowPath, localLowMeasurement);
        var entries = BuildAppDataEntries(localBreakdown.RootEntry, roamingBreakdown.RootEntry, localLowEntry, localEntries, roamingEntries);
        return new DiskUsageSection("AppData", "Current AppData footprint, including the largest Local and Roaming subfolders.", entries);
    }

    private static List<DiskUsageEntry> BuildAppDataEntries(
        DiskUsageEntry? localRootEntry,
        DiskUsageEntry? roamingRootEntry,
        DiskUsageEntry? localLowEntry,
        IEnumerable<DiskUsageEntry> localEntries,
        IEnumerable<DiskUsageEntry> roamingEntries)
    {
        var entries = new List<DiskUsageEntry>();
        if (localRootEntry is not null)
        {
            entries.Add(localRootEntry);
        }

        if (roamingRootEntry is not null)
        {
            entries.Add(roamingRootEntry);
        }

        if (localLowEntry is not null)
        {
            entries.Add(localLowEntry);
        }

        entries.AddRange(localEntries);
        entries.AddRange(roamingEntries);
        return entries;
    }

    private async Task<DiskUsageSection> BuildInstalledSoftwareSectionAsync(string programFilesPath, string programDataPath, Action<DiskUsageSection, string> reportSection, CancellationToken cancellationToken)
    {
        var entries = new List<DiskUsageEntry>();
        var programFilesBreakdown = await MeasureRootBreakdownAsync(programFilesPath, 10, (partialEntries, completed, total, currentPath) =>
        {
            reportSection(new DiskUsageSection("Installed Software", $"Measured {completed} of {total} Program Files children.", partialEntries), $"Scanning Program Files: {completed}/{total} children measured. Current: {currentPath}");
        }, cancellationToken);
        var programFilesEntries = SortEntriesForDisplay(programFilesBreakdown.ChildEntries, 10);
        entries.AddRange(programFilesEntries);

        var programDataBreakdown = await MeasureRootBreakdownAsync(programDataPath, 10, (partialEntries, completed, total, currentPath) =>
        {
            reportSection(new DiskUsageSection("Installed Software", $"Measured {completed} of {total} ProgramData children.", entries.Concat(partialEntries).ToList()), $"Scanning ProgramData: {completed}/{total} children measured. Current: {currentPath}");
        }, cancellationToken);
        var programDataEntries = SortEntriesForDisplay(programDataBreakdown.ChildEntries, 10);
        entries.AddRange(programDataEntries);
        return new DiskUsageSection("Installed Software", "Largest direct children under Program Files and ProgramData.", entries);
    }

    private async Task<DiskUsageSection> BuildDockerSectionAsync(string dockerPath, Action<DiskUsageSection, string> reportSection, CancellationToken cancellationToken)
    {
        var candidatePaths = new[]
        {
            dockerPath,
            Path.Combine(dockerPath, "wsl"),
            Path.Combine(dockerPath, "wsl", "disk", "docker_data.vhdx"),
            Path.Combine(dockerPath, "wsl", "main", "ext4.vhdx"),
            Path.Combine(dockerPath, "log")
        };
        var measuredEntries = await MeasureEntriesAsync(candidatePaths, candidatePaths.Length, (partialEntries, completed, total, currentPath) =>
        {
            reportSection(new DiskUsageSection("Docker", $"Measured {completed} of {total} Docker storage paths.", partialEntries), $"Scanning Docker storage: {completed}/{total} paths measured. Current: {currentPath}");
        }, cancellationToken);
        var existingEntries = measuredEntries.Where(entry => entry.ExistsOnDisk).ToList();
        if (existingEntries.Count == 0)
        {
            existingEntries.Add(new DiskUsageEntry(dockerPath, "Not found", "Docker Desktop storage was not found under Local AppData."));
        }

        return new DiskUsageSection("Docker", "Current Docker Desktop storage paths under Local AppData.", existingEntries);
    }

    private async Task<DiskUsageSection> BuildLargestChildrenSectionAsync(string title, string summary, string rootPath, int topCount, Action<DiskUsageSection, string> reportSection, CancellationToken cancellationToken)
    {
        var breakdown = await MeasureRootBreakdownAsync(rootPath, topCount, (partialEntries, completed, total, currentPath) =>
        {
            reportSection(new DiskUsageSection(title, $"Measured {completed} of {total} direct children.", partialEntries), $"Scanning {title}: {completed}/{total} children measured. Current: {currentPath}");
        }, cancellationToken);
        var entries = SortEntriesForDisplay(breakdown.ChildEntries, topCount);

        if (entries.Count == 0)
        {
            entries = [breakdown.RootEntry];
        }

        return new DiskUsageSection(title, summary, entries);
    }

    private async Task<RootBreakdownResult> MeasureRootBreakdownAsync(string rootPath, int visibleCount, Action<List<DiskUsageEntry>, int, int, string>? reportPartialEntries, CancellationToken cancellationToken)
    {
        if (_rootBreakdownCache.TryGetValue(rootPath, out var cachedBreakdown))
        {
            return cachedBreakdown;
        }

        RootBreakdownResult breakdown;
        if (!Directory.Exists(rootPath))
        {
            breakdown = new RootBreakdownResult(
                DiskUsageEntry.CreateCompleted(rootPath, false, null, "Not found", "The path does not exist on this machine."),
                []);
        }
        else
        {
            List<string> childPaths;
            try
            {
                childPaths = EnumerateChildPaths(rootPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                breakdown = new RootBreakdownResult(
                    DiskUsageEntry.CreateCompleted(rootPath, true, null, "Unavailable", ex.Message),
                    []);
                _rootBreakdownCache[rootPath] = breakdown;
                return breakdown;
            }

            if (childPaths.Count == 0)
            {
                var measurement = await MeasurePathAsync(rootPath, cancellationToken);
                var rootEntry = CreateStaticMeasuredEntry(rootPath, measurement);
                breakdown = new RootBreakdownResult(rootEntry, [rootEntry]);
            }
            else
            {
                var childEntries = await MeasureEntriesAsync(childPaths, visibleCount, reportPartialEntries, cancellationToken);
                breakdown = new RootBreakdownResult(CreateAggregateRootEntry(rootPath, childEntries), childEntries);
            }
        }

        _rootBreakdownCache[rootPath] = breakdown;
        return breakdown;
    }

    private async Task<List<DiskUsageEntry>> MeasureLargestChildrenAsync(string rootPath, int topCount, Action<List<DiskUsageEntry>, int, int, string>? reportPartialEntries, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(rootPath))
        {
            return [new DiskUsageEntry(rootPath, "Not found", "The path does not exist on this machine.")];
        }

        List<string> childPaths;
        try
        {
            childPaths = EnumerateChildPaths(rootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [new DiskUsageEntry(rootPath, "Unavailable", ex.Message)];
        }

        if (childPaths.Count == 0)
        {
            var measurement = await MeasurePathAsync(rootPath, cancellationToken);
            return [CreateMeasuredEntry(rootPath, measurement, TimeSpan.Zero)];
        }

        var entries = await MeasureEntriesAsync(childPaths, topCount, reportPartialEntries, cancellationToken);
        return SortEntriesForDisplay(entries, topCount);
    }

    private async Task<List<DiskUsageEntry>> MeasureEntriesAsync(IReadOnlyList<string> paths, int visibleCount, Action<List<DiskUsageEntry>, int, int, string>? reportPartialEntries, CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
        {
            return [];
        }

        var entries = paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => DiskUsageEntry.CreateQueued(path)).ToList();
        var totalCount = entries.Count;
        var completedCount = 0;
        reportPartialEntries?.Invoke(BuildVisibleEntries(entries, visibleCount), completedCount, totalCount, entries[0].Path);

        using var gate = new SemaphoreSlim(ResolveWorkerLimit());
        var tasks = entries.Select(async entry =>
        {
            await gate.WaitAsync(cancellationToken);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                entry.StartMeasurement("Calculating size.");
                reportPartialEntries?.Invoke(BuildVisibleEntries(entries, visibleCount), Volatile.Read(ref completedCount), totalCount, entry.Path);
                var measurement = await MeasurePathAsync(entry.Path, cancellationToken);
                stopwatch.Stop();
                entry.CompleteMeasurement(measurement.Exists, measurement.SizeBytes, measurement.DisplayValue, measurement.Note, stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                AppLog.Error($"Disk measurement failed for {entry.Path}.", ex);
                entry.CompleteMeasurement(true, null, "Unavailable", ex.Message, stopwatch.Elapsed);
            }
            finally
            {
                var completed = Interlocked.Increment(ref completedCount);
                reportPartialEntries?.Invoke(BuildVisibleEntries(entries, visibleCount), completed, totalCount, entry.Path);
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        return entries;
    }

    private async Task<PathMeasurement> MeasurePathAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new PathMeasurement(false, null, "Unavailable", "No path was provided.");
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return new PathMeasurement(false, null, "Not found", "The path does not exist on this machine.");
        }

        if (Directory.Exists(path))
        {
            var gduMeasurement = await TryMeasurePathWithGduAsync(path, cancellationToken);
            if (gduMeasurement is not null)
            {
                return gduMeasurement.Value;
            }

            var dustMeasurement = await TryMeasurePathWithDustAsync(path, cancellationToken);
            if (dustMeasurement is not null)
            {
                return dustMeasurement.Value;
            }
        }

        try
        {
            var sizeBytes = await Task.Run(() => MeasurePathSize(path, cancellationToken), cancellationToken);
            var note = File.Exists(path)
                ? "Measured directly from file metadata."
                : "Measured with the managed fallback scanner.";
            return new PathMeasurement(true, sizeBytes, FormatBytes(sizeBytes), note);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            return new PathMeasurement(true, null, "Unavailable", ex.Message);
        }
    }

    private async Task<PathMeasurement?> TryMeasurePathWithGduAsync(string path, CancellationToken cancellationToken)
    {
        var gduExecutablePath = GduExecutablePath.Value;
        if (string.IsNullOrWhiteSpace(gduExecutablePath))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = gduExecutablePath,
            Arguments = $"-n -c -p -s --no-prefix --max-cores {GduCoreLimitPerScan} \"{path}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            AppLog.Info($"gdu scan skipped for {path}. ExitCode={process.ExitCode}. Error={error}");
            return null;
        }

        var firstLine = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return null;
        }

        var parts = firstLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !long.TryParse(parts[0], out var sizeBytes))
        {
            return null;
        }

        return new PathMeasurement(true, sizeBytes, FormatBytes(sizeBytes), "Measured with gdu.");
    }

    private async Task<PathMeasurement?> TryMeasurePathWithDustAsync(string path, CancellationToken cancellationToken)
    {
        var dustRoot = await RunDustAsync(path, 1, cancellationToken);
        if (dustRoot is null || string.IsNullOrWhiteSpace(dustRoot.Size))
        {
            return null;
        }

        var sizeBytes = ParseDustSizeToBytes(dustRoot.Size);
        return sizeBytes is null
            ? null
            : new PathMeasurement(true, sizeBytes.Value, FormatBytes(sizeBytes.Value), "Measured with dust.");
    }

    private static long MeasurePathSize(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new FileInfo(path).Length;
        }

        var totalBytes = 0L;
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(path);

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();

            IEnumerable<string> filePaths = [];
            try
            {
                filePaths = Directory.EnumerateFiles(currentDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }

            foreach (var filePath in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    totalBytes += new FileInfo(filePath).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or PathTooLongException)
                {
                }
            }

            IEnumerable<string> subdirectories = [];
            try
            {
                subdirectories = Directory.EnumerateDirectories(currentDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
            }

            foreach (var subdirectory in subdirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(subdirectory) & System.IO.FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    pendingDirectories.Push(subdirectory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
                {
                }
            }
        }

        return totalBytes;
    }

    private static string BuildAnalysis(IEnumerable<DiskUsageSection> sections)
    {
        var heaviestEntries = sections
            .SelectMany(section => section.Entries)
            .Select(entry => new { Entry = entry, SizeBytes = entry.SizeBytes ?? TryParseSize(entry.Usage) })
            .Where(item => item.SizeBytes.HasValue)
            .OrderByDescending(item => item.SizeBytes)
            .Take(3)
            .ToList();

        if (heaviestEntries.Count == 0)
        {
            return "Refresh in progress. Completed sections will update here as measurements finish.";
        }

        return "Current largest measured paths: " +
               string.Join(", ", heaviestEntries.Select(item => $"{item.Entry.DisplayName} ({item.Entry.Usage})")) +
               ".";
    }

    public static IReadOnlyList<DiskUsageEntry> TryParseEverythingCsvRows(string csv)
    {
        var entries = new List<DiskUsageEntry>();
        if (string.IsNullOrWhiteSpace(csv))
        {
            return entries;
        }

        foreach (var line in csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = ParseCsvLine(line);
            if (columns.Count < 2 || string.Equals(columns[0], "Filename", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!long.TryParse(columns[1], out var sizeBytes))
            {
                continue;
            }

            entries.Add(DiskUsageEntry.CreateCompleted(columns[0], true, sizeBytes, FormatBytes(sizeBytes), "Measured by Everything."));
        }

        return entries;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var columns = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var currentChar = line[i];
            if (currentChar == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (currentChar == ',' && !inQuotes)
            {
                columns.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(currentChar);
        }

        columns.Add(current.ToString());
        return columns;
    }

    private static long? TryParseSize(string usage)
    {
        if (string.IsNullOrWhiteSpace(usage))
        {
            return null;
        }

        var parts = usage.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 || !double.TryParse(parts[0], out var value))
        {
            return null;
        }

        return parts[1].ToUpperInvariant() switch
        {
            "B" => (long)value,
            "KB" => (long)(value * 1024),
            "MB" => (long)(value * 1024 * 1024),
            "GB" => (long)(value * 1024 * 1024 * 1024),
            "TB" => (long)(value * 1024 * 1024 * 1024 * 1024),
            _ => null
        };
    }

    private static string FormatBytes(long bytes)
    {
        const double scale = 1024d;
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var suffixIndex = 0;
        double displayValue = bytes;
        while (displayValue >= scale && suffixIndex < suffixes.Length - 1)
        {
            displayValue /= scale;
            suffixIndex++;
        }

        return suffixIndex == 0 ? $"{displayValue:0} {suffixes[suffixIndex]}" : $"{displayValue:0.00} {suffixes[suffixIndex]}";
    }

    private sealed class DustNode
    {
        public string? Size { get; set; }

        public string? Name { get; set; }

        public List<DustNode> Children { get; set; } = [];
    }

    private static DiskUsageSection CreatePendingSection(string title)
    {
        return new DiskUsageSection(title, "Scanning this section now.", [new DiskUsageEntry("Working...", "In progress", "Results appear here as soon as this section finishes.")]);
    }

    private static bool IsPendingSection(DiskUsageSection section)
    {
        return section.Entries.Count == 1 &&
               string.Equals(section.Entries[0].Path, "Working...", StringComparison.Ordinal) &&
               string.Equals(section.Entries[0].Usage, "In progress", StringComparison.Ordinal);
    }

    private async Task<DustNode?> RunDustAsync(string path, int depth, CancellationToken cancellationToken)
    {
        var cacheKey = $"{depth}|{Path.GetFullPath(path)}";
        if (_dustCache.TryGetValue(cacheKey, out var cachedTask))
        {
            return await cachedTask;
        }

        var task = RunDustCoreAsync(path, depth, cancellationToken);
        _dustCache[cacheKey] = task;
        return await task;
    }

    private static async Task<DustNode?> RunDustCoreAsync(string path, int depth, CancellationToken cancellationToken)
    {
        var dustExecutablePath = DustExecutablePath.Value;
        if (string.IsNullOrWhiteSpace(dustExecutablePath))
        {
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = dustExecutablePath,
            Arguments = $"-D -p -d {depth} -n 200 -j \"{path}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            AppLog.Info($"dust scan skipped for {path}. ExitCode={process.ExitCode}. Error={error}");
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DustNode>(output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ResolveGduExecutablePath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "gdu.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, @"..\..\..\..\.tools\gdu\gdu_windows_amd64.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, @"..\..\..\..\.tools\gdu\gdu.exe")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", "gdu.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "gdu", "gdu.exe")
        };

        foreach (var candidate in candidates.Where(File.Exists))
        {
            return candidate;
        }

        return ResolveExecutableFromWhere("gdu");
    }

    private static string? ResolveDustExecutablePath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "dust.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", "dust.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dust", "dust.exe")
        };

        foreach (var candidate in candidates.Where(File.Exists))
        {
            return candidate;
        }

        return ResolveExecutableFromWhere("dust");
    }

    private static string? ResolveExecutableFromWhere(string executableName)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = executableName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            process.WaitForExit(2000);
            return process.StandardOutput.ReadToEnd()
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(File.Exists);
        }
        catch
        {
            return null;
        }
    }

    private static long? ParseDustSizeToBytes(string rawSize)
    {
        if (string.IsNullOrWhiteSpace(rawSize))
        {
            return null;
        }

        var normalized = rawSize.Trim();
        var splitIndex = normalized.TakeWhile(ch => char.IsDigit(ch) || ch is '.' or ',').Count();
        if (splitIndex == 0)
        {
            return null;
        }

        var numberPart = normalized[..splitIndex].Replace(',', '.');
        var unitPart = normalized[splitIndex..].Trim().ToUpperInvariant();
        if (!double.TryParse(numberPart, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return unitPart switch
        {
            "B" => (long)value,
            "K" or "KB" or "KIB" => (long)(value * 1024),
            "M" or "MB" or "MIB" => (long)(value * 1024 * 1024),
            "G" or "GB" or "GIB" => (long)(value * 1024 * 1024 * 1024),
            "T" or "TB" or "TIB" => (long)(value * 1024 * 1024 * 1024 * 1024),
            _ => null
        };
    }

    private static int ResolveWorkerLimit()
    {
        return !string.IsNullOrWhiteSpace(GduExecutablePath.Value)
            ? GduWorkerLimit
            : Math.Clamp(Environment.ProcessorCount / 2, 1, 3);
    }

    private static string ResolvePreferredToolName()
    {
        if (!string.IsNullOrWhiteSpace(GduExecutablePath.Value))
        {
            return "gdu";
        }

        if (!string.IsNullOrWhiteSpace(DustExecutablePath.Value))
        {
            return "dust";
        }

        return "managed fallback";
    }

    private static string ResolveRepositoryRootPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("REPOS_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
    }

    private static List<string> EnumerateChildPaths(string rootPath)
    {
        var childPaths = new List<string>();
        childPaths.AddRange(Directory.EnumerateDirectories(rootPath));
        childPaths.AddRange(Directory.EnumerateFiles(rootPath));
        return childPaths.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<DiskUsageEntry> BuildVisibleEntries(IReadOnlyList<DiskUsageEntry> entries, int maxCount)
    {
        if (entries.Count <= maxCount)
        {
            return entries.ToList();
        }

        var visibleEntries = new List<DiskUsageEntry>(maxCount);
        var reserveForMeasuring = entries.Any(entry => entry.IsMeasuring) ? Math.Min(MaxVisibleMeasuringEntries, maxCount) : 0;
        var completedLimit = Math.Max(0, maxCount - reserveForMeasuring);
        visibleEntries.AddRange(entries.Where(entry => !entry.IsMeasuring && entry.SizeBytes.HasValue).OrderByDescending(entry => entry.SizeBytes).ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).Take(completedLimit));
        visibleEntries.AddRange(entries.Where(entry => entry.IsMeasuring).OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).Take(maxCount - visibleEntries.Count));
        if (visibleEntries.Count < maxCount)
        {
            visibleEntries.AddRange(entries.Where(entry => !visibleEntries.Contains(entry)).OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).Take(maxCount - visibleEntries.Count));
        }

        return visibleEntries;
    }

    private static List<DiskUsageEntry> SortEntriesForDisplay(IEnumerable<DiskUsageEntry> entries, int topCount)
    {
        return entries.OrderByDescending(entry => entry.SizeBytes ?? -1).ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase).Take(topCount).ToList();
    }

    private static DiskUsageEntry CreateAggregateRootEntry(string rootPath, IReadOnlyList<DiskUsageEntry> childEntries)
    {
        var measuredEntries = childEntries.Where(entry => entry.SizeBytes.HasValue).ToList();
        if (measuredEntries.Count == 0)
        {
            return DiskUsageEntry.CreateCompleted(rootPath, true, null, "Unavailable", "None of the direct children could be measured.");
        }

        var totalSizeBytes = measuredEntries.Sum(entry => entry.SizeBytes!.Value);
        var unavailableCount = childEntries.Count - measuredEntries.Count;
        var note = unavailableCount == 0
            ? $"Calculated from {measuredEntries.Count} direct children."
            : $"Calculated from {measuredEntries.Count} of {childEntries.Count} direct children; {unavailableCount} entries were unavailable.";
        var rootEntry = DiskUsageEntry.CreateCompleted(rootPath, true, totalSizeBytes, FormatBytes(totalSizeBytes), note);
        rootEntry.SetChildren(childEntries);
        return rootEntry;
    }

    private static DiskUsageEntry CreateStaticMeasuredEntry(string path, PathMeasurement measurement)
    {
        return DiskUsageEntry.CreateCompleted(path, measurement.Exists, measurement.SizeBytes, measurement.DisplayValue, measurement.Note);
    }

    private static DiskUsageEntry CreateMeasuredEntry(string path, PathMeasurement measurement, TimeSpan elapsed)
    {
        var entry = new DiskUsageEntry(path, measurement.DisplayValue);
        entry.CompleteMeasurement(measurement.Exists, measurement.SizeBytes, measurement.DisplayValue, measurement.Note, elapsed);
        return entry;
    }

    private sealed record RootBreakdownResult(DiskUsageEntry RootEntry, IReadOnlyList<DiskUsageEntry> ChildEntries);

    private readonly record struct PathMeasurement(bool Exists, long? SizeBytes, string DisplayValue, string Note);
}

public sealed record DiskUsageSnapshot(DateTimeOffset CapturedAt, string Headline, string Analysis, IReadOnlyList<DiskUsageSection> Sections);

public sealed record DiskUsageProgress(string Headline, string StatusText, string Analysis, IReadOnlyList<DiskUsageSection> Sections, bool IsCompleted);
