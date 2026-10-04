using DevTools.Infrastructure;
using DevTools.Services.BiggestFiles;

namespace DevTools.Services.FolderSizes;

/// <summary>One fixed drive as the scan sees it.</summary>
public sealed record DriveDescriptor(string RootPath, string Label, string FileSystem, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);
}

/// <summary>Result of the all-folders scan of one drive.</summary>
public sealed record DriveScanResult(DriveDescriptor Drive, FolderTree? Tree, string EngineName, string Note);

/// <summary>
/// Sizes every folder on every fixed drive with the fastest engine that works, in this order:
/// <list type="number">
/// <item>Everything with folder sizes indexed (one short list of folders, no disk reads).</item>
/// <item>NTFS Master File Table when DevTools runs as administrator (measured 48 s for C:).</item>
/// <item>Everything without folder sizes (every file from the index: 81 s for C: with free RAM, 309 s when RAM was nearly full).</item>
/// <item>A low-priority folder walk (slowest: gdu, dua and diskus each took over 15 minutes for C:).</item>
/// </list>
/// Each engine has a time limit; a failed or timed-out engine falls through to the next one.
/// </summary>
public sealed class DriveUsageService
{
    private static readonly TimeSpan EverythingFoldersTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MftTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EverythingFilesTimeout = TimeSpan.FromMinutes(10);

    private readonly EverythingFolderScanner _everything = new();
    private readonly MftFolderSizeScanner _mft = new();
    private readonly ParallelFolderWalker _walker = new();

    public static IReadOnlyList<DriveDescriptor> GetFixedDrives()
    {
        var drives = new List<DriveDescriptor>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                drives.Add(new DriveDescriptor(
                    FolderTree.NormalizeRoot(drive.RootDirectory.FullName),
                    drive.VolumeLabel,
                    drive.DriveFormat,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Info($"Skipping drive {drive.Name}: {ex.Message}");
            }
        }

        // System drive first, then by letter.
        var systemRoot = FolderTree.NormalizeRoot(Environment.SystemDirectory);
        return drives
            .OrderByDescending(drive => string.Equals(drive.RootPath, systemRoot, StringComparison.OrdinalIgnoreCase))
            .ThenBy(drive => drive.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<DriveScanResult> ScanDriveAsync(
        DriveDescriptor drive,
        IProgress<FolderScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = drive.RootPath;
        var notes = new List<string>();

        void Report(string engine, string message) =>
            progress?.Report(new FolderScanProgress(root, engine, message, 0, TimeSpan.Zero));

        Report(EverythingFolderScanner.EngineName, $"Checking Everything for {root}...");
        EverythingProbe probe;
        try
        {
            probe = await _everything.ProbeAsync(root, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            probe = new EverythingProbe(false, false, false, null, $"Everything check failed: {ex.Message}");
        }

        AppLog.Info($"Drive {root}: {probe.Reason}");
        notes.Add(probe.Reason);

        if (probe.IndexesDrive && probe.HasFolderSizes)
        {
            var tree = await TryEngineAsync(
                () => _everything.ScanAsync(root, probe, progress, EverythingFoldersTimeout, cancellationToken),
                EverythingFolderScanner.EngineName, root, notes, cancellationToken).ConfigureAwait(false);
            if (tree is not null)
            {
                return Done(drive, tree, notes);
            }
        }

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

        if (probe.IndexesDrive && !probe.HasFolderSizes)
        {
            var tree = await TryEngineAsync(
                () => _everything.ScanAsync(root, probe, progress, EverythingFilesTimeout, cancellationToken),
                EverythingFolderScanner.EngineName, root, notes, cancellationToken).ConfigureAwait(false);
            if (tree is not null)
            {
                return Done(drive, tree, notes);
            }
        }

        Report(ParallelFolderWalker.EngineName, $"Walking every folder on {root} (slow path)...");
        var walked = await TryEngineAsync(
            () => _walker.ScanAsync(root, progress, cancellationToken),
            ParallelFolderWalker.EngineName, root, notes, cancellationToken).ConfigureAwait(false);
        return walked is not null
            ? Done(drive, walked, notes)
            : new DriveScanResult(drive, null, "None", string.Join(" ", notes));
    }

    /// <summary>Measures one folder subtree (used when a folder is not in any drive tree).</summary>
    public Task<FolderTree> WalkFolderAsync(string path, CancellationToken cancellationToken)
    {
        return _walker.ScanAsync(path, null, cancellationToken);
    }

    private static DriveScanResult Done(DriveDescriptor drive, FolderTree tree, List<string> notes)
    {
        AppLog.Info($"Drive {drive.RootPath}: {tree.EngineName} sized {tree.FolderCount:N0} folders ({ByteFormatter.Format(tree.TotalBytes)} in {tree.TotalFiles:N0} files) in {tree.Elapsed.TotalSeconds:F1}s.");
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
