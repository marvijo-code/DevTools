using System.Runtime.CompilerServices;
using DevTools.Infrastructure;
using DevTools.Models;
using DevTools.Services.BiggestFiles;
using DevTools.Services.BiggestFiles.Ntfs;
using DevTools.Services.FolderSizes;

namespace DevTools.Services;

/// <summary>
/// Disk usage for every fixed drive. Each drive is sized ONCE by <see cref="DriveUsageService"/>
/// (the NTFS MFT or a folder walk), and every section, expansion and folder listing
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
    private readonly Dictionary<string, FolderTree> _overlayTrees = new(StringComparer.Ordinal);

    /// <summary>The folder trees from the last scan, system drive first.</summary>
    public IReadOnlyList<FolderTree> Trees
    {
        get
        {
            lock (_treesLock)
            {
                var systemRoot = FolderTree.NormalizeRoot(Environment.SystemDirectory);
                return _trees.Values.OrderByDescending(tree => string.Equals(tree.RootPath, systemRoot, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(tree => tree.RootPath, StringComparer.OrdinalIgnoreCase).ToList();
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
        var drives = DriveUsageService.GetFixedDrives(includeVolumesWithoutDriveLetters: true);
        var diskCapacities = DiskCapacityInventory.Read(drives, cancellationToken, out var backingDisks);
        var hasSeparateDisks = backingDisks.Where(disk => disk.HasValue).Distinct().Skip(1).Any();
        var useRawMft = hasSeparateDisks && OperatingSystem.IsWindows() && drives.Any(drive =>
            string.Equals(drive.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)) && ElevationService.IsProcessElevated();
        var concurrentWalks = hasSeparateDisks && !useRawMft && ScanResources.Capture().CanOverlapVolumeWalks ? 2 : 1;
        var browseRoots = drives.Select(drive => drive.RootPath).ToArray();
        lock (_treesLock)
        {
            foreach (var root in _trees.Keys.Where(root => !browseRoots.Contains(root, StringComparer.OrdinalIgnoreCase)).ToArray())
            {
                _trees.Remove(root);
            }
        }

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

        sections.Add(BuildDriveSummarySection(drives, new Dictionary<string, DriveScanResult>(StringComparer.OrdinalIgnoreCase), diskCapacities));
        progress?.Report(new DiskUsageProgress(headline, $"Found {drives.Count} fixed volume(s). Scanning folders...", "Sizing folders. Results appear per drive as soon as each drive is done.", sections.ToList(), false)
        {
            BrowseRoots = browseRoots
        });

        var results = new Dictionary<string, DriveScanResult>(StringComparer.OrdinalIgnoreCase);
        var currentAnalysis = BuildAnalysis([]);
        var progressLock = new object();

        async Task<DriveScanResult> ScanVolumeAsync(int sectionIndex, int workerShare, CancellationToken scanToken)
        {
            var drive = drives[sectionIndex];
            var title = LargestFoldersTitleFor(drive.RootPath);
            var lastStatus = DateTime.MinValue;
            var driveProgress = new InlineProgress<FolderScanProgress>(update =>
            {
                lock (progressLock)
                {
                    var now = DateTime.UtcNow;
                    if ((now - lastStatus).TotalMilliseconds < 400)
                    {
                        return;
                    }

                    lastStatus = now;
                    var elapsed = update.Elapsed > TimeSpan.Zero ? $" ({update.Elapsed.TotalSeconds:F0}s)" : string.Empty;
                    sections[sectionIndex] = CreatePendingSection(title, $"{update.EngineName}: {update.Message}{elapsed}");
                    progress?.Report(new DiskUsageProgress(headline, $"Volume {sectionIndex + 1} of {drives.Count} ({drive.RootPath}): {update.Message}", currentAnalysis, sections.ToList(), false));
                }
            });

            try
            {
                return await _driveUsage.ScanDriveAsync(drive, driveProgress, scanToken, workerShare).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Sizing {drive.RootPath} failed.", ex);
                return new DriveScanResult(drive, null, "None", ex.Message);
            }
        }

        await foreach (var (sectionIndex, result) in ScanVolumesAsync(backingDisks, concurrentWalks, ScanVolumeAsync, cancellationToken)
            .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var drive = drives[sectionIndex];
            results[drive.RootPath] = result;
            lock (_treesLock)
            {
                if (result.Tree is { } tree)
                {
                    _trees[drive.RootPath] = tree;
                }
                else
                {
                    _trees.Remove(drive.RootPath);
                }
            }

            var driveSection = BuildDriveSection(result);
            var derived = string.Equals(drive.RootPath, systemRoot, StringComparison.OrdinalIgnoreCase)
                ? BuildDerivedSections(result.Tree)
                : null;
            var currentDrives = drives
                .Select(item => results.TryGetValue(item.RootPath, out var completed) ? completed.Drive : item)
                .ToArray();
            lock (progressLock)
            {
                sections[sectionIndex] = driveSection;
                if (derived is not null)
                {
                    for (var i = 0; i < derived.Count; i++)
                    {
                        sections[drives.Count + i] = derived[i];
                    }
                }

                headline = BuildHeadline(currentDrives, capturedAt);
                sections[^1] = BuildDriveSummarySection(currentDrives, results, diskCapacities);
                currentAnalysis = BuildAnalysis(results.Values);
                progress?.Report(new DiskUsageProgress(headline, $"Sized {results.Count} of {drives.Count} volume(s).", currentAnalysis, sections.ToList(), false)
                {
                    BrowseRoots = browseRoots,
                    CompletedRoot = drive.RootPath
                });
            }
        }

        if (!results.ContainsKey(systemRoot))
        {
            // No system drive among the fixed drives (unusual): drop the derived placeholders.
            sections.RemoveAll(section => derivedTitles.Contains(section.Title));
        }

        var analysis = BuildAnalysis(results.Values);
        progress?.Report(new DiskUsageProgress(headline, $"Last disk refresh: {capturedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}", analysis, sections.ToList(), true));
        return new DiskUsageSnapshot(capturedAt, headline, analysis, sections) { BrowseRoots = browseRoots };
    }

    internal static async IAsyncEnumerable<(int Index, DriveScanResult Result)> ScanVolumesAsync(
        IReadOnlyList<int?> backingDisks, int parallelism, Func<int, int, CancellationToken, Task<DriveScanResult>> scan,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        using var activeScans = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = Enumerable.Range(0, backingDisks.Count).ToList();
        var running = new List<(int Index, Task<DriveScanResult> Scan)>(parallelism);

        bool CanOverlap(int left, int right) =>
            backingDisks[left] is int leftDisk && backingDisks[right] is int rightDisk && leftDisk != rightDisk;

        bool CanStart(int index) => running.All(item => CanOverlap(index, item.Index));

        try
        {
            while (pending.Count > 0 || running.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                while (running.Count < parallelism)
                {
                    var next = pending.FindIndex(CanStart);
                    if (next < 0)
                    {
                        break;
                    }

                    var index = pending[next];
                    pending.RemoveAt(next);
                    var canRunWithAnother = running.Count > 0 ||
                        (parallelism > 1 && pending.Exists(other => CanOverlap(index, other)));
                    running.Add((index, scan(index, canRunWithAnother ? parallelism : 1, activeScans.Token)));
                }

                var complete = await Task.WhenAny(running.Select(item => item.Scan)).ConfigureAwait(false);
                var slot = running.FindIndex(item => item.Scan == complete);
                var indexCompleted = running[slot].Index;
                running.RemoveAt(slot);
                yield return (indexCompleted, await complete.ConfigureAwait(false));
            }
        }
        finally
        {
            if (running.Count > 0)
            {
                activeScans.Cancel();
                try
                {
                    await Task.WhenAll(running.Select(item => item.Scan)).ConfigureAwait(false);
                }
                catch
                {
                    // Finish draining the other scans without replacing the original error or cancellation.
                }
            }
        }
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
        if (string.IsNullOrWhiteSpace(path) || Path.GetPathRoot(path) is not { } root)
        {
            return false;
        }

        if (root.Length == 2 && root[1] == ':')
        {
            root += Path.DirectorySeparatorChar;
        }

        lock (_treesLock)
        {
            if (_trees.TryGetValue(root, out var found))
            {
                var foundNode = found.Find(path);
                if (foundNode >= 0)
                {
                    tree = found;
                    node = foundNode;
                    return true;
                }
            }

            // Not in a drive tree (yet): use a folder the user clicked that was sized on demand.
            var trimmed = Path.TrimEndingDirectorySeparator(path.Trim());
            foreach (var (folder, overlay) in _overlayTrees)
            {
                var underOverlay = trimmed.Equals(folder, StringComparison.Ordinal)
                    || (trimmed.Length > folder.Length
                        && trimmed.StartsWith(folder, StringComparison.Ordinal)
                        && trimmed[folder.Length] == Path.DirectorySeparatorChar);
                if (!underOverlay)
                {
                    continue;
                }

                var overlayNode = overlay.Find(path);
                if (overlayNode >= 0)
                {
                    tree = overlay;
                    node = overlayNode;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Sizes a folder the user just clicked, ahead of the all-drive scan, unless it is already known.
    /// The background scans pause while this runs. Returns false when the folder does not exist.
    /// </summary>
    public async Task<bool> SizeFolderNowAsync(string path, CancellationToken cancellationToken)
    {
        if (TryFindNode(path, out _, out _))
        {
            return true;
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        var tree = await _driveUsage.WalkFolderAsync(path, cancellationToken).ConfigureAwait(false);
        lock (_treesLock)
        {
            _overlayTrees[Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))] = tree;
        }

        return true;
    }

    /// <summary>
    /// Size of one folder (or file) in bytes, or null when it could not be measured. A folder that is already
    /// in the last drive scan is read from that tree for free; anything else is walked on its own.
    /// </summary>
    public async Task<long?> MeasureDirectoryBytesAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (File.Exists(path))
            {
                return new FileInfo(path).Length;
            }

            if (!Directory.Exists(path))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PathTooLongException)
        {
            AppLog.Info($"Could not inspect {path}: {ex.Message}");
            return null;
        }

        if (TryFindNode(path, out var tree, out var node))
        {
            return tree!.GetTotalBytes(node);
        }

        try
        {
            var walked = await _driveUsage.WalkFolderAsync(path, cancellationToken).ConfigureAwait(false);
            return walked.TotalBytes;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Measuring {path} failed.", ex);
            return null;
        }
    }

    private static string BuildHeadline(IReadOnlyList<DriveDescriptor> drives, DateTimeOffset capturedAt)
    {
        if (drives.Count == 0)
        {
            return $"No fixed drives were found ({capturedAt:yyyy-MM-dd HH:mm:ss}).";
        }

        var parts = drives.Where(drive => drive.HasDriveLetter && drive.Space.HasValue).Select(drive =>
        {
            var space = drive.Space.GetValueOrDefault();
            var percent = space.TotalBytes <= 0 ? 0 : (double)space.UsedBytes / space.TotalBytes * 100d;
            var scope = space.IsVolumeWide ? string.Empty : " (caller-visible totals)";
            return $"{drive.RootPath} {FormatBytes(space.UsedBytes)} used of {FormatBytes(space.TotalBytes)} ({percent:0.0}%), {FormatBytes(space.FreeBytes)} free{scope}";
        });
        var additionalVolumes = drives.Count(drive => !drive.HasDriveLetter);
        if (additionalVolumes > 0)
        {
            parts = parts.Append($"{additionalVolumes} fixed volume(s) without drive letters in Drive Summary");
        }

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

        var summary =
            $"{tree.EngineName} sized {tree.FolderCount:N0} folders and {tree.TotalFiles:N0} files on {tree.RootPath} in {tree.Elapsed.TotalSeconds:F1}s. " +
            $"Files total {FormatBytes(tree.TotalBytes)} by logical size. Open the Folder Browser to explore." +
            DescribeIncompleteFolderScan(tree);
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

    internal static DiskUsageSection BuildDriveSummarySection(
        IReadOnlyList<DriveDescriptor> drives,
        IReadOnlyDictionary<string, DriveScanResult> results,
        IReadOnlyList<DiskCapacity>? diskCapacities = null)
    {
        var entries = new List<DiskUsageEntry>();
        foreach (var drive in drives)
        {
            var label = string.IsNullOrWhiteSpace(drive.Label) ? drive.FileSystem : $"{drive.Label}, {drive.FileSystem}";
            var display = string.IsNullOrWhiteSpace(label) ? drive.RootPath : $"{drive.RootPath} ({label})";
            results.TryGetValue(drive.RootPath, out var result);
            var tree = result?.Tree;
            var bitmap = tree?.NtfsAllocation?.Bitmap;
            var incompleteNote = DescribeIncompleteFolderScan(tree);
            if (drive.Space is not { } space)
            {
                entries.Add(DiskUsageEntry.CreateCompleted(drive.RootPath, false, null, "Unavailable",
                    $"Windows did not report space totals for this volume.{incompleteNote}{DescribeNtfsBitmap(bitmap)}", display));
                continue;
            }

            var totals = $"{space.TotalBytes:N0} bytes total, {space.UsedBytes:N0} used, {space.FreeBytes:N0} free";
            if (space.IsVolumeWide)
            {
                totals += $", {space.ReservedBytes:N0} reserved, {space.PoolUnavailableBytes:N0} unavailable in the storage pool";
            }
            else
            {
                totals += " (caller-visible totals)";
            }

            var remainder = space.UnclassifiedBytes;
            if (remainder != 0)
            {
                totals += remainder > 0
                    ? $", {remainder:N0} unclassified"
                    : $", Windows categories overlap by {-remainder:N0} bytes";
            }

            var scanNote = tree is not null
                ? $"Logical file size: {tree.TotalBytes:N0} bytes ({tree.EngineName}, {tree.Elapsed.TotalSeconds:F1}s)."
                : result?.Note ?? "Folder scan pending.";
            if (tree?.GetAllocatedBytes(0) is long allocatedBytes)
            {
                var observed = (Int128)allocatedBytes;
                if (tree.NtfsAllocation is { } ntfs)
                {
                    observed += (Int128)ntfs.ReservedBytes + ntfs.UnlinkedBytes;
                    scanNote += bitmap is null
                        ? $" {allocatedBytes:N0} bytes assigned to folders, {ntfs.ReservedBytes:N0} in reserved NTFS records, " +
                            $"and {ntfs.UnlinkedBytes:N0} in NTFS records without a verified folder."
                        : $" {allocatedBytes:N0} bytes referenced by folder records, {ntfs.ReservedBytes:N0} by reserved NTFS records, " +
                            $"and {ntfs.UnlinkedBytes:N0} by unlinked NTFS records.";
                }
                else
                {
                    scanNote += $" {allocatedBytes:N0} bytes of allocation attributed by the scan.";
                }

                if (!space.IsVolumeWide)
                {
                    scanNote += " Volume-wide usage is unavailable for reconciliation.";
                }
                else if (bitmap is null)
                {
                    var remainingUsed = space.UsedBytes - observed;
                    scanNote += remainingUsed >= 0
                        ? $" {remainingUsed:N0} used bytes remain unattributed."
                        : $" Observed allocation exceeded reported usage by {-remainingUsed:N0} bytes during the scan.";
                }
            }
            else if (result is not null)
            {
                scanNote += space.IsVolumeWide
                    ? $" {space.UsedBytes:N0} used bytes have no folder attribution."
                    : " Folder allocation is unavailable.";
            }

            var bitmapNote = DescribeNtfsBitmap(bitmap, space.IsVolumeWide ? space.UsedBytes : null);
            var note = $"{totals}. {scanNote}{incompleteNote}{bitmapNote}";
            entries.Add(DiskUsageEntry.CreateCompleted(drive.RootPath, drive.HasDriveLetter || result?.Tree is not null, space.UsedBytes,
                $"{FormatBytes(space.UsedBytes)} used", note, display));
        }

        var summary = "Space reported by Windows for each fixed volume.";
        if (diskCapacities is { Count: > 0 })
        {
            summary += Environment.NewLine + string.Join(Environment.NewLine, diskCapacities.Select(DescribeDiskCapacity));
        }

        return new DiskUsageSection("Drive Summary", summary, entries);
    }

    private static string DescribeDiskCapacity(DiskCapacity disk)
    {
        var total = $"Disk {disk.DiskNumber} has {disk.TotalBytes:N0} bytes.";
        if (disk.PartitionedBytes is not long partitioned)
        {
            return $"{total} Partition ranges are unavailable.";
        }

        var partitionRemainder = disk.MatchedPartitionBytes - disk.FileSystemBytes;
        var unmatched = partitioned - disk.MatchedPartitionBytes;
        var outside = disk.TotalBytes - partitioned;
        return $"{total} {disk.FileSystemBytes:N0} bytes in matched filesystem capacities, " +
            $"{partitionRemainder:N0} inside matched partitions beyond their filesystem capacities, " +
            $"{unmatched:N0} in other partition ranges and {outside:N0} outside partition ranges.";
    }

    private static string DescribeIncompleteFolderScan(FolderTree? tree) =>
        tree is { IncompleteDirectoryCount: > 0 }
            ? $" Incomplete listings in {tree.IncompleteDirectoryCount:N0} {(tree.IncompleteDirectoryCount == 1 ? "directory" : "directories")}."
            : string.Empty;

    private static string DescribeNtfsBitmap(NtfsBitmapAllocation? allocation, long? volumeUsedBytes = null)
    {
        if (allocation is not { } bitmap)
        {
            return string.Empty;
        }

        var withoutRun = bitmap.InUseBytes - bitmap.MatchedBytes;
        var note = $" The NTFS bitmap reported {bitmap.InUseBytes:N0} allocated bytes. " +
            $"Of these, {bitmap.MatchedBytes:N0} appear in MFT runs and {withoutRun:N0} have no matching run.";
        if (bitmap.RecordedFreeBytes > 0)
        {
            note += $" {bitmap.RecordedFreeBytes:N0} bytes in MFT runs were marked free in the bitmap.";
        }

        if (bitmap.RepeatedReferenceBytes > 0)
        {
            note += $" MFT runs contain {bitmap.RepeatedReferenceBytes:N0} bytes of repeated cluster references.";
        }

        if (volumeUsedBytes is long used && used != bitmap.InUseBytes)
        {
            var difference = (Int128)used - bitmap.InUseBytes;
            note += $" Windows volume counters reported {Int128.Abs(difference):N0} " +
                $"{(difference > 0 ? "more" : "fewer")} used bytes than the bitmap pass.";
        }

        return note;
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
        var note = tree.GetAllocatedBytes(node) is long allocatedBytes
            ? $"{tree.GetFileCount(node):N0} files, {FormatBytes(allocatedBytes)} attributed allocation"
            : $"{tree.GetFileCount(node):N0} files";
        if (tree.HasIncompleteScan(node))
        {
            note += ". Folder scan incomplete.";
        }

        var entry = DiskUsageEntry.CreateCompleted(path, true, size, FormatBytes(size), note, ResolveFolderDisplayName(path));
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
        return RepositoryRootLocator.Resolve();
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

public sealed record DiskUsageSnapshot(DateTimeOffset CapturedAt, string Headline, string Analysis, IReadOnlyList<DiskUsageSection> Sections)
{
    public IReadOnlyList<string> BrowseRoots { get; init; } = [];
}

public sealed record DiskUsageProgress(string Headline, string StatusText, string Analysis, IReadOnlyList<DiskUsageSection> Sections, bool IsCompleted)
{
    public IReadOnlyList<string>? BrowseRoots { get; init; }
    public string? CompletedRoot { get; init; }
}
