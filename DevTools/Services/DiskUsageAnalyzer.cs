using System.Runtime.CompilerServices;
using DevTools.Infrastructure;
using DevTools.Models;
using DevTools.Services.BiggestFiles;
using DevTools.Services.BiggestFiles.Ntfs;
using DevTools.Services.FolderSizes;

namespace DevTools.Services;

/// <summary>
/// Disk usage for local fixed and removable volumes. Each drive is sized ONCE by <see cref="DriveUsageService"/>
/// (the NTFS MFT or a folder walk), and every section, expansion and folder listing
/// is then read from that in-memory tree, so nothing is measured twice and no section waits on
/// a separate slow scan. All heavy work runs off the UI thread.
/// </summary>
public sealed class DiskUsageAnalyzer
{
    /// <summary>Title prefix of the per-drive largest-folders sections.</summary>
    public const string LargestFoldersSectionTitle = "Largest Folders";

    internal const string DriveSummarySectionTitle = "Drive Summary";

    private const int DefaultTopCount = 10;
    private const int LargestFolderCount = 25;
    private const int LargestFolderChildren = 15;

    private readonly DriveUsageService _driveUsage = new();
    private readonly Func<string, CancellationToken, bool, Task<FolderTree>> _walkFolder;
    private readonly object _treesLock = new();
    private readonly Dictionary<(string Path, long InventoryVersion, bool Allocation), FolderWalk> _folderWalks = new();
    private readonly Dictionary<string, FolderTree> _trees = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (FolderTree Tree, bool AllocationRequested, long InventoryVersion)> _overlayTrees =
        new(StringComparer.Ordinal);
    private Dictionary<string, string?> _volumeNames = new(StringComparer.OrdinalIgnoreCase);
    private VolumeMountAlias[] _mountAliases = [];
    private long _inventoryVersion;

    public DiskUsageAnalyzer()
    {
        _walkFolder = _driveUsage.WalkFolderAsync;
    }

    internal DiskUsageAnalyzer(Func<string, CancellationToken, bool, Task<FolderTree>> walkFolder)
    {
        _walkFolder = walkFolder;
    }

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
        var drives = DriveUsageService.GetLocalVolumes();
        var browseRoots = drives.Select(drive => drive.RootPath).ToArray();
        var mountAliases = WindowsVolumePaths.GetMountAliases(drives, cancellationToken);
        var inventoryVersion = ReplaceVolumeInventory(drives, mountAliases);

        var headline = BuildHeadline(drives, capturedAt);
        var systemRoot = FolderTree.NormalizeRoot(Environment.SystemDirectory);

        var sections = new List<DiskUsageSection>();
        foreach (var drive in drives)
        {
            sections.Add(CreatePendingSection(LargestFoldersTitleFor(drive.DisplayRoot), $"Waiting to size every folder on {drive.DisplayRoot}."));
        }

        var derivedTitles = new[] { "User Profile", "AppData", "Repositories", "Installed Software", "Temp Hotspots", "Docker" };
        foreach (var title in derivedTitles)
        {
            sections.Add(CreatePendingSection(title, $"Filled in from the {systemRoot} scan."));
        }

        var results = new Dictionary<string, DriveScanResult>(StringComparer.OrdinalIgnoreCase);
        var currentStatus = $"Found {drives.Count} local volume(s). Scanning folders...";
        var currentAnalysis = "Sizing folders. Results appear per drive as soon as each drive is done.";
        var progressLock = new object();
        sections.Add(BuildDriveSummarySection(drives, results));
        progress?.Report(new DiskUsageProgress(headline, currentStatus, currentAnalysis, sections.ToList(), false)
        {
            BrowseRoots = browseRoots
        });

        cancellationToken.ThrowIfCancellationRequested();
        var extents = DiskCapacityInventory.ReadVolumeTopology(drives, cancellationToken, out var backingDisks,
            out var volumeExtentsByRoot);
        var diskCount = backingDisks.SelectMany(disks => disks).Distinct().Count();
        var possibleParallelism = Math.Min(diskCount, drives.Count);
        var elevated = OperatingSystem.IsWindows() && ElevationService.IsProcessElevated();
        var rawMftVolumes = drives.Select(drive => DriveUsageService.CanTryMft(drive, elevated)).ToArray();
        var concurrentScans = possibleParallelism > 1
            ? PlanVolumeParallelism(drives, rawMftVolumes, possibleParallelism, ScanResources.Capture(), cancellationToken)
            : 1;

        IReadOnlyList<DriveDescriptor> currentDrives = drives;
        IReadOnlyList<DiskCapacity>? diskCapacities = null;
        using var inventoryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var inventoryToken = inventoryCancellation.Token;
        var inventory = Task.Run(() =>
        {
            var capacities = DiskCapacityInventory.ReadPhysicalDisks(extents, inventoryToken);
            lock (progressLock)
            {
                inventoryToken.ThrowIfCancellationRequested();
                diskCapacities = capacities;
                if (capacities.Count > 0 || currentDrives.Count > 0)
                {
                    sections[^1] = BuildDriveSummarySection(currentDrives, results, capacities, volumeExtentsByRoot);
                    progress?.Report(new DiskUsageProgress(headline, currentStatus, currentAnalysis, sections.ToList(), false));
                }
            }
        }, inventoryToken);

        async Task<DriveScanResult> ScanVolumeAsync(int sectionIndex, int workerShare, CancellationToken scanToken)
        {
            var drive = drives[sectionIndex];
            var title = LargestFoldersTitleFor(drive.DisplayRoot);
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
                    currentStatus = $"Volume {sectionIndex + 1} of {drives.Count} ({drive.DisplayRoot}): {update.Message}";
                    progress?.Report(new DiskUsageProgress(headline, currentStatus, currentAnalysis, sections.ToList(), false));
                }
            });

            try
            {
                return await _driveUsage.ScanDriveAsync(drive, driveProgress, scanToken, workerShare, rawMftVolumes[sectionIndex])
                    .ConfigureAwait(false);
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

        try
        {
            await foreach (var (sectionIndex, result) in ScanVolumesAsync(backingDisks, concurrentScans, ScanVolumeAsync,
                cancellationToken, rawMftVolumes)
                .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var drive = drives[sectionIndex];
                PublishVolumeTree(inventoryVersion, drive.RootPath, result.Tree);

                var driveSection = BuildDriveSection(result);
                var derived = string.Equals(drive.RootPath, systemRoot, StringComparison.OrdinalIgnoreCase)
                    ? BuildDerivedSections(result.Tree)
                    : null;
                lock (progressLock)
                {
                    results[drive.RootPath] = result;
                    currentDrives = drives
                        .Select(item => results.TryGetValue(item.RootPath, out var completed) ? completed.Drive : item)
                        .ToArray();
                    sections[sectionIndex] = driveSection;
                    if (derived is not null)
                    {
                        for (var i = 0; i < derived.Count; i++)
                        {
                            sections[drives.Count + i] = derived[i];
                        }
                    }

                    headline = BuildHeadline(currentDrives, capturedAt);
                    sections[^1] = BuildDriveSummarySection(currentDrives, results, diskCapacities, volumeExtentsByRoot);
                    currentAnalysis = BuildAnalysis(results.Values);
                    currentStatus = $"Sized {results.Count} of {drives.Count} volume(s).";
                    progress?.Report(new DiskUsageProgress(headline, currentStatus, currentAnalysis, sections.ToList(), false)
                    {
                        BrowseRoots = browseRoots,
                        CompletedRoot = drive.RootPath
                    });
                }
            }

            await inventory.ConfigureAwait(false);
        }
        catch
        {
            inventoryCancellation.Cancel();
            try
            {
                await inventory.ConfigureAwait(false);
            }
            catch
            {
                // Keep the original scan, reporting, or cancellation error.
            }

            throw;
        }

        if (!results.ContainsKey(systemRoot))
        {
            // No system drive among the local volumes (unusual): drop the derived placeholders.
            sections.RemoveAll(section => derivedTitles.Contains(section.Title));
        }

        var analysis = BuildAnalysis(results.Values);
        progress?.Report(new DiskUsageProgress(headline, $"Last disk refresh: {capturedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}", analysis, sections.ToList(), true));
        return new DiskUsageSnapshot(capturedAt, headline, analysis, sections) { BrowseRoots = browseRoots };
    }

    internal long ReplaceVolumeInventory(IReadOnlyList<DriveDescriptor> drives, VolumeMountAlias[] mountAliases)
    {
        var volumeNames = drives.ToDictionary(drive => drive.RootPath, drive => drive.VolumeName, StringComparer.OrdinalIgnoreCase);
        lock (_treesLock)
        {
            bool StaysMounted(string root) =>
                _volumeNames.TryGetValue(root, out var previousName) && previousName is not null &&
                volumeNames.TryGetValue(root, out var currentName) &&
                previousName.Equals(currentName, StringComparison.OrdinalIgnoreCase);

            foreach (var root in _trees.Keys.Where(root => !StaysMounted(root)).ToArray())
            {
                _trees.Remove(root);
            }

            foreach (var path in _overlayTrees.Where(item => !StaysMounted(item.Value.Tree.RootPath)).Select(item => item.Key).ToArray())
            {
                _overlayTrees.Remove(path);
            }

            _volumeNames = volumeNames;
            _mountAliases = mountAliases;
            return ++_inventoryVersion;
        }
    }

    internal void PublishVolumeTree(long inventoryVersion, string root, FolderTree? tree)
    {
        lock (_treesLock)
        {
            if (inventoryVersion != _inventoryVersion)
            {
                return;
            }

            if (tree is null)
            {
                _trees.Remove(root);
            }
            else
            {
                _trees[root] = tree;
            }

            // Old priority scans can fill gaps while a refresh is pending. The new volume
            // snapshot supersedes them, while clicks during this refresh can still supply allocation.
            foreach (var path in _overlayTrees
                .Where(item => item.Value.InventoryVersion < inventoryVersion &&
                    item.Value.Tree.RootPath.Equals(root, StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Key).ToArray())
            {
                _overlayTrees.Remove(path);
            }
        }
    }

    private static int PlanVolumeParallelism(IReadOnlyList<DriveDescriptor> drives, bool[] rawMftVolumes,
        int independentDisks, ScanResources resources, CancellationToken cancellationToken)
    {
        var rawVolumeCount = rawMftVolumes.Count(raw => raw);
        var parallelism = resources.VolumeWalkParallelism(independentDisks);
        if (rawVolumeCount == 0 || parallelism == 1)
        {
            return parallelism;
        }

        parallelism = Math.Min(parallelism, drives.Count - rawVolumeCount + 1);
        if (parallelism == 1)
        {
            return 1;
        }

        // Estimate the largest table before allowing any directory workers to compete with it.
        using var errorMode = WindowsVolumePaths.SuppressCriticalErrors();
        for (var index = 0; index < drives.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rawMftVolumes[index])
            {
                var records = NativeMethods.GetDirectoryRecordCapacity(drives[index].RootPath);
                parallelism = resources.LimitParallelFolderMft(parallelism, records);
                if (parallelism == 1)
                {
                    break;
                }
            }
        }

        return parallelism;
    }

    internal static async IAsyncEnumerable<(int Index, DriveScanResult Result)> ScanVolumesAsync(
        IReadOnlyList<int[]> backingDisks, int parallelism, Func<int, int, CancellationToken, Task<DriveScanResult>> scan,
        [EnumeratorCancellation] CancellationToken cancellationToken, IReadOnlyList<bool>? rawMftVolumes = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        using var activeScans = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = Enumerable.Range(0, backingDisks.Count).ToList();
        var running = new List<(int Index, Task<DriveScanResult> Scan)>(parallelism);
        var remainingByDisk = backingDisks.SelectMany(disks => disks).GroupBy(disk => disk)
            .ToDictionary(group => group.Key, group => group.Count());

        bool CanOverlap(int left, int right)
        {
            var first = backingDisks[left];
            var second = backingDisks[right];
            // Keep at most one memory-intensive MFT scan in flight, including its fallback.
            if (first.Length == 0 || second.Length == 0 ||
                (rawMftVolumes is not null && rawMftVolumes[left] && rawMftVolumes[right]))
            {
                return false;
            }

            foreach (var disk in first)
            {
                if (Array.IndexOf(second, disk) >= 0)
                {
                    return false;
                }
            }

            return true;
        }

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
                    // All disks behind a volume occupy one scan slot. The remaining disk count
                    // bounds how many other scans can share its worker budget, including later starts.
                    var volumeDiskCount = backingDisks[index].Length;
                    var workerShare = volumeDiskCount == 0
                        ? 1
                        : Math.Min(parallelism, remainingByDisk.Count - volumeDiskCount + 1);
                    if (workerShare > 1 && running.Count == 0 && !pending.Exists(other => CanOverlap(index, other)))
                    {
                        workerShare = 1;
                    }

                    running.Add((index, scan(index, workerShare, activeScans.Token)));
                }

                var complete = await Task.WhenAny(running.Select(item => item.Scan)).ConfigureAwait(false);
                var slot = running.FindIndex(item => item.Scan == complete);
                var indexCompleted = running[slot].Index;
                running.RemoveAt(slot);
                foreach (var disk in backingDisks[indexCompleted])
                {
                    if (--remainingByDisk[disk] == 0)
                    {
                        remainingByDisk.Remove(disk);
                    }
                }

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

        var scanPath = ResolveWalkPath(entry.Path, out var inventoryVersion);
        var walked = await WalkFolderSharedAsync(scanPath, inventoryVersion, measureAllocation: false, cacheResult: false,
            cancellationToken).ConfigureAwait(false);
        var walkedNode = walked?.Find(scanPath) ?? -1;
        return walkedNode < 0 ? [] : BuildChildEntries(walked!, walkedNode, LargestFolderChildren, includeGrandchildren: false);
    }

    /// <summary>Finds the tree and node for a folder path from the last scan.</summary>
    public bool TryFindNode(string path, out FolderTree? tree, out int node) =>
        TryFindNode(path, preferAllocation: false, out tree, out node);

    internal bool TryFindNode(string path, bool preferAllocation, out FolderTree? tree, out int node)
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
            var alias = WindowsVolumePaths.ResolveMountAlias(path, _mountAliases);
            var scanPath = alias?.Path ?? path;
            if (_trees.TryGetValue(alias?.Mount.ScanRoot ?? root, out var found))
            {
                var foundNode = found.Find(scanPath);
                if (foundNode >= 0)
                {
                    tree = found;
                    node = foundNode;
                    if (!preferAllocation || found.GetAllocatedBytes(foundNode).HasValue)
                    {
                        return true;
                    }
                }
            }

            // A priority scan can supply allocation while the drive scan is pending or has logical sizes only.
            if (preferAllocation &&
                (TryFindOverlayNode(scanPath, requireAllocation: true, out var allocatedTree, out var allocatedNode) ||
                    (alias.HasValue && TryFindOverlayNode(path, requireAllocation: true, out allocatedTree, out allocatedNode))))
            {
                tree = allocatedTree;
                node = allocatedNode;
                return true;
            }

            return tree is not null || TryFindOverlayNode(scanPath, requireAllocation: false, out tree, out node)
                || (alias.HasValue && TryFindOverlayNode(path, requireAllocation: false, out tree, out node));
        }
    }

    internal string GetFolderBrowserDriveLabel(string root)
    {
        if (WindowsVolumePaths.GetVolumeGuid(root) is not { } id)
        {
            return root;
        }

        lock (_treesLock)
        {
            foreach (var alias in _mountAliases)
            {
                if (alias.AccessPath is { Length: 3 } &&
                    alias.ScanRoot.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    return alias.AccessPath;
                }
            }
        }

        return $"Volume {id.ToString("N")[..8]}";
    }

    private bool TryFindOverlayNode(string path, bool requireAllocation, out FolderTree? tree, out int node)
    {
        tree = null;
        node = -1;
        if (_overlayTrees.Count == 0)
        {
            return false;
        }

        var bestVersion = long.MinValue;
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var lookup = _overlayTrees.GetAlternateLookup<ReadOnlySpan<char>>();
        for (var candidate = Path.TrimEndingDirectorySeparator(normalized.AsSpan().Trim()); !candidate.IsEmpty;
             candidate = Path.GetDirectoryName(candidate))
        {
            if (!lookup.TryGetValue(candidate, out var entry) || entry.InventoryVersion <= bestVersion ||
                (requireAllocation && !entry.AllocationRequested))
            {
                continue;
            }

            var overlayNode = entry.Tree.Find(path);
            if (overlayNode >= 0 && (!requireAllocation || entry.Tree.GetAllocatedBytes(overlayNode).HasValue))
            {
                tree = entry.Tree;
                node = overlayNode;
                bestVersion = entry.InventoryVersion;
                if (bestVersion == _inventoryVersion)
                {
                    break;
                }
            }
        }

        return tree is not null;
    }

    private string ResolveWalkPath(string path, out long inventoryVersion)
    {
        ResolvedVolumeMount? resolved;
        lock (_treesLock)
        {
            inventoryVersion = _inventoryVersion;
            if (!Path.IsPathFullyQualified(path))
            {
                return path;
            }

            resolved = WindowsVolumePaths.ResolveMountAlias(path, _mountAliases);
            if (resolved.HasValue)
            {
                var fullPath = Path.GetFullPath(path);
                if (!fullPath.Equals(path, StringComparison.Ordinal))
                {
                    resolved = WindowsVolumePaths.ResolveMountAlias(fullPath, _mountAliases);
                }
            }
        }

        // A drive letter or mount can change while the volume inventory is cached.
        if (resolved is { } alias &&
            alias.Mount.VolumeName.Equals(WindowsVolumePaths.GetVolumeName(alias.Mount.AccessPath), StringComparison.OrdinalIgnoreCase) &&
            alias.Mount.VolumeName.Equals(WindowsVolumePaths.GetVolumeName(alias.Mount.ScanRoot), StringComparison.OrdinalIgnoreCase))
        {
            return alias.Path;
        }

        return path;
    }

    /// <summary>
    /// Sizes a folder the user just clicked, ahead of the all-drive scan, unless it is already known.
    /// The background scans pause while this runs. Returns false when the folder does not exist.
    /// </summary>
    public Task<bool> SizeFolderNowAsync(string path, CancellationToken cancellationToken) =>
        SizeFolderNowAsync(path, cancellationToken, measureAllocation: false);

    internal async Task<bool> SizeFolderNowAsync(string path, CancellationToken cancellationToken, bool measureAllocation)
    {
        var known = TryFindNode(path, measureAllocation, out var knownTree, out var knownNode);
        if (known && (!measureAllocation || knownTree!.GetAllocatedBytes(knownNode).HasValue))
        {
            return true;
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        var scanPath = ResolveWalkPath(path, out var inventoryVersion);
        return await WalkFolderSharedAsync(scanPath, inventoryVersion, measureAllocation, cacheResult: true,
            cancellationToken).ConfigureAwait(false) is not null;
    }

    private async Task<FolderTree?> WalkFolderSharedAsync(string path, long inventoryVersion, bool measureAllocation,
        bool cacheResult, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var key = (Path: normalized, InventoryVersion: inventoryVersion, Allocation: measureAllocation);
        FolderWalk walk;
        lock (_treesLock)
        {
            if (inventoryVersion != _inventoryVersion)
            {
                return null;
            }

            if (cacheResult && _overlayTrees.TryGetValue(normalized, out var cachedResult) &&
                cachedResult.InventoryVersion == inventoryVersion &&
                (cachedResult.AllocationRequested || (!measureAllocation && cachedResult.Tree.Find(normalized) >= 0)))
            {
                return cachedResult.Tree;
            }

            var allocationKey = (normalized, inventoryVersion, true);
            if (!measureAllocation && _folderWalks.TryGetValue(allocationKey, out var allocationWalk))
            {
                key = allocationKey;
                walk = allocationWalk;
            }
            else if (_folderWalks.TryGetValue(key, out var active))
            {
                walk = active;
            }
            else
            {
                var source = new CancellationTokenSource();
                try
                {
                    walk = new FolderWalk(_walkFolder(normalized, source.Token, measureAllocation), source);
                    _folderWalks.Add(key, walk);
                }
                catch
                {
                    source.Dispose();
                    throw;
                }
            }

            walk.Waiters++;
        }

        try
        {
            var tree = await walk.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_treesLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (inventoryVersion != _inventoryVersion)
                {
                    return null;
                }

                if (cacheResult &&
                    (!_overlayTrees.TryGetValue(normalized, out var cached) || cached.InventoryVersion != inventoryVersion ||
                     !cached.AllocationRequested))
                {
                    _overlayTrees[normalized] = (tree, key.Allocation, inventoryVersion);
                }
            }

            return tree;
        }
        finally
        {
            bool lastWaiter;
            lock (_treesLock)
            {
                lastWaiter = --walk.Waiters == 0;
                if (lastWaiter)
                {
                    _folderWalks.Remove(key);
                }
            }

            if (lastWaiter)
            {
                // Keep cancellation and asynchronous cleanup outside the tree lock.
                using var source = walk.Cancellation;
                if (!walk.Task.IsCompleted)
                {
                    try
                    {
                        await source.CancelAsync().ConfigureAwait(false);
                    }
                    catch (AggregateException ex)
                    {
                        AppLog.Error("Canceling the folder walk failed.", ex);
                    }
                }

                try
                {
                    await walk.Task.ConfigureAwait(false);
                }
                catch
                {
                    // The caller already received its result, cancellation or original error.
                }
            }
        }
    }

    private sealed class FolderWalk(Task<FolderTree> task, CancellationTokenSource cancellation)
    {
        public Task<FolderTree> Task { get; } = task;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public int Waiters { get; set; }
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
            var scanPath = ResolveWalkPath(path, out var inventoryVersion);
            var walked = await WalkFolderSharedAsync(scanPath, inventoryVersion, measureAllocation: false, cacheResult: false,
                cancellationToken).ConfigureAwait(false);
            return walked?.TotalBytes;
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
            return $"No local volumes were found ({capturedAt:yyyy-MM-dd HH:mm:ss}).";
        }

        var parts = drives.Where(drive => drive.HasDriveLetter && drive.Space.HasValue).Select(drive =>
        {
            var space = drive.Space.GetValueOrDefault();
            var percent = space.TotalBytes <= 0 ? 0 : (double)space.UsedBytes / space.TotalBytes * 100d;
            var scope = space.IsVolumeWide ? string.Empty : " (caller-visible totals)";
            return $"{drive.DisplayRoot} {FormatBytes(space.UsedBytes)} used of {FormatBytes(space.TotalBytes)} ({percent:0.0}%), {FormatBytes(space.FreeBytes)} free{scope}";
        });
        var additionalVolumes = drives.Count(drive => !drive.HasDriveLetter);
        if (additionalVolumes > 0)
        {
            parts = parts.Append($"{additionalVolumes} local volume(s) without drive letters in Drive Summary");
        }

        return $"Snapshot {capturedAt:yyyy-MM-dd HH:mm:ss}. " + string.Join(". ", parts) + ".";
    }

    private static DiskUsageSection BuildDriveSection(DriveScanResult result)
    {
        var title = LargestFoldersTitleFor(result.Drive.DisplayRoot);
        if (result.Tree is null)
        {
            return new DiskUsageSection(title, $"Could not size the folders on {result.Drive.DisplayRoot}. {result.Note}",
                [DiskUsageEntry.CreateCompleted(result.Drive.RootPath, true, null, "Unavailable", result.Note)]);
        }

        var tree = result.Tree;
        var entries = BuildChildEntries(tree, 0, LargestFolderCount, includeGrandchildren: true);
        var rootFiles = tree.GetDirectFileBytes(0);
        if (rootFiles > 0)
        {
            entries.Add(DiskUsageEntry.CreateCompleted(tree.RootPath, true, rootFiles, FormatBytes(rootFiles), "Files directly in the drive root.", $"Files in {result.Drive.DisplayRoot}"));
        }

        var summary =
            $"{tree.EngineName} sized {tree.FolderCount:N0} folders and {tree.TotalFiles:N0} files on {result.Drive.DisplayRoot} in {tree.Elapsed.TotalSeconds:F1}s. " +
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
        IReadOnlyList<DiskCapacity>? diskCapacities = null,
        IReadOnlyDictionary<string, VolumeExtentSize[]>? volumeExtentsByRoot = null)
    {
        var entries = new List<DiskUsageEntry>();
        foreach (var drive in drives)
        {
            var label = string.IsNullOrWhiteSpace(drive.Label) ? drive.FileSystem : $"{drive.Label}, {drive.FileSystem}";
            var display = string.IsNullOrWhiteSpace(label) ? drive.DisplayRoot : $"{drive.DisplayRoot} ({label})";
            results.TryGetValue(drive.RootPath, out var result);
            var tree = result?.Tree;
            var bitmap = tree?.NtfsAllocation?.Bitmap;
            var incompleteNote = DescribeIncompleteFolderScan(tree) + DescribeIncompleteAllocation(tree) +
                DescribeIncompleteMftAllocation(tree);
            var totals = drive.Space is { } space
                ? DescribeVolumeTotals(space)
                : "Windows did not report space totals for this volume";
            long? volumeUsed = drive.Space is { IsVolumeWide: true } reported ? reported.UsedBytes : null;

            var scanNote = tree is not null
                ? $"Logical file size: {tree.TotalBytes:N0} bytes in {tree.TotalFiles:N0} files ({tree.EngineName}, {tree.Elapsed.TotalSeconds:F1}s)."
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

                if (bitmap is null)
                {
                    if (volumeUsed is long used)
                    {
                        var remainingUsed = used - observed;
                        scanNote += remainingUsed >= 0
                            ? $" {remainingUsed:N0} used bytes remain unattributed."
                            : $" Observed allocation exceeded reported usage by {-remainingUsed:N0} bytes during the scan.";
                    }
                    else
                    {
                        scanNote += " Volume-wide usage is unavailable for reconciliation.";
                    }
                }
            }
            else if (result is not null)
            {
                scanNote += volumeUsed is long used
                    ? $" {used:N0} used bytes have no folder attribution."
                    : " Folder allocation is unavailable.";
            }

            var extentNote = volumeExtentsByRoot is null
                ? string.Empty
                : volumeExtentsByRoot.TryGetValue(drive.RootPath, out var extents)
                    ? DescribeVolumeExtents(extents, drive.Space)
                    : " Windows did not report disk extents for this volume.";
            var bitmapNote = DescribeNtfsBitmap(bitmap, volumeUsed);
            var note = $"{totals}.{extentNote} {scanNote}{incompleteNote}{bitmapNote}";
            var usedBytes = volumeUsed ?? bitmap?.InUseBytes ?? drive.Space?.UsedBytes;
            var sizeText = usedBytes switch
            {
                long bytes when volumeUsed.HasValue => $"{FormatBytes(bytes)} used",
                long bytes when bitmap.HasValue => $"{FormatBytes(bytes)} allocated (NTFS bitmap)",
                long bytes => $"{FormatBytes(bytes)} used (caller-visible)",
                _ => "Usage unavailable"
            };
            var hasBrowsableRoot = tree is not null || (drive.HasDriveLetter && drive.Space.HasValue);
            entries.Add(DiskUsageEntry.CreateCompleted(drive.RootPath, hasBrowsableRoot, usedBytes, sizeText, note, display));
        }

        var summary = "Space reported by Windows for each local volume.";
        if (diskCapacities is { Count: > 0 })
        {
            summary += Environment.NewLine + string.Join(Environment.NewLine, diskCapacities.Select(DescribeDiskCapacity));
        }

        return new DiskUsageSection(DriveSummarySectionTitle, summary, entries);
    }

    private static string DescribeVolumeExtents(VolumeExtentSize[] extents, VolumeSpace? space)
    {
        Int128 bytes = 0;
        foreach (var extent in extents)
        {
            bytes += extent.Bytes;
        }

        var note = extents.Length == 1
            ? $" Windows reports {bytes:N0} bytes in volume extents on Disk {extents[0].DiskNumber}."
            : $" Windows reports {bytes:N0} bytes in volume extents (" +
                string.Join(", ", extents.Select(extent => $"{extent.Bytes:N0} on Disk {extent.DiskNumber}")) + ").";
        if (space is not { IsVolumeWide: true } volume)
        {
            return note;
        }

        var difference = bytes - volume.TotalBytes;
        if (difference > 0)
        {
            return note + $" The reported extents exceed the filesystem capacity by {difference:N0} bytes.";
        }

        return difference < 0
            ? note + $" The filesystem capacity exceeds the reported extents by {-difference:N0} bytes."
            : note;
    }

    private static string DescribeVolumeTotals(VolumeSpace space)
    {
        var totals = $"{space.TotalBytes:N0} bytes total, {space.UsedBytes:N0} used, {space.FreeBytes:N0} free";
        totals += space.IsVolumeWide
            ? $", {space.ReservedBytes:N0} reserved, {space.PoolUnavailableBytes:N0} unavailable in the storage pool"
            : " (caller-visible totals)";

        var remainder = space.UnclassifiedBytes;
        return remainder switch
        {
            > 0 => $"{totals}, {remainder:N0} unclassified",
            < 0 => $"{totals}, Windows categories overlap by {-remainder:N0} bytes",
            _ => totals
        };
    }

    private static string DescribeDiskCapacity(DiskCapacity disk)
    {
        if (disk.TotalBytes is not long capacity)
        {
            return disk.VolumeExtentBytes > 0
                ? $"Disk {disk.DiskNumber} capacity is unavailable. Windows reports {disk.VolumeExtentBytes:N0} bytes in volume extents."
                : $"Disk {disk.DiskNumber} capacity is unavailable. Windows reported no volume extents.";
        }

        var total = $"Disk {disk.DiskNumber} has {capacity:N0} bytes.";
        var matchedRemainder = disk.MatchedExtentBytes - disk.FileSystemBytes;
        var otherVolumeExtents = disk.VolumeExtentBytes - disk.MatchedExtentBytes;
        var otherExtentNote = otherVolumeExtents > 0 ? $"{otherVolumeExtents:N0} in other volume extents, " : string.Empty;
        var volumeNotes = $"{disk.FileSystemBytes:N0} bytes in matched filesystem capacities, " +
            $"{matchedRemainder:N0} inside matched volume extents beyond their filesystem capacities, " +
            otherExtentNote;
        if (disk.PartitionedBytes is not long partitioned)
        {
            return $"{total} {volumeNotes}and {capacity - disk.VolumeExtentBytes:N0} outside reported volume extents. " +
                "Partition ranges are unavailable.";
        }

        var extentsOutsidePartitions = disk.VolumeExtentBytes - disk.VolumeExtentBytesInPartitions;
        var otherPartitions = partitioned - disk.VolumeExtentBytesInPartitions;
        var outside = capacity - partitioned - extentsOutsidePartitions;
        var outsideNote = (disk.UnassignedGptUsableBytes, disk.UnassignedMbrSectorBytes) switch
        {
            (long usable, _) => $"{usable:N0} unallocated in the GPT usable range and {outside - usable:N0} remaining outside the GPT usable range.",
            (_, long bootSector) => $"{bootSector:N0} in the master boot record sector and {outside - bootSector:N0} elsewhere outside partition and volume ranges.",
            _ when extentsOutsidePartitions > 0 => $"{outside:N0} outside partition and volume ranges.",
            _ => $"{outside:N0} outside partition ranges."
        };
        if (extentsOutsidePartitions > 0)
        {
            outsideNote += $" Of the reported volume extents, {extentsOutsidePartitions:N0} bytes are outside partition ranges.";
        }

        return $"{total} {volumeNotes}{otherPartitions:N0} in other partition ranges and {outsideNote}";
    }

    private static string DescribeIncompleteFolderScan(FolderTree? tree) =>
        tree is { IncompleteDirectoryCount: > 0 }
            ? $" Incomplete listings in {tree.IncompleteDirectoryCount:N0} {(tree.IncompleteDirectoryCount == 1 ? "directory" : "directories")}."
            : string.Empty;

    private static string DescribeIncompleteAllocation(FolderTree? tree) =>
        tree is { IncompleteAllocationDirectoryCount: > 0 }
            ? $" Incomplete allocation in {tree.IncompleteAllocationDirectoryCount:N0} {(tree.IncompleteAllocationDirectoryCount == 1 ? "directory" : "directories")}."
            : string.Empty;

    internal static string DescribeIncompleteMftAllocation(FolderTree? tree) =>
        tree?.NtfsAllocation is { IncompleteRecordCount: > 0 } allocation
            ? $" Incomplete allocation in {allocation.IncompleteRecordCount:N0} NTFS {(allocation.IncompleteRecordCount == 1 ? "record" : "records")}."
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

        if (tree.HasIncompleteAllocation(node))
        {
            note += ". Allocation incomplete.";
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
