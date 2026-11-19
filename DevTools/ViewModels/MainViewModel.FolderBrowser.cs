using System.Collections.ObjectModel;
using System.Diagnostics;
using DevTools.Infrastructure;
using DevTools.Services;
using DevTools.Services.FolderSizes;

namespace DevTools.ViewModels;

public sealed record FolderBrowserDrive(string Path, string Label);

public sealed record FolderBrowserLiveSize(string Name, string Path, string SizeText);

/// <summary>One row of the folder browser: a folder, a file, or the tail of smaller files in a folder.</summary>
public sealed class FolderBrowserRow
{
    private const double IndentPerLevel = 18;

    public FolderBrowserRow(
        string name,
        string path,
        long? sizeBytes,
        string detail,
        double sharePercent,
        int depth,
        bool canOpen,
        bool canExpand,
        bool isExpanded,
        bool isFile,
        long? scannedBytes = null)
    {
        Name = name;
        Path = path;
        SizeBytes = sizeBytes;
        ScannedBytes = sizeBytes.HasValue ? null : scannedBytes;
        SizeText = (sizeBytes ?? ScannedBytes) is long bytes ? ByteFormatter.Format(bytes) : "—";
        DetailText = detail;
        SharePercent = sharePercent;
        Depth = depth;
        Indent = new Microsoft.UI.Xaml.Thickness(depth * IndentPerLevel, 0, 0, 0);
        CanOpen = canOpen;
        CanExpand = canExpand;
        IsExpanded = isExpanded;
        IsFile = isFile;
        ExpandGlyph = canExpand ? (isExpanded ? "" : "") : string.Empty;
    }

    public string Name { get; }

    public string Path { get; }

    public long? SizeBytes { get; }

    public long? ScannedBytes { get; }

    public bool HasScannedSize => ScannedBytes.HasValue;

    public bool HasMeasuredShare => SizeBytes.HasValue && SharePercent > 0;

    public string SizeText { get; }

    public string DetailText { get; }

    public double SharePercent { get; }

    public int Depth { get; }

    public Microsoft.UI.Xaml.Thickness Indent { get; }

    /// <summary>True when clicking the name navigates into the folder.</summary>
    public bool CanOpen { get; }

    /// <summary>True when the row is a folder with contents that can be expanded in place.</summary>
    public bool CanExpand { get; }

    public bool IsExpanded { get; }

    public bool IsFile { get; }

    /// <summary>Segoe Fluent chevron: down when expanded, right when collapsed, empty when not expandable.</summary>
    public string ExpandGlyph { get; }

    public Microsoft.UI.Xaml.Visibility ExpandVisibility => CanExpand
        ? Microsoft.UI.Xaml.Visibility.Visible
        : Microsoft.UI.Xaml.Visibility.Collapsed;

    internal bool HasSamePresentation(FolderBrowserRow other) =>
        SizeBytes == other.SizeBytes && ScannedBytes == other.ScannedBytes && SharePercent == other.SharePercent && Depth == other.Depth &&
        CanOpen == other.CanOpen && CanExpand == other.CanExpand && IsExpanded == other.IsExpanded &&
        IsFile == other.IsFile && Name == other.Name && Path == other.Path &&
        SizeText == other.SizeText && DetailText == other.DetailText;
}

public sealed partial class MainViewModel
{
    private const int FolderBrowserMaxRows = 200;
    private const int FolderBrowserLiveSizeLimit = 4;
    private const int FolderBrowserMaxFiles = 100;
    private const int FolderBrowserPreviewSizeLimit = 48;
    private const int FolderBrowserPreviewDirectoryLimit = 8;
    private const int FolderBrowserPreviewEntryLimit = 2048;
    private const int FolderBrowserRecentListingLimit = 32;
    private const int FolderBrowserWideLayoutLimit = 32;

    private readonly ObservableCollection<FolderBrowserRow> _folderBrowserRows = [];
    private readonly ObservableCollection<FolderBrowserDrive> _folderBrowserDrives = [];
    private readonly HashSet<string> _folderBrowserExpanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _folderBrowserCollapsed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompletedFolderSize> _folderBrowserCompletedSizes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParallelFolderWalker.LogicalFolderWalk> _folderBrowserLiveWalks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _folderBrowserQuickSizes = new(StringComparer.Ordinal);
    private readonly Dictionary<int, FolderFileSnapshot?> _folderBrowserFiles = [];
    private readonly HashSet<int> _folderBrowserFilesLoading = [];
    private readonly OrderedDictionary<int, FolderFileSnapshot> _recentFolderBrowserFiles = new();
    private OrderedDictionary<(int Node, bool Allocation), FolderBrowserLayout>? _folderBrowserLayouts;
    private FolderTree? _folderBrowserFileTree;
    private FolderPreview? _folderBrowserPreview;
    private long _folderBrowserScannedRootBytes;
    private IReadOnlyList<FolderBrowserLiveSize> _folderBrowserLiveSizes = [];
    private string _folderBrowserPath = string.Empty;
    private string _folderBrowserSummary = "Discovering local drives.";
    private bool _showFolderBrowserVolumeSummary;
    private bool _canFolderBrowserGoUp;
    private bool _folderBrowserUseAllocation;
    private bool _folderBrowserPreviewUsesAllocation;
    private CancellationTokenSource? _folderBrowserFileLoadCts;
    private CancellationTokenSource? _folderBrowserClickCts;


    /// <summary>Folders can be browsed before measurement. Measured rows are sorted by size.</summary>
    public ObservableCollection<FolderBrowserRow> FolderBrowserRows => _folderBrowserRows;

    /// <summary>Drive roots available in the folder browser.</summary>
    public ObservableCollection<FolderBrowserDrive> FolderBrowserDrives => _folderBrowserDrives;

    public IReadOnlyList<FolderBrowserLiveSize> FolderBrowserLiveSizes => _folderBrowserLiveSizes;

    public bool HasLiveFolderSizes => _folderBrowserLiveSizes.Count > 0;

    public string FolderBrowserPath => _folderBrowserPath;

    public string FolderBrowserSummary
    {
        get
        {
            var summary = GetSelectedVolumeNote() ?? _folderBrowserSummary;
            return _folderBrowserScannedRootBytes > 0 && !FolderBrowserUseAllocation
                ? $"{summary} {ByteFormatter.Format(_folderBrowserScannedRootBytes)} scanned."
                : summary;
        }
    }

    private string? GetSelectedVolumeNote()
    {
        if (!_showFolderBrowserVolumeSummary || string.IsNullOrEmpty(_folderBrowserPath))
        {
            return null;
        }

        var index = FindDiskUsageSectionIndex(DiskUsageAnalyzer.DriveSummarySectionTitle);
        return index < 0 ? null : _diskUsageSections[index].Entries.FirstOrDefault(entry =>
            string.Equals(entry.Path, _folderBrowserPath, StringComparison.OrdinalIgnoreCase))?.Note;
    }

    public bool CanFolderBrowserGoUp => _canFolderBrowserGoUp;

    public bool FolderBrowserUseAllocation
    {
        get => _folderBrowserUseAllocation;
        set
        {
            if (!SetProperty(ref _folderBrowserUseAllocation, value))
            {
                return;
            }

            _ = BrowseFolderAsync(_folderBrowserPath);
        }
    }

    /// <summary>Opens <paramref name="path"/> in the browser, with nothing expanded.</summary>
    public bool BrowseFolder(string? path) => BrowseFolder(path, skipUnchanged: false);

    private bool BrowseFolder(string? path, bool skipUnchanged)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !_diskUsageAnalyzer.TryFindNode(path, FolderBrowserUseAllocation, out var tree, out var node) || tree is null ||
            (FolderBrowserUseAllocation && !tree.GetAllocatedBytes(node).HasValue))
        {
            return false;
        }

        ShowFolderBrowserLocation(tree, node, skipUnchanged);
        return true;
    }

    private void ShowFolderBrowserLocation(FolderTree tree, int node, bool skipUnchanged)
    {
        var normalized = tree.GetPath(node);
        if (!string.Equals(normalized, _folderBrowserPath, StringComparison.Ordinal))
        {
            _folderBrowserExpanded.Clear();
            CancelFolderBrowserFileLoads();
        }
        else if (skipUnchanged && ReferenceEquals(tree, _folderBrowserFileTree))
        {
            return;
        }

        ClearLiveFolderSizes();
        _folderBrowserCollapsed.Clear();
        _folderBrowserPreview = null;
        _folderBrowserQuickSizes.Clear();
        _folderBrowserScannedRootBytes = 0;
        _folderBrowserPath = normalized;
        _canFolderBrowserGoUp = node != 0;
        _showFolderBrowserVolumeSummary = node == 0;
        var logicalComplete = HasExactSize(tree, node, showAllocation: false);
        var allocation = HasExactSize(tree, node, showAllocation: true) && tree.GetAllocatedBytes(node) is long allocatedBytes
            ? $" {ByteFormatter.Format(allocatedBytes)} of attributed allocation."
            : FolderBrowserUseAllocation ? " Exact allocation is unavailable for this scan." : string.Empty;
        var coverage = string.Empty;
        if (tree.HasIncompleteAllocation(node))
        {
            coverage += " Allocation could not be fully measured.";
        }
        if (node == 0)
        {
            coverage += DiskUsageAnalyzer.DescribeIncompleteMftAllocation(tree);
        }

        var logicalSize = logicalComplete
            ? $"{ByteFormatter.Format(tree.GetTotalBytes(node))} of logical file size in {tree.GetFileCount(node):N0} files."
            : $"Scanned {ByteFormatter.Format(tree.GetTotalBytes(node))}. Some folder listings could not be read.";
        _folderBrowserSummary = $"{logicalSize}{allocation}{coverage} Sized by {tree.EngineName} in {tree.Elapsed.TotalSeconds:F1}s.";
        RaisePropertyChanged(nameof(FolderBrowserPath));
        RaisePropertyChanged(nameof(FolderBrowserSummary));
        RaisePropertyChanged(nameof(CanFolderBrowserGoUp));

        RebuildFolderBrowserRows(tree, node);
    }

    /// <summary>
    /// Opens a folder, sizing it right now if it or its requested allocation is missing.
    /// A newer click cancels the previous one.
    /// </summary>
    public async Task BrowseFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _folderBrowserClickCts?.Cancel();
        var measureAllocation = FolderBrowserUseAllocation;
        if (_diskUsageAnalyzer.TryFindNode(path, measureAllocation, out var tree, out var node) && tree is not null)
        {
            if (!measureAllocation || tree.GetAllocatedBytes(node).HasValue)
            {
                ShowFolderBrowserLocation(tree, node, skipUnchanged: false);
                return;
            }

            if (node == 0)
            {
                // The drive scan already attempted allocation for this volume.
                ShowPendingBrowserDrive(path, scanCompleted: true);
                return;
            }
        }

        if (IsDiskUsageRefreshing && _folderBrowserDrives.Any(drive => path.Equals(drive.Path, StringComparison.OrdinalIgnoreCase)))
        {
            ShowPendingBrowserDrive(path);
            return;
        }

        var cts = _folderBrowserClickCts = new CancellationTokenSource();
        var status = $"Sizing {path} first.";
        ShowFolderBrowserPreview(path, status, showVolumeSummary: false);

        try
        {
            var token = cts.Token;
            var sized = await Task.Run(() => _diskUsageAnalyzer.SizeFolderNowAsync(path, token, measureAllocation), token);
            if (!cts.IsCancellationRequested && (!sized || !BrowseFolder(path)))
            {
                _showFolderBrowserVolumeSummary = false;
                _folderBrowserSummary = $"Folder sizes for {path} are unavailable.";
                RaisePropertyChanged(nameof(FolderBrowserSummary));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error($"Sizing clicked folder '{path}' failed.", ex);
            if (!cts.IsCancellationRequested)
            {
                _showFolderBrowserVolumeSummary = false;
                _folderBrowserSummary = $"Could not size {path}: {ex.Message}";
                RaisePropertyChanged(nameof(FolderBrowserSummary));
            }
        }
        finally
        {
            if (ReferenceEquals(_folderBrowserClickCts, cts))
            {
                _folderBrowserClickCts = null;
            }

            cts.Dispose();
        }
    }

    public void BrowseFolderUp()
    {
        if (!_canFolderBrowserGoUp)
        {
            return;
        }

        var parent = _diskUsageAnalyzer.TryFindNode(_folderBrowserPath, FolderBrowserUseAllocation, out var tree, out var node) &&
            tree is not null && node != 0
            ? tree.GetPath(tree.GetParent(node))
            : GetFolderBrowserParentPath(_folderBrowserPath);
        if (!string.IsNullOrEmpty(parent))
        {
            _ = BrowseFolderAsync(parent);
        }
    }

    /// <summary>Expands or collapses a folder row in place.</summary>
    public void ToggleFolderRow(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_folderBrowserFileTree is null)
        {
            var row = _folderBrowserRows.FirstOrDefault(candidate => candidate.Path == path && candidate.CanExpand);
            if (row is null)
            {
                return;
            }

            if (row.IsExpanded)
            {
                _folderBrowserExpanded.Remove(path);
                _folderBrowserCollapsed.Add(path);
            }
            else
            {
                _folderBrowserCollapsed.Remove(path);
                _folderBrowserExpanded.Add(path);
            }

            RebuildFolderBrowserPreview();
            return;
        }

        if (!_folderBrowserExpanded.Remove(path))
        {
            _folderBrowserExpanded.Add(path);
        }

        RebuildFolderBrowserRows();
    }

    private void RefreshFolderBrowserDrives(IReadOnlyList<string> roots, string? completedRoot = null, bool scanCompleted = false)
    {
        var selectedRoot = Path.GetPathRoot(_folderBrowserPath);
        if (completedRoot is not null && !string.Equals(selectedRoot, completedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var drives = roots.Select(root => new FolderBrowserDrive(root, _diskUsageAnalyzer.GetFolderBrowserDriveLabel(root)));
        if (!drives.SequenceEqual(_folderBrowserDrives))
        {
            _folderBrowserDrives.Clear();
            foreach (var drive in drives)
            {
                _folderBrowserDrives.Add(drive);
            }
        }

        if (roots.Count == 0)
        {
            _folderBrowserClickCts?.Cancel();
            ShowPendingBrowserDrive(string.Empty, scanCompleted);
            return;
        }

        var keepLocation = selectedRoot is not null && roots.Contains(selectedRoot, StringComparer.OrdinalIgnoreCase);
        if (!keepLocation)
        {
            _folderBrowserClickCts?.Cancel();
        }

        var path = keepLocation ? _folderBrowserPath : roots[0];
        var driveScanCompleted = scanCompleted || completedRoot is not null;
        var isDriveRoot = string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase);
        if (!driveScanCompleted && isDriveRoot)
        {
            ShowPendingBrowserDrive(path);
            return;
        }

        if (BrowseFolder(path, skipUnchanged: true))
        {
            return;
        }

        if (isDriveRoot)
        {
            ShowPendingBrowserDrive(path, driveScanCompleted);
            return;
        }

        var status = _folderBrowserClickCts is { IsCancellationRequested: false } ? _folderBrowserSummary :
            driveScanCompleted ? $"Folder sizes for {path} are unavailable." : $"Waiting for folder sizes in {path}.";
        ShowFolderBrowserPreview(path, status, showVolumeSummary: false);
    }

    private void ShowPendingBrowserDrive(string path, bool scanCompleted = false)
    {
        var status = string.IsNullOrEmpty(path)
            ? "No local volumes are available."
            : scanCompleted
                ? $"Folder sizes for {path} are unavailable."
                : $"Waiting for the drive scan of {path}.";
        ShowFolderBrowserPreview(path, status, showVolumeSummary: true);
    }

    private static string? GetFolderBrowserParentPath(string path) =>
        Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));

    private void ShowFolderBrowserPreview(string path, string status, bool showVolumeSummary)
    {
        var useAllocation = FolderBrowserUseAllocation;
        var keepPreview = _folderBrowserFileTree is null && _folderBrowserFileLoadCts is not null &&
            _folderBrowserPreviewUsesAllocation == useAllocation && string.Equals(path, _folderBrowserPath, StringComparison.Ordinal);
        if (!keepPreview)
        {
            ClearLiveFolderSizes();
            _folderBrowserCollapsed.Clear();
            _folderBrowserPreview = null;
            _folderBrowserQuickSizes.Clear();
            _folderBrowserScannedRootBytes = 0;
            _folderBrowserExpanded.Clear();
            ResetFolderBrowserCaches(null);
            if (_folderBrowserRows.Count > 0)
            {
                _folderBrowserRows.Clear();
            }
        }

        _folderBrowserPath = path;
        _canFolderBrowserGoUp = !showVolumeSummary && !string.IsNullOrEmpty(GetFolderBrowserParentPath(path));
        _showFolderBrowserVolumeSummary = showVolumeSummary;
        _folderBrowserSummary = status;
        RaisePropertyChanged(nameof(FolderBrowserPath));
        RaisePropertyChanged(nameof(FolderBrowserSummary));
        RaisePropertyChanged(nameof(CanFolderBrowserGoUp));

        if (!keepPreview && path.Length > 0)
        {
            _folderBrowserPreviewUsesAllocation = useAllocation;
            _folderBrowserFileLoadCts = new CancellationTokenSource();
            _ = LoadFolderBrowserPreviewAsync(path, useAllocation, _folderBrowserFileLoadCts.Token);
        }
    }

    private async Task LoadFolderBrowserPreviewAsync(string path, bool useAllocation, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var preview = await Task.Run(
                () => FolderFileListing.ReadChildFolders(path, FolderBrowserMaxRows, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (preview is null)
            {
                return;
            }

            _folderBrowserPreview = preview;
            RebuildFolderBrowserPreview();
            if (!useAllocation && preview.FolderNames.Count > 0)
            {
                await LoadFolderPreviewSizesAsync(path, preview.FolderNames, started, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error($"Loading the folder preview for '{path}' failed.", ex);
        }
    }

    private Task LoadFolderPreviewSizesAsync(string parentPath, IReadOnlyList<string> names, long started,
        CancellationToken cancellationToken)
    {
        var firstNonzeroSize = true;
        IProgress<(string Path, long Bytes)> progress = new Progress<(string Path, long Bytes)>(value =>
        {
            if (cancellationToken.IsCancellationRequested || _folderBrowserFileTree is not null || FolderBrowserUseAllocation ||
                _folderBrowserRows.FirstOrDefault(row => row.Path == value.Path)?.SizeBytes.HasValue != false)
            {
                return;
            }

            _folderBrowserQuickSizes[value.Path] = value.Bytes;
            RebuildFolderBrowserPreview();
            if (firstNonzeroSize && value.Bytes > 0)
            {
                firstNonzeroSize = false;
                AppLog.Info($"Folder Browser first nonzero preview size shown after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
            }
        });

        return Task.Run(() =>
        {
            var count = Math.Min(names.Count, FolderBrowserPreviewSizeLimit);
            for (var index = 0; index < count; index++)
            {
                var path = Path.Join(parentPath, names[index]);
                var size = FolderFileListing.ReadExactPreviewSize(path, FolderBrowserPreviewDirectoryLimit,
                    FolderBrowserPreviewEntryLimit, cancellationToken);
                if (size is { } measured)
                {
                    progress.Report((path, measured));
                }
            }
        }, cancellationToken);
    }

    private void RebuildFolderBrowserPreview(IReadOnlyList<FolderBrowserLiveSize>? recent = null)
    {
        if (_folderBrowserPreview is not { } preview)
        {
            return;
        }

        var driveRoot = Path.GetPathRoot(_folderBrowserPath);
        var live = !FolderBrowserUseAllocation && driveRoot is not null &&
            _folderBrowserLiveWalks.TryGetValue(driveRoot, out var walk)
            ? walk.Capture(_folderBrowserPath, preview.FolderNames, _folderBrowserExpanded,
                (recent ?? _folderBrowserLiveSizes).Select(folder => folder.Path).ToArray(),
                _folderBrowserCollapsed, FolderBrowserMaxRows)
            : null;

        var rows = new List<FolderBrowserRow>(live?.Rows.Count ?? preview.FolderNames.Count);
        if (live is not null)
        {
            foreach (var folder in live.Rows)
            {
                rows.Add(CreatePreviewFolderRow(folder.Name, folder.Path, folder));
            }
        }
        else
        {
            foreach (var name in preview.FolderNames)
            {
                rows.Add(CreatePreviewFolderRow(name, Path.Join(_folderBrowserPath, name)));
            }
        }

        if (preview.HasMore)
        {
            rows.Add(new FolderBrowserRow("Additional folders", _folderBrowserPath, null,
                $"Preview limited to {FolderBrowserMaxRows:N0} folders", 0, 0,
                canOpen: false, canExpand: false, isExpanded: false, isFile: false));
        }

        UpdateFolderBrowserRows(rows);
        SetProperty(ref _folderBrowserScannedRootBytes, live?.ScannedBytes ?? 0, nameof(FolderBrowserSummary));
    }

    private FolderBrowserRow CreatePreviewFolderRow(string name, string path, LiveFolderRow? live = null)
    {
        long? bytes = null;
        var settled = live is { IsSettled: true };
        if (live is { HasExactSize: true } measured)
        {
            bytes = measured.ScannedBytes;
        }
        else if (live is null && _folderBrowserCompletedSizes.TryGetValue(path, out var completed) &&
                 (!FolderBrowserUseAllocation || completed.AllocationAttempted))
        {
            settled = true;
            bytes = FolderBrowserUseAllocation ? completed.AllocatedBytes : completed.LogicalBytes;
        }
        else if (!settled && !FolderBrowserUseAllocation && _folderBrowserQuickSizes.TryGetValue(path, out var previewBytes))
        {
            bytes = previewBytes;
        }

        long? scannedBytes = live is { ScannedBytes: > 0 } running ? running.ScannedBytes : null;
        var detail = settled
            ? bytes.HasValue ? "Verified by drive scan" : "Exact size unavailable"
            : bytes.HasValue ? "Quick scan complete"
            : scannedBytes.HasValue ? "Scanning folder contents" : "Waiting for exact size";
        return new FolderBrowserRow(name, path, bytes, detail, 0, live?.Depth ?? 0, canOpen: true,
            canExpand: live?.CanExpand ?? false, isExpanded: live?.IsExpanded ?? false, isFile: false, scannedBytes);
    }

    private bool TryShowLiveFolderSizes(IReadOnlyList<LiveFolderSize> subfolders)
    {
        if (subfolders.Count == 0 || FolderBrowserUseAllocation || _folderBrowserFileTree is not null ||
            _folderBrowserPreview is null || _folderBrowserPath.Length == 0)
        {
            return false;
        }

        var prefix = Path.EndsInDirectorySeparator(_folderBrowserPath)
            ? _folderBrowserPath : _folderBrowserPath + Path.DirectorySeparatorChar;
        var rows = new List<FolderBrowserLiveSize>(FolderBrowserLiveSizeLimit);
        long latestBytes = 0;
        for (var index = subfolders.Count - 1; index >= 0 && rows.Count < FolderBrowserLiveSizeLimit; index--)
        {
            var folder = subfolders[index];
            if (!folder.Path.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (rows.Count == 0)
            {
                latestBytes = folder.Bytes;
            }

            rows.Add(new FolderBrowserLiveSize(folder.Path[prefix.Length..], folder.Path, ByteFormatter.Format(folder.Bytes)));
        }

        if (rows.Count == 0)
        {
            return false;
        }

        var published = rows.Count;
        rows.AddRange(_folderBrowserLiveSizes.Take(FolderBrowserLiveSizeLimit - published));
        RebuildFolderBrowserPreview(rows);
        SetLiveFolderSizes(rows);

        var latest = rows[0];
        var ancestors = _folderBrowserRows.Where(row => row.CanOpen && !row.IsFile &&
            latest.Path.StartsWith(row.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal)).ToArray();
        var ancestor = ancestors.LastOrDefault();
        var parentBytes = ancestor?.SizeBytes ?? ancestor?.ScannedBytes;
        var parent = parentBytes is long measured ? $", {measured:N0} B in '{ancestor!.Name}'" : string.Empty;
        var expectedParents = latest.Name.Count(character => character == Path.DirectorySeparatorChar);
        var measuredParents = ancestors.Count(row => (row.SizeBytes ?? row.ScannedBytes) >= latestBytes);
        AppLog.Info($"Folder Browser published {published:N0} live exact subfolder sizes under {_folderBrowserPath} " +
            $"({latestBytes:N0} B in the latest folder{parent}, {_folderBrowserScannedRootBytes:N0} B in its browser root, " +
            $"{measuredParents}/{expectedParents} parent rows with sizes in the main list).");
        return true;
    }

    private void ClearLiveFolderSizes()
    {
        if (HasLiveFolderSizes)
        {
            SetLiveFolderSizes([]);
        }
    }

    private void SetLiveFolderSizes(IReadOnlyList<FolderBrowserLiveSize> sizes)
    {
        var wasVisible = HasLiveFolderSizes;
        _folderBrowserLiveSizes = sizes;
        RaisePropertyChanged(nameof(FolderBrowserLiveSizes));
        if (wasVisible != HasLiveFolderSizes)
        {
            RaisePropertyChanged(nameof(HasLiveFolderSizes));
        }
    }

    private void ApplyFolderScanProgress(IReadOnlyList<CompletedFolderSize> completed,
        ParallelFolderWalker.LogicalFolderWalk? live, IReadOnlyList<LiveFolderSize> subfolders)
    {
        foreach (var folder in completed)
        {
            _folderBrowserCompletedSizes[folder.Path] = folder;
        }

        if (live is not null)
        {
            _folderBrowserLiveWalks[live.ScanRoot] = live;
        }

        if (!TryShowLiveFolderSizes(subfolders) && _folderBrowserFileTree is null &&
            (completed.Count > 0 || (live is not null && !FolderBrowserUseAllocation &&
                string.Equals(live.ScanRoot, Path.GetPathRoot(_folderBrowserPath), StringComparison.OrdinalIgnoreCase))))
        {
            RebuildFolderBrowserPreview();
        }
    }

    private void CancelFolderBrowserFileLoads()
    {
        _folderBrowserFileLoadCts?.Cancel();
        _folderBrowserFileLoadCts?.Dispose();
        _folderBrowserFileLoadCts = null;
        _folderBrowserFiles.Clear();
        _folderBrowserFilesLoading.Clear();
    }

    private void ResetFolderBrowserCaches(FolderTree? tree)
    {
        CancelFolderBrowserFileLoads();
        _recentFolderBrowserFiles.Clear();
        _folderBrowserLayouts?.Clear();
        _folderBrowserFileTree = tree;
    }

    private bool TryGetFolderBrowserListing(int node, out FolderFileSnapshot? listing)
    {
        if (_folderBrowserFiles.TryGetValue(node, out listing))
        {
            return true;
        }

        if (!_recentFolderBrowserFiles.Remove(node, out var recent))
        {
            return false;
        }

        _recentFolderBrowserFiles.Add(node, recent);
        _folderBrowserFiles.Add(node, recent);
        listing = recent;
        return true;
    }

    private void RebuildFolderBrowserRows()
    {
        if (!_diskUsageAnalyzer.TryFindNode(_folderBrowserPath, FolderBrowserUseAllocation, out var tree, out var node) || tree is null)
        {
            return;
        }

        RebuildFolderBrowserRows(tree, node);
    }

    private void RebuildFolderBrowserRows(FolderTree tree, int node)
    {
        if (!ReferenceEquals(tree, _folderBrowserFileTree))
        {
            ResetFolderBrowserCaches(tree);
        }

        var showAllocation = FolderBrowserUseAllocation;
        var total = showAllocation ? tree.GetAllocatedBytes(node) : tree.GetTotalBytes(node);
        if (total is null)
        {
            if (_folderBrowserRows.Count > 0)
            {
                _folderBrowserRows.Clear();
            }

            return;
        }

        var rows = new List<FolderBrowserRow>();
        var pendingFolders = new List<FolderFileRequest>();
        var rootTotal = HasExactSize(tree, node, showAllocation) ? total.Value : 0;
        AppendLevel(tree, node, 0, rootTotal, showAllocation, rows, pendingFolders);

        UpdateFolderBrowserRows(rows);

        if (pendingFolders.Count > 0)
        {
            _folderBrowserFileLoadCts ??= new();
            _ = LoadFolderFilesAsync(pendingFolders, _folderBrowserFileLoadCts.Token);
        }
    }

    private void UpdateFolderBrowserRows(List<FolderBrowserRow> rows)
    {
        // Keep unchanged rows at the edges so listing updates don't recreate the whole view.
        var oldEnd = _folderBrowserRows.Count;
        var newEnd = rows.Count;
        var first = 0;
        while (first < oldEnd && first < newEnd && _folderBrowserRows[first].HasSamePresentation(rows[first]))
        {
            first++;
        }

        while (oldEnd > first && newEnd > first && _folderBrowserRows[oldEnd - 1].HasSamePresentation(rows[newEnd - 1]))
        {
            oldEnd--;
            newEnd--;
        }

        var sharedEnd = Math.Min(oldEnd, newEnd);
        for (var index = first; index < sharedEnd; index++)
        {
            if (!_folderBrowserRows[index].HasSamePresentation(rows[index]))
            {
                _folderBrowserRows[index] = rows[index];
            }
        }

        for (var index = oldEnd - 1; index >= newEnd; index--)
        {
            _folderBrowserRows.RemoveAt(index);
        }

        for (var index = oldEnd; index < newEnd; index++)
        {
            _folderBrowserRows.Insert(index, rows[index]);
        }
    }

    private FolderBrowserLayout GetFolderBrowserLayout(FolderTree tree, int node, bool showAllocation)
    {
        var childCount = tree.GetChildFolderCount(node);
        var key = (node, showAllocation);
        var cache = _folderBrowserLayouts;
        if (childCount > FolderBrowserMaxRows && cache is not null &&
            cache.TryGetValue(key, out var saved, out var position))
        {
            if (position != cache.Count - 1)
            {
                cache.RemoveAt(position);
                cache.Add(key, saved);
            }

            return saved;
        }

        var children = showAllocation
            ? tree.GetChildrenByAllocation(node, FolderBrowserMaxRows)
            : tree.GetChildren(node, FolderBrowserMaxRows);
        var directFiles = showAllocation ? default : tree.GetDirectFileStatistics(node);
        var directBytes = showAllocation ? tree.GetDirectAllocatedBytes(node)!.Value : directFiles.Bytes;
        var otherFolderCount = childCount - children.Count;
        if (otherFolderCount == 0)
        {
            return new FolderBrowserLayout(children, directBytes, directFiles.FileCount, 0, 0);
        }

        var otherFolderBytes = (showAllocation ? tree.GetAllocatedBytes(node)!.Value : tree.GetTotalBytes(node)) - directBytes;
        foreach (var child in children)
        {
            otherFolderBytes -= showAllocation ? tree.GetAllocatedBytes(child)!.Value : tree.GetTotalBytes(child);
        }

        var layout = new FolderBrowserLayout(children, directBytes, directFiles.FileCount,
            otherFolderCount, Math.Max(0, otherFolderBytes));
        var recentLayouts = _folderBrowserLayouts ??= new();
        if (recentLayouts.Count == FolderBrowserWideLayoutLimit)
        {
            recentLayouts.RemoveAt(0);
        }

        recentLayouts.Add(key, layout);
        return layout;
    }

    private static bool HasExactSize(FolderTree tree, int node, bool showAllocation) =>
        !tree.HasIncompleteScan(node) && (!showAllocation || !tree.HasIncompleteAllocation(node));

    /// <summary>Adds the largest entries and recurses into expanded folders.</summary>
    private void AppendLevel(
        FolderTree tree, int node, int depth, long rootTotal, bool showAllocation,
        List<FolderBrowserRow> output, List<FolderFileRequest> pendingFolders)
    {
        var folderPath = tree.GetPath(node);
        var layout = GetFolderBrowserLayout(tree, node, showAllocation);
        var entries = new List<(long Bytes, string Name, FolderBrowserRow Row, int ChildNode)>();
        var exact = HasExactSize(tree, node, showAllocation);
        double Share(long size) => rootTotal > 0 ? size * 100d / rootTotal : 0;
        string ShareText(double share) => rootTotal > 0 ? $" - {share:0.0}%" : string.Empty;

        foreach (var child in layout.Children)
        {
            var size = showAllocation ? tree.GetAllocatedBytes(child)!.Value : tree.GetTotalBytes(child);
            var name = tree.GetName(child);
            var childPath = Path.Join(folderPath, name);
            var share = Share(size);
            var expanded = _folderBrowserExpanded.Contains(childPath);
            var hasContent = tree.HasChildFolders(child) || (showAllocation ? size > 0 : tree.GetFileCount(child) > 0);
            var complete = HasExactSize(tree, child, showAllocation);
            var row = new FolderBrowserRow(
                name,
                childPath,
                complete ? size : null,
                complete ? $"{tree.GetFileCount(child):N0} files{ShareText(share)}" : "Exact size unavailable",
                complete ? share : 0,
                depth,
                canOpen: true,
                canExpand: hasContent,
                isExpanded: expanded && hasContent,
                isFile: false,
                scannedBytes: !complete && !showAllocation && size > 0 ? size : null);
            entries.Add((complete || !showAllocation ? size : -1, row.Name, row, expanded && hasContent ? child : -1));
        }

        var directBytes = layout.DirectBytes;
        if (layout.OtherFolderCount > 0)
        {
            var share = exact ? Share(layout.OtherFolderBytes) : 0;
            entries.Add((exact ? layout.OtherFolderBytes : -1, string.Empty, new FolderBrowserRow(
                "[other folders]", folderPath, exact ? layout.OtherFolderBytes : null,
                $"{layout.OtherFolderCount:N0} more folders{(exact ? ShareText(share) : " - exact size unavailable")}",
                share, depth, canOpen: false, canExpand: false, isExpanded: false, isFile: false), -1));
        }

        if (showAllocation)
        {
            if (directBytes > 0)
            {
                var share = exact ? Share(directBytes) : 0;
                entries.Add((exact ? directBytes : -1, string.Empty, new FolderBrowserRow(
                    "Direct allocation", folderPath, exact ? directBytes : null,
                    exact ? $"Files, streams and folder metadata{ShareText(share)}" : "Exact allocation unavailable",
                    share, depth, canOpen: false, canExpand: false, isExpanded: false, isFile: false), -1));
            }
        }
        else if (directBytes > 0 || layout.DirectFileCount > 0)
        {
            if (!TryGetFolderBrowserListing(node, out var listing))
            {
                if (_folderBrowserFilesLoading.Add(node))
                {
                    pendingFolders.Add(new FolderFileRequest(node, folderPath, directBytes, layout.DirectFileCount));
                }

                AddFileSummary("[files in this folder]", directBytes, "loading file names");
            }
            else if (listing is null)
            {
                AddFileSummary("[files in this scan]", directBytes, "live file listing unavailable");
            }
            else if (!listing.Matches(directBytes, layout.DirectFileCount))
            {
                AddFileSummary("[files in this scan]", directBytes, "live file listing differs from the scan");
            }
            else
            {
                var listed = 0L;
                foreach (var file in listing.Files)
                {
                    listed += file.Bytes;
                    var share = Share(file.Bytes);
                    entries.Add((file.Bytes, file.Name, new FolderBrowserRow(
                        file.Name, file.Path, file.Bytes, $"file{ShareText(share)}",
                        share, depth, canOpen: false, canExpand: false, isExpanded: false, isFile: true), -1));
                }

                var otherFiles = layout.DirectFileCount - listing.Files.Count;
                if (otherFiles > 0)
                {
                    AddFileSummary("[other files]", directBytes - listed, $"{otherFiles:N0} smaller files");
                }
            }
        }

        void AddFileSummary(string title, long bytes, string detail)
        {
            var share = exact ? Share(bytes) : 0;
            entries.Add((exact ? bytes : -1, string.Empty, new FolderBrowserRow(
                title, folderPath, exact ? bytes : null, $"{detail}{(exact ? ShareText(share) : " - exact size unavailable")}",
                share, depth, canOpen: false, canExpand: false, isExpanded: false, isFile: true), -1));
        }

        entries.Sort((a, b) =>
        {
            var bySize = b.Bytes.CompareTo(a.Bytes);
            return bySize != 0 ? bySize : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        foreach (var entry in entries)
        {
            output.Add(entry.Row);
            if (entry.ChildNode >= 0)
            {
                AppendLevel(tree, entry.ChildNode, depth + 1, rootTotal, showAllocation, output, pendingFolders);
            }
        }
    }

    private readonly record struct FolderBrowserLayout(
        IReadOnlyList<int> Children, long DirectBytes, long DirectFileCount, int OtherFolderCount, long OtherFolderBytes);

    private async Task LoadFolderFilesAsync(List<FolderFileRequest> folders, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var ready in FolderFileListing.ReadBatchesAsync(folders, FolderBrowserMaxFiles, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var (folder, listing) in ready)
                {
                    _folderBrowserFilesLoading.Remove(folder.Node);
                    _folderBrowserFiles[folder.Node] = listing;
                    if (listing is null || !listing.Matches(folder.Bytes, folder.FileCount))
                    {
                        continue;
                    }

                    // Cache matching listings for later visits within the same scan.
                    if (_recentFolderBrowserFiles.Count == FolderBrowserRecentListingLimit)
                    {
                        _recentFolderBrowserFiles.RemoveAt(0);
                    }

                    _recentFolderBrowserFiles.Add(folder.Node, listing);
                }

                RebuildFolderBrowserRows();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
