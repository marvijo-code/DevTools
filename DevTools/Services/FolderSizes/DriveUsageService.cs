using System.Runtime.InteropServices;
using DevTools.Infrastructure;
using DevTools.Services.BiggestFiles;

namespace DevTools.Services.FolderSizes;

/// <summary>One local volume as the scan sees it.</summary>
public sealed record DriveDescriptor(string RootPath, string Label, string FileSystem, VolumeSpace? Space, bool HasDriveLetter = true)
{
    internal string? VolumeName { get; init; }
    internal string? DriveLetterRoot { get; init; }
    internal string DisplayRoot => DriveLetterRoot ?? RootPath;

    internal static DriveDescriptor FromDriveLetter(string rootPath, DriveType type, string? volumeName,
        string label, string fileSystem, VolumeSpace? space)
    {
        // Keep removable scans tied to the volume if its letter is reassigned.
        var useVolumeName = type == DriveType.Removable && WindowsVolumePaths.GetVolumeGuid(volumeName).HasValue;
        return new DriveDescriptor(useVolumeName ? volumeName! : rootPath, label, fileSystem, space)
        {
            VolumeName = volumeName,
            DriveLetterRoot = useVolumeName ? rootPath : null
        };
    }
}

/// <summary>Byte counts reported by Windows for one volume.</summary>
public readonly record struct VolumeSpace(
    long TotalBytes,
    long FreeBytes,
    long UsedBytes,
    long ReservedBytes,
    long PoolUnavailableBytes,
    bool IsVolumeWide)
{
    public long UnclassifiedBytes => TotalBytes - FreeBytes - UsedBytes - ReservedBytes - PoolUnavailableBytes;
}

/// <summary>Result of the all-folders scan of one drive.</summary>
public sealed record DriveScanResult(DriveDescriptor Drive, FolderTree? Tree, string EngineName, string Note);

/// <summary>
/// Sizes folders on local fixed and removable volumes with the fastest engine that works, in this order:
/// <list type="number">
/// <item>NTFS Master File Table when DevTools runs as administrator (measured 48 s for C:).</item>
/// <item>A low-priority folder walk (slowest: gdu, dua and diskus each took over 15 minutes for C:).</item>
/// </list>
/// Each engine has a time limit; a failed or timed-out engine falls through to the next one.
/// No external indexer is read: the scan either reads the volume's own MFT or walks the folders.
/// </summary>
public sealed class DriveUsageService
{
    private static readonly TimeSpan MftTimeout = TimeSpan.FromMinutes(5);

    private readonly MftFolderSizeScanner _mft = new();
    private readonly ParallelFolderWalker _walker = new();

    public static IReadOnlyList<DriveDescriptor> GetFixedDrives(bool includeVolumesWithoutDriveLetters = false) =>
        GetVolumes(includeVolumesWithoutDriveLetters, includeRemovable: false);

    internal static IReadOnlyList<DriveDescriptor> GetLocalVolumes() =>
        GetVolumes(includeVolumesWithoutDriveLetters: true, includeRemovable: true);

    private static IReadOnlyList<DriveDescriptor> GetVolumes(bool includeVolumesWithoutDriveLetters, bool includeRemovable)
    {
        using var errorMode = WindowsVolumePaths.SuppressCriticalErrors();
        var drives = new List<DriveDescriptor>();
        var volumeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var systemRoot = FolderTree.NormalizeRoot(Environment.SystemDirectory);
        var windows = OperatingSystem.IsWindows();
        var driveLetters = DriveInfo.GetDrives()
            .OrderByDescending(drive => string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase))
            .ThenBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var drive in driveLetters)
        {
            try
            {
                var type = drive.DriveType;
                if (!WindowsVolumePaths.IsDiskVolumeType(type, includeRemovable) ||
                    ((type == DriveType.Removable || !windows) && !drive.IsReady))
                {
                    continue;
                }

                var rootPath = FolderTree.NormalizeRoot(drive.Name);
                var volumeName = WindowsVolumePaths.GetVolumeName(rootPath);
                var stableRoot = type == DriveType.Removable && WindowsVolumePaths.GetVolumeGuid(volumeName).HasValue
                    ? volumeName! : rootPath;
                var (label, fileSystem) = windows
                    ? WindowsVolumePaths.ReadVolumeInformation(stableRoot)
                    : (drive.VolumeLabel, drive.DriveFormat);
                var space = ReadVolumeSpace(stableRoot);
                // A fixed volume with a known identity still contributes disk extents when its filesystem is unreadable.
                if (fileSystem.Length == 0 && space is null &&
                    (type == DriveType.Removable || (volumeName is null && label.Length == 0)))
                {
                    continue;
                }

                if (volumeName is null || volumeNames.Add(volumeName))
                {
                    drives.Add(DriveDescriptor.FromDriveLetter(rootPath, type, volumeName, label, fileSystem, space));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Info($"Skipping drive {drive.Name}: {ex.Message}");
            }
        }

        if (includeVolumesWithoutDriveLetters)
        {
            foreach (var (volumeName, type) in WindowsVolumePaths.GetDiskVolumeNames(includeRemovable)
                .OrderBy(volume => volume.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!volumeNames.Add(volumeName))
                {
                    continue;
                }

                var (label, fileSystem) = WindowsVolumePaths.ReadVolumeInformation(volumeName);
                var space = ReadVolumeSpace(volumeName);
                if (type == DriveType.Removable && fileSystem.Length == 0 && space is null)
                {
                    continue;
                }

                drives.Add(new DriveDescriptor(volumeName, label, fileSystem, space, HasDriveLetter: false)
                {
                    VolumeName = volumeName
                });
            }
        }

        return drives;
    }

    internal static VolumeSpace? ReadVolumeSpace(string rootPath)
    {
        using var errorMode = WindowsVolumePaths.SuppressCriticalErrors();
        return TryReadWindowsVolumeSpace(rootPath) ?? TryReadCallerVolumeSpace(rootPath);
    }

    internal static VolumeSpace? TryReadCallerVolumeSpace(string rootPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var errorMode = WindowsVolumePaths.SuppressCriticalErrors();
        // Read both quota-sensitive counters in a single call, also for volume GUID paths.
        if (!GetDiskFreeSpaceEx(rootPath, out var free, out var total, IntPtr.Zero) ||
            total > long.MaxValue || free > long.MaxValue)
        {
            return null;
        }

        return new VolumeSpace((long)total, (long)free, Math.Max(0, (long)total - (long)free),
            0, 0, IsVolumeWide: false);
    }

    private static VolumeSpace? TryReadWindowsVolumeSpace(string rootPath)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) &&
            GetDiskSpaceInformation(rootPath, out var information) == 0)
        {
            var bytesPerUnit = (ulong)information.SectorsPerAllocationUnit * information.BytesPerSector;
            if (bytesPerUnit > 0)
            {
                try
                {
                    return new VolumeSpace(
                        checked((long)(information.ActualTotalAllocationUnits * bytesPerUnit)),
                        checked((long)(information.ActualAvailableAllocationUnits * bytesPerUnit)),
                        checked((long)(information.UsedAllocationUnits * bytesPerUnit)),
                        checked((long)(information.TotalReservedAllocationUnits * bytesPerUnit)),
                        checked((long)(information.ActualPoolUnavailableAllocationUnits * bytesPerUnit)),
                        IsVolumeWide: true);
                }
                catch (OverflowException)
                {
                    AppLog.Info($"Volume byte counts for {rootPath} exceed the supported range.");
                }
            }
        }

        return null;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetDiskSpaceInformationW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetDiskSpaceInformation(string rootPath, out DiskSpaceInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string rootPath, out ulong freeToCaller, out ulong callerTotal,
        IntPtr volumeFree);


    [StructLayout(LayoutKind.Sequential)]
    private struct DiskSpaceInformation
    {
        public ulong ActualTotalAllocationUnits;
        public ulong ActualAvailableAllocationUnits;
        public ulong ActualPoolUnavailableAllocationUnits;
        public ulong CallerTotalAllocationUnits;
        public ulong CallerAvailableAllocationUnits;
        public ulong CallerPoolUnavailableAllocationUnits;
        public ulong UsedAllocationUnits;
        public ulong TotalReservedAllocationUnits;
        public ulong VolumeStorageReserveAllocationUnits;
        public ulong AvailableCommittedAllocationUnits;
        public ulong PoolAvailableAllocationUnits;
        public uint SectorsPerAllocationUnit;
        public uint BytesPerSector;
    }

    public Task<DriveScanResult> ScanDriveAsync(
        DriveDescriptor drive,
        IProgress<FolderScanProgress>? progress,
        CancellationToken cancellationToken) => ScanDriveAsync(drive, progress, cancellationToken, volumeShare: 1,
            CanTryMft(drive, OperatingSystem.IsWindows() && ElevationService.IsProcessElevated()));

    internal static bool CanTryMft(DriveDescriptor drive, bool elevated) =>
        elevated && string.Equals(drive.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase) &&
        WindowsVolumePaths.GetDevicePath(drive.RootPath) is not null;

    internal async Task<DriveScanResult> ScanDriveAsync(
        DriveDescriptor drive,
        IProgress<FolderScanProgress>? progress,
        CancellationToken cancellationToken,
        int volumeShare,
        bool tryMft,
        int[]? backingDisks = null,
        Action<FolderTree>? publishLogicalTree = null)
    {
        using var diskScope = ScanGate.ForDisks(backingDisks);
        var root = drive.RootPath;
        var notes = new List<string>();

        void Report(string engine, string message) =>
            progress?.Report(new FolderScanProgress(root, engine, message, 0, TimeSpan.Zero));

        // Try hashed persistent index cache + NTFS USN Journal incremental refresh first.
        if (TryLoadOrRefreshCachedTree(drive, tryMft, notes, cancellationToken) is { } cachedResult)
        {
            return cachedResult;
        }

        if (tryMft)
        {
            Report("NTFS MFT", $"Reading the NTFS Master File Table of {root}...");
            var usnBefore = NtfsUsnJournal.TryQueryCursor(root);
            using var mftCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            mftCts.CancelAfter(MftTimeout);
            var mftProgress = new InlineProgress<FolderSizeProgress>(update => progress?.Report(new FolderScanProgress(
                root, "NTFS MFT", $"Reading the NTFS Master File Table: {update.RecordsParsed:N0} of {update.RecordCount:N0} records.", update.RecordsParsed, update.Elapsed)));
            var tree = await TryEngineAsync(
                () => _mft.ScanTreeAsync(root, mftProgress, mftCts.Token, volumeShare),
                "NTFS MFT", root, notes, cancellationToken).ConfigureAwait(false);
            if (tree is not null)
            {
                return Done(drive, tree, notes, usnBefore);
            }
        }
        else if (string.Equals(drive.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(WindowsVolumePaths.GetDevicePath(root) is null
                ? "The NTFS Master File Table fast path needs a local volume device."
                : "Not running as administrator, so the NTFS Master File Table fast path is off.");
        }

        FolderTree? logical = null;
        if (publishLogicalTree is not null && string.Equals(drive.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            if (progress is null && Native.NativeFastEngine.IsAvailable)
            {
                logical = await TryEngineAsync(
                    () => Task.Run(() => Native.NativeFastEngine.TryWalkTreeNative(root, 0, cancellationToken)?.Tree!, cancellationToken),
                    "Rust fast walk", root, notes, cancellationToken).ConfigureAwait(false);
            }

            if (logical is null)
            {
                Report(ParallelFolderWalker.EngineName, $"Measuring logical folder sizes on {root}...");
                logical = await TryEngineAsync(
                    () => _walker.ScanVolumeAsync(root, progress, cancellationToken, volumeShare, measureAllocation: false),
                    "Logical folder walk", root, notes, cancellationToken).ConfigureAwait(false);
            }
            if (logical is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                publishLogicalTree(logical);
            }
        }

        Report(ParallelFolderWalker.EngineName, $"Measuring attributed allocation on {root}...");
        var walked = await TryEngineAsync(
            () => _walker.ScanVolumeAsync(root, progress, cancellationToken, volumeShare),
            ParallelFolderWalker.EngineName, root, notes, cancellationToken).ConfigureAwait(false);
        var result = walked ?? logical;
        return result is not null
            ? Done(drive, result, notes, usnCursor: null)
            : new DriveScanResult(drive, null, "None", string.Join(" ", notes));
    }

    /// <summary>
    /// Measures a folder subtree first by pausing background scans on its backing disks.
    /// </summary>
    public Task<FolderTree> WalkFolderAsync(string path, CancellationToken cancellationToken, bool measureAllocation = false) =>
        _walker.ScanPriorityFolderAsync(path, cancellationToken, measureAllocation);

    private static DriveScanResult? TryLoadOrRefreshCachedTree(
        DriveDescriptor drive,
        bool tryMft,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        if (!FolderIndexCache.TryLoad(drive.RootPath, out var cachedTree, out var metadata) || metadata is null)
        {
            return null;
        }

        var sizedLocal = metadata.SizedAt.ToLocalTime();
        if (tryMft && metadata.UsnJournalId != 0)
        {
            var previousCursor = new UsnJournalCursor(metadata.UsnJournalId, metadata.NextUsn, metadata.LowestValidUsn);
            var delta = NtfsUsnJournal.TryReadDelta(drive.RootPath, previousCursor, cancellationToken);
            if (delta.CanApplyIncrementally)
            {
                if (delta.ModifiedFileDirectories.Count == 0)
                {
                    clock.Stop();
                    AppLog.Info($"Drive {drive.RootPath}: verified hashed index via USN journal in {clock.ElapsedMilliseconds} ms ({cachedTree.FolderCount:N0} folders, hash={metadata.TreeHash:X16}). {delta.Reason}");
                    notes.Add($"Verified index from {sizedLocal:yyyy-MM-dd HH:mm} ({delta.Reason})");
                    if (delta.NewNextUsn > metadata.NextUsn)
                    {
                        FolderIndexCache.SaveInBackground(drive.RootPath, cachedTree,
                            new UsnJournalCursor(metadata.UsnJournalId, delta.NewNextUsn, metadata.LowestValidUsn));
                    }

                    return RefreshDriveSpace(drive, cachedTree, notes);
                }

                var updates = new List<(int Node, long NewDirectBytes, long NewDirectFiles)>(delta.ModifiedFileDirectories.Count);
                var allResolvedInTree = true;
                foreach (var dirPath in delta.ModifiedFileDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var node = cachedTree.Find(dirPath);
                    if (node < 0)
                    {
                        allResolvedInTree = false;
                        break;
                    }

                    if (!TryMeasureDirectFiles(dirPath, out var directBytes, out var directFiles))
                    {
                        allResolvedInTree = false;
                        break;
                    }

                    updates.Add((node, directBytes, directFiles));
                }

                if (allResolvedInTree)
                {
                    clock.Stop();
                    var updated = cachedTree.ApplyDirectFileUpdates(updates, "NTFS MFT (USN incremental)", clock.Elapsed);
                    AppLog.Info($"Drive {drive.RootPath}: incrementally updated {updates.Count} changed folder(s) via USN journal in {clock.ElapsedMilliseconds} ms.");
                    notes.Add($"Incremental USN update ({updates.Count} changed folder(s) in {clock.ElapsedMilliseconds} ms).");
                    FolderIndexCache.SaveInBackground(drive.RootPath, updated,
                        new UsnJournalCursor(metadata.UsnJournalId, delta.NewNextUsn, metadata.LowestValidUsn));
                    return RefreshDriveSpace(drive, updated, notes);
                }
            }
            else
            {
                AppLog.Info($"Drive {drive.RootPath}: USN incremental refresh skipped ({delta.Reason}); running fresh scan.");
                return null;
            }
        }

        clock.Stop();
        AppLog.Info($"Drive {drive.RootPath}: loaded SHA-256 verified folder index from {sizedLocal:yyyy-MM-dd HH:mm} in {clock.ElapsedMilliseconds} ms ({cachedTree.FolderCount:N0} folders, hash={metadata.TreeHash:X16}).");
        notes.Add($"Loaded verified index from {sizedLocal:yyyy-MM-dd HH:mm} ({cachedTree.FolderCount:N0} folders).");
        return RefreshDriveSpace(drive, cachedTree, notes);
    }

    private static bool TryMeasureDirectFiles(string directoryPath, out long directBytes, out long directFiles)
    {
        directBytes = 0;
        directFiles = 0;
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = false,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0
            };
            var entries = new System.IO.Enumeration.FileSystemEnumerable<(bool IsDirectory, long Length)>(
                directoryPath,
                (ref System.IO.Enumeration.FileSystemEntry entry) => (entry.IsDirectory, entry.Length),
                options);
            foreach (var entry in entries)
            {
                if (!entry.IsDirectory)
                {
                    directBytes += Math.Max(0, entry.Length);
                    directFiles++;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DriveScanResult RefreshDriveSpace(DriveDescriptor drive, FolderTree tree, List<string> notes)
    {
        if (ReadVolumeSpace(drive.RootPath) is { } refreshed)
        {
            drive = drive with { Space = refreshed };
        }
        else if (drive.Space.HasValue)
        {
            notes.Add("Volume totals are from before the folder scan.");
        }

        return new DriveScanResult(drive, tree, tree.EngineName, string.Join(" ", notes));
    }

    private static DriveScanResult Done(DriveDescriptor drive, FolderTree tree, List<string> notes, UsnJournalCursor? usnCursor = null)
    {
        AppLog.Info($"Drive {drive.RootPath}: {tree.EngineName} sized {tree.FolderCount:N0} folders ({ByteFormatter.Format(tree.TotalBytes)} in {tree.TotalFiles:N0} files) in {tree.Elapsed.TotalSeconds:F1}s.");
        FolderIndexCache.SaveInBackground(drive.RootPath, tree, usnCursor);
        return RefreshDriveSpace(drive, tree, notes);
    }

    private static async Task<FolderTree?> TryEngineAsync(
        Func<Task<FolderTree>> run,
        string engineName,
        string root,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException ? "timed out" : ex.Message;
            AppLog.Error($"Drive {root}: {engineName} failed ({message}); trying the next engine.", ex);
            notes.Add($"{engineName} failed: {message}.");
            return null;
        }
    }

    /// <summary>Progress that calls straight through (no synchronization context capture).</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}