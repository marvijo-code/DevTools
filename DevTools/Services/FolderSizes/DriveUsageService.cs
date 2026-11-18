using System.Runtime.InteropServices;
using DevTools.Infrastructure;
using DevTools.Services.BiggestFiles;

namespace DevTools.Services.FolderSizes;

/// <summary>One fixed drive as the scan sees it.</summary>
public sealed record DriveDescriptor(string RootPath, string Label, string FileSystem, VolumeSpace? Space, bool HasDriveLetter = true)
{
    internal string? VolumeName { get; init; }
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
/// Sizes every folder on every fixed drive with the fastest engine that works, in this order:
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

    public static IReadOnlyList<DriveDescriptor> GetFixedDrives(bool includeVolumesWithoutDriveLetters = false)
    {
        var drives = new List<DriveDescriptor>();
        var volumeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var systemRoot = FolderTree.NormalizeRoot(Environment.SystemDirectory);
        var driveLetters = DriveInfo.GetDrives()
            .OrderByDescending(drive => string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase))
            .ThenBy(drive => drive.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var drive in driveLetters)
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                var rootPath = FolderTree.NormalizeRoot(drive.RootDirectory.FullName);
                var volumeName = WindowsVolumePaths.GetVolumeName(rootPath);
                var descriptor = new DriveDescriptor(rootPath, drive.VolumeLabel, drive.DriveFormat, ReadVolumeSpace(drive))
                {
                    VolumeName = volumeName
                };
                if (volumeName is null || volumeNames.Add(volumeName))
                {
                    drives.Add(descriptor);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Info($"Skipping drive {drive.Name}: {ex.Message}");
            }
        }

        if (includeVolumesWithoutDriveLetters)
        {
            foreach (var volumeName in WindowsVolumePaths.GetFixedVolumeNames().Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!volumeNames.Add(volumeName))
                {
                    continue;
                }

                var (label, fileSystem) = WindowsVolumePaths.ReadVolumeInformation(volumeName);
                drives.Add(new DriveDescriptor(volumeName, label, fileSystem,
                    TryReadWindowsVolumeSpace(volumeName), HasDriveLetter: false)
                {
                    VolumeName = volumeName
                });
            }
        }

        return drives;
    }

    internal static VolumeSpace ReadVolumeSpace(DriveInfo drive)
    {
        if (TryReadWindowsVolumeSpace(drive.RootDirectory.FullName) is { } space)
        {
            return space;
        }

        // DriveInfo.TotalSize and AvailableFreeSpace can both reflect a caller quota.
        var total = drive.TotalSize;
        var free = drive.AvailableFreeSpace;
        return new VolumeSpace(total, free, Math.Max(0, total - free), 0, 0, IsVolumeWide: false);
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
        CancellationToken cancellationToken) => ScanDriveAsync(drive, progress, cancellationToken, concurrentWalks: 1);

    internal async Task<DriveScanResult> ScanDriveAsync(
        DriveDescriptor drive,
        IProgress<FolderScanProgress>? progress,
        CancellationToken cancellationToken,
        int concurrentWalks)
    {
        var root = drive.RootPath;
        var notes = new List<string>();

        void Report(string engine, string message) =>
            progress?.Report(new FolderScanProgress(root, engine, message, 0, TimeSpan.Zero));

        var elevated = ElevationService.IsProcessElevated();
        var ntfs = string.Equals(drive.FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);
        if (elevated && ntfs)
        {
            Report("NTFS MFT", $"Reading the NTFS Master File Table of {root}...");
            using var mftCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            mftCts.CancelAfter(MftTimeout);
            var mftProgress = new InlineProgress<FolderSizeProgress>(update => progress?.Report(new FolderScanProgress(
                root, "NTFS MFT", $"Reading the NTFS Master File Table: {update.RecordsParsed:N0} of {update.RecordCount:N0} records.", update.RecordsParsed, update.Elapsed)));
            var tree = await TryEngineAsync(
                () => _mft.ScanTreeAsync(root, mftProgress, mftCts.Token),
                "NTFS MFT", root, notes, cancellationToken).ConfigureAwait(false);
            if (tree is not null)
            {
                return Done(drive, tree, notes);
            }
        }
        else if (ntfs)
        {
            notes.Add("Not running as administrator, so the NTFS Master File Table fast path is off.");
        }

        Report(ParallelFolderWalker.EngineName, $"Walking every folder on {root} (slow path)...");
        var walked = await TryEngineAsync(
            () => _walker.ScanVolumeAsync(root, progress, cancellationToken, concurrentWalks),
            ParallelFolderWalker.EngineName, root, notes, cancellationToken).ConfigureAwait(false);
        return walked is not null
            ? Done(drive, walked, notes)
            : new DriveScanResult(drive, null, "None", string.Join(" ", notes));
    }

    /// <summary>
    /// Measures one folder subtree the user asked for (a click). It runs at full speed while the
    /// background all-drive scans pause, so the clicked folder is sized first.
    /// </summary>
    public async Task<FolderTree> WalkFolderAsync(string path, CancellationToken cancellationToken, bool measureAllocation = false)
    {
        using (ScanGate.Pause())
        {
            return await _walker.ScanAsync(path, null, cancellationToken, prioritize: true, measureAllocation: measureAllocation)
                .ConfigureAwait(false);
        }
    }

    private static DriveScanResult Done(DriveDescriptor drive, FolderTree tree, List<string> notes)
    {
        AppLog.Info($"Drive {drive.RootPath}: {tree.EngineName} sized {tree.FolderCount:N0} folders ({ByteFormatter.Format(tree.TotalBytes)} in {tree.TotalFiles:N0} files) in {tree.Elapsed.TotalSeconds:F1}s.");
        try
        {
            var space = drive.HasDriveLetter
                ? ReadVolumeSpace(new DriveInfo(drive.RootPath))
                : TryReadWindowsVolumeSpace(drive.RootPath);
            if (space is { } refreshed)
            {
                drive = drive with { Space = refreshed };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add("Volume totals are from before the folder scan.");
        }

        return new DriveScanResult(drive, tree, tree.EngineName, string.Join(" ", notes));
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