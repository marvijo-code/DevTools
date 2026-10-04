using DevTools.Infrastructure;
using DevTools.Models;
using DevTools.Services.FolderSizes;

namespace DevTools.Services;

/// <summary>
/// Disk usage for every fixed drive. Each drive is sized ONCE by <see cref="DriveUsageService"/>
/// (Everything, the NTFS MFT or a folder walk), and every section, expansion and folder listing
/// is then read from that in-memory tree, so nothing is measured twice and no section waits on
/// a separate slow scan. All heavy work runs off the UI thread.
/// </summary>
public sealed class DiskUsageAnalyzer
{
    /// <summary>Title prefix of the per-drive largest-folders sections.</summary>
    public const string LargestFoldersSectionTitle = "Largest Folders";

    private const int DefaultTopCount = 10;
    private const int LargestFolderCount = 25;
    private const int LargestFolderChildren = 15;

    private readonly DriveUsageService _driveUsage = new();
    private readonly object _treesLock = new();
    private readonly Dictionary<string, FolderTree> _trees = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder trees from the last scan, system drive first.</summary>
    public IReadOnlyList<FolderTree> Trees
    {
        get
        {
            lock (_treesLock)
            {
                return _trees.Values.ToList();
            }
        }
    }

    public static string LargestFoldersTitleFor(string driveRoot) => $"{LargestFoldersSectionTitle} - {driveRoot}";

    public Task<DiskUsageSnapshot> CaptureAsync(IProgress<DiskUsageProgress>? progress, CancellationToken cancellationToken)
    {
        // Task.Run: drive enumeration, process starts and tree building must never touch the UI
        // thread. The 2026-10 "hangs on load" bug was the MFT read running on the dispatcher.
        return Task.Run(() => CaptureCoreAsync(progress, cancellationToken), cancellationToken);
    }

    private async Task<DiskUsageSnapshot> CaptureCoreAsync(IProgress<DiskUsageProgress>? progress, CancellationToken cancellationToken)
    {
        var capturedAt = DateTimeOffset.Now;
        var drives = DriveUsageService.GetFixedDrives();
        var headline = BuildHeadline(drives, capturedAt);
        var systemRoot = FolderTree.NormalizeRoot(Environment.SystemDirectory);

        var sections = new List<DiskUsageSection>();
        foreach (var drive in drives)
        {
            sections.Add(CreatePendingSection(LargestFoldersTitleFor(drive.RootPath), $"Waiting to size every folder on {drive.RootPath}."));
        }

        var derivedTitles = new[] { "User Profile", "AppData", "Repositories", "Installed Software", "Temp Hotspots", "Docker" };
        foreach (var title in derivedTitles)
        {
            sections.Add(CreatePendingSection(title, $"Filled in from the {systemRoot} scan."));
        }

        sections.Add(BuildDriveSummarySection(drives, new Dictionary<string, DriveScanResult>(StringComparer.OrdinalIgnoreCase)));
        progress?.Report(new DiskUsageProgress(headline, $"Found {drives.Count} fixed drive(s). Sizing every folder...", "Sizing folders. Results appear per drive as soon as each drive is done.", sections.ToList(), false));

        var results = new Dictionary<string, DriveScanResult>(StringComparer.OrdinalIgnoreCase);
        var currentAnalysis = BuildAnalysis([]);
        for (var driveIndex = 0; driveIndex < drives.Count; driveIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var drive = drives[driveIndex];
            var sectionIndex = driveIndex;
            var title = LargestFoldersTitleFor(drive.RootPath);
            var lastStatus = DateTime.MinValue;
            var driveProgress = new InlineProgress<FolderScanProgress>(update =>
            {
                // Throttle: the UI only needs a few updates a second.
                if ((DateTime.UtcNow - lastStatus).TotalMilliseconds < 400)
                {
                    return;
                }

                lastStatus = DateTime.UtcNow;
                var elapsed = update.Elapsed > TimeSpan.Zero ? $" ({update.Elapsed.TotalSeconds:F0}s)" : string.Empty;
                sections[sectionIndex] = CreatePendingSection(title, $"{update.EngineName}: {update.Message}{elapsed}");
                progress?.Report(new DiskUsageProgress(headline, $"Drive {sectionIndex + 1} of {drives.Count} ({drive.RootPath}): {update.Message}", currentAnalysis, sections.ToList(), false));
            });

            DriveScanResult result;
            try
            {
                result = await _driveUsage.ScanDriveAsync(drive, driveProgress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Sizing {drive.RootPath} failed.", ex);
                result = new DriveScanResult(drive, null, "None", ex.Message);
            }

            results[drive.RootPath] = result;
            if (result.Tree is not null)
            {
                lock (_treesLock)
                {
                    _trees[drive.RootPath] = result.Tree;
                }
            }

            sections[sectionIndex] = BuildDriveSection(result);
            if (string.Equals(drive.RootPath, systemRoot, StringComparison.OrdinalIgnoreCase))
            {
                var derived = BuildDerivedSections(result.Tree);
                for (var i = 0; i < derived.Count; i++)
                {
                    sections[drives.Count + i] = derived[i];
                }
            }

            sections[^1] = BuildDriveSummarySection(drives, results);
            currentAnalysis = BuildAnalysis(results.Values);
            progress?.Report(new DiskUsageProgress(headline, $"Sized {results.Count} of {drives.Count} drive(s).", currentAnalysis, sections.ToList(), false));
        }

        if (!results.ContainsKey(systemRoot))
        {
            // No system drive among the fixed drives (unusual): drop the derived placeholders.
            sections.RemoveAll(section => derivedTitles.Contains(section.Title));
        }

        var analysis = BuildAnalysis(results.Values);
        progress?.Report(new DiskUsageProgress(headline, $"Last disk refresh: {capturedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}", analysis, sections.ToList(), true));
        return new DiskUsageSnapshot(capturedAt, headline, analysis, sections);
    }

    /// <summary>
    /// Children of a folder for the "+" button: read from the drive tree when the folder is in
    /// it (instant), otherwise measured with a low-priority walk of just that folder.
    /// </summary>
    public async Task<IReadOnlyList<DiskUsageEntry>> ExpandEntryAsync(DiskUsageEntry entry, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(entry.Path))
        {
            return [];
        }

        if (TryFindNode(entry.Path, out var tree, out var node))
        {
            return BuildChildEntries(tree!, node, LargestFolderChildren, includeGrandchildren: false);
        }

        var walked = await _driveUsage.WalkFolderAsync(entry.Path, cancellationToken).ConfigureAwait(false);
        var walkedNode = walked.Find(entry.Path);
        return walkedNode < 0 ? [] : BuildChildEntries(walked, walkedNode, LargestFolderChildren, includeGrandchildren: false);
    }

    /// <summary>Finds the tree and node for a folder path from the last scan.</summary>
    public bool TryFindNode(string path, out FolderTree? tree, out int node)
    {
        tree = null;
        node = -1;
        if (string.IsNullOrWhiteSpace(path) || path.Length < 2 || path[1] != ':')
        {
            return false;
        }

        var root = char.ToUpperInvariant(path[0]) + @":\";
        lock (_treesLock)
        {
            if (!_trees.TryGetValue(root, out var found))
            {
                return false;
            }

            tree = found;
        }

        node = tree.Find(path);
        return node >= 0;
    }

    private static string BuildHeadline(IReadOnlyList<DriveDescriptor> drives, DateTimeOffset capturedAt)
    {
        if (drives.Count == 0)
        {
            return $"No fixed drives were found ({capturedAt:yyyy-MM-dd HH:mm:ss}).";
        }

        var parts = drives.Select(drive =>
        {
            var percent = drive.TotalBytes <= 0 ? 0 : (double)drive.UsedBytes / drive.TotalBytes * 100d;
            return $"{drive.RootPath} {FormatBytes(drive.UsedBytes)} used of {FormatBytes(drive.TotalBytes)} ({percent:0.0}%), {FormatBytes(drive.FreeBytes)} free";
        });
        return $"Snapshot {capturedAt:yyyy-MM-dd HH:mm:ss}. " + string.Join(". ", parts) + ".";
    }

    private static DiskUsageSection BuildDriveSection(DriveScanResult result)
    {
        var title = LargestFoldersTitleFor(result.Drive.RootPath);
        if (result.Tree is null)
        {
            return new DiskUsageSection(title, $"Could not size the folders on {result.Drive.RootPath}. {result.Note}",
                [DiskUsageEntry.CreateCompleted(result.Drive.RootPath, true, null, "Unavailable", result.Note)]);
        }

        var tree = result.Tree;
        var entries = BuildChildEntries(tree, 0, LargestFolderCount, includeGrandchildren: true);
        var rootFiles = tree.GetDirectFileBytes(0);
        if (rootFiles > 0)
        {
            entries.Add(DiskUsageEntry.CreateCompleted(tree.RootPath, true, rootFiles, FormatBytes(rootFiles), "Files directly in the drive root.", $"Files in {tree.RootPath}"));
        }

        var hidden = result.Drive.UsedBytes - tree.TotalBytes;
        var hiddenText = hidden > 1024L * 1024 * 1024
            ? $" {FormatBytes(hidden)} of used space is not in any readable folder (system files such as pagefile/hiberfil, restore points, or folders without access)."
            : string.Empty;
        var summary =
            $"{tree.EngineName} sized {tree.FolderCount:N0} folders and {tree.TotalFiles:N0} files ({FormatBytes(tree.TotalBytes)}) on {tree.RootPath} in {tree.Elapsed.TotalSeconds:F1}s. " +
            $"Use + to see subfolders, or the folder browser to go deeper.{hiddenText}";
        return new DiskUsageSection(title, summary, entries);
    }

    private List<DiskUsageSection> BuildDerivedSections(FolderTree? tree)
    {
        var userProfilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localLowPath = Path.Combine(userProfilePath, "AppData", "LocalLow");
        var devRootPath = ResolveRepositoryRootPath();
        var programFilesPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86Path = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programDataPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var tempPath = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        var dockerPath = Path.Combine(localAppDataPath, "Docker");

        DiskUsageSection Children(string title, string summary, string path, int top)
        {
            var entries = ChildEntriesFor(tree, path, top);
            return new DiskUsageSection(title, summary, entries);
        }

        var appData = new List<DiskUsageEntry>();
        appData.AddRange(new[] { localAppDataPath, roamingAppDataPath, localLowPath }.Select(path => FolderEntryFor(tree, path)));
        appData.AddRange(ChildEntriesFor(tree, localAppDataPath, 10));
        appData.AddRange(ChildEntriesFor(tree, roamingAppDataPath, 8));

        var installed = new List<DiskUsageEntry>();
        installed.AddRange(ChildEntriesFor(tree, programFilesPath, 10));
        installed.AddRange(ChildEntriesFor(tree, programFilesX86Path, 5));
        installed.AddRange(ChildEntriesFor(tree, programDataPath, 10));

        var docker = new List<DiskUsageEntry> { FolderEntryFor(tree, dockerPath) };
        foreach (var file in new[] { Path.Combine(dockerPath, "wsl", "disk", "docker_data.vhdx"), Path.Combine(dockerPath, "wsl", "main", "ext4.vhdx") })
        {
            try
            {
                if (File.Exists(file))
                {
                    var length = new FileInfo(file).Length;
                    docker.Add(DiskUsageEntry.CreateCompleted(file, true, length, FormatBytes(length), "Docker virtual disk."));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return
        [
            Children("User Profile", "Largest folders under the current user profile.", userProfilePath, DefaultTopCount),
            new DiskUsageSection("AppData", "AppData Local, Roaming and LocalLow, plus their largest subfolders.", appData),
            Children("Repositories", $"Largest folders under {devRootPath}.", devRootPath, DefaultTopCount),
            new DiskUsageSection("Installed Software", "Largest folders under Program Files and ProgramData.", installed),
            Children("Temp Hotspots", "Largest folders under the current temp folder.", tempPath, DefaultTopCount),
            new DiskUsageSection("Docker", "Docker Desktop storage under Local AppData.", docker)
        ];
    }

    private static DiskUsageSection BuildDriveSummarySection(IReadOnlyList<DriveDescriptor> drives, IReadOnlyDictionary<string, DriveScanResult> results)
    {
        var entries = new List<DiskUsageEntry>();
        foreach (var drive in drives)
        {
            var label = string.IsNullOrWhiteSpace(drive.Label) ? drive.FileSystem : $"{drive.Label}, {drive.FileSystem}";
            var note = results.TryGetValue(drive.RootPath, out var result) && result.Tree is not null
                ? $"{FormatBytes(drive.FreeBytes)} free of {FormatBytes(drive.TotalBytes)}. Folders hold {FormatBytes(result.Tree.TotalBytes)} (sized by {result.EngineName} in {result.Tree.Elapsed.TotalSeconds:F1}s)."
                : $"{FormatBytes(drive.FreeBytes)} free of {FormatBytes(drive.TotalBytes)}. {(result is null ? "Folder scan pending." : result.Note)}";
            entries.Add(DiskUsageEntry.CreateCompleted(drive.RootPath, true, drive.UsedBytes, $"{FormatBytes(drive.UsedBytes)} used", note, $"{drive.RootPath} ({label})"));
        }

        return new DiskUsageSection("Drive Summary", "Used and free space for every fixed drive.", entries);
    }

    private static List<DiskUsageEntry> ChildEntriesFor(FolderTree? tree, string path, int top)
    {
        if (tree is null || string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var node = tree.Find(path);
        if (node < 0)
        {
            return [DiskUsageEntry.CreateCompleted(path, Directory.Exists(path), null, "Not found", "This folder was not found in the drive scan.")];
        }

        var entries = BuildChildEntries(tree, node, top, includeGrandchildren: true);
        return entries.Count == 0 ? [CreateFolderEntry(tree, node, includeGrandchildren: false)] : entries;
    }

    private static DiskUsageEntry FolderEntryFor(FolderTree? tree, string path)
    {
        var node = tree?.Find(path) ?? -1;
        return node < 0
            ? DiskUsageEntry.CreateCompleted(path, Directory.Exists(path), null, "Not found", "This folder was not found in the drive scan.")
            : CreateFolderEntry(tree!, node, includeGrandchildren: true);
    }

    private static List<DiskUsageEntry> BuildChildEntries(FolderTree tree, int node, int top, bool includeGrandchildren)
    {
        return tree.GetChildren(node, top)
            .Where(child => tree.GetTotalBytes(child) > 0)
            .Select(child => CreateFolderEntry(tree, child, includeGrandchildren))
            .ToList();
    }

    private static DiskUsageEntry CreateFolderEntry(FolderTree tree, int node, bool includeGrandchildren)
    {
        var path = tree.GetPath(node);
        var size = tree.GetTotalBytes(node);
        var entry = DiskUsageEntry.CreateCompleted(
            path,
            true,
            size,
            FormatBytes(size),
            $"{tree.GetFileCount(node):N0} files",
            ResolveFolderDisplayName(path));
        if (includeGrandchildren && tree.HasChildFolders(node))
        {
            entry.SetChildren(BuildChildEntries(tree, node, LargestFolderChildren, includeGrandchildren: false));
        }

        entry.SetExpandable(tree.HasChildFolders(node));
        return entry;
    }

    private static string? ResolveFolderDisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string BuildAnalysis(IEnumerable<DriveScanResult> results)
    {
        var top = results
            .Where(result => result.Tree is not null)
            .SelectMany(result => result.Tree!.GetChildren(0, 3).Select(node => (Tree: result.Tree!, Node: node)))
            .OrderByDescending(item => item.Tree.GetTotalBytes(item.Node))
            .Take(3)
            .ToList();
        if (top.Count == 0)
        {
            return "Sizing folders. Results appear per drive as soon as each drive is done.";
        }

        return "Largest folders: " + string.Join(", ", top.Select(item => $"{item.Tree.GetPath(item.Node)} ({FormatBytes(item.Tree.GetTotalBytes(item.Node))})")) + ".";
    }

    private static string ResolveRepositoryRootPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("REPOS_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return configuredPath;
        }

        var sourceRepos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos");
        return Directory.Exists(sourceRepos) || !Directory.Exists(@"C:\dev") ? sourceRepos : @"C:\dev";
    }

    private static DiskUsageSection CreatePendingSection(string title, string summary)
    {
        return new DiskUsageSection(title, summary, [new DiskUsageEntry("Working...", "In progress", summary)]);
    }

    private static string FormatBytes(long bytes) => ByteFormatter.Format(bytes);

    /// <summary>Progress that calls straight through (no synchronization context capture).</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}

public sealed record DiskUsageSnapshot(DateTimeOffset CapturedAt, string Headline, string Analysis, IReadOnlyList<DiskUsageSection> Sections);

public sealed record DiskUsageProgress(string Headline, string StatusText, string Analysis, IReadOnlyList<DiskUsageSection> Sections, bool IsCompleted);
