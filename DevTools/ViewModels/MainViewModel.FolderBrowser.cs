using System.Collections.ObjectModel;
using System.Diagnostics;
using DevTools.Infrastructure;
using DevTools.Services;
using DevTools.Services.FolderSizes;

namespace DevTools.ViewModels;

public sealed record FolderBrowserDrive(string Path, string Label);

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
        bool isLowerBound = false)
    {
        Name = name;
        Path = path;
        SizeBytes = sizeBytes;
        SizeText = sizeBytes is long bytes ? (isLowerBound ? "≥ " : string.Empty) + ByteFormatter.Format(bytes) : "—";
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

    public bool HasMeasuredSize => SizeBytes.HasValue;

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
        SizeBytes == other.SizeBytes && SharePercent == other.SharePercent && Depth == other.Depth &&
        CanOpen == other.CanOpen && CanExpand == other.CanExpand && IsExpanded == other.IsExpanded &&
        IsFile == other.IsFile && Name == other.Name && Path == other.Path &&
        SizeText == other.SizeText && DetailText == other.DetailText;
}

public sealed partial class MainViewModel
{
    private const int FolderBrowserMaxRows = 200;
    private const int FolderBrowserMaxFiles = 100;
    private const int FolderBrowserPreviewSizeLimit = 48;
    private const int FolderBrowserPreviewDirectoryLimit = 8;
    private const int FolderBrowserPreviewEntryLimit = 2048;
    private const int FolderBrowserRecentListingLimit = 32;
    private const int FolderBrowserWideLayoutLimit = 32;

    private readonly ObservableCollection<FolderBrowserRow> _folderBrowserRows = [];
    private readonly ObservableCollection<FolderBrowserDrive> _folderBrowserDrives = [];
    private readonly HashSet<string> _folderBrowserExpanded = new(StringComparer.Ordinal);
    private readonly Dictionary<int, FolderFileSnapshot?> _folderBrowserFiles = [];
    private readonly HashSet<int> _folderBrowserFilesLoading = [];
    private readonly OrderedDictionary<int, FolderFileSnapshot> _recentFolderBrowserFiles = new();
    private OrderedDictionary<(int Node, bool Allocation), FolderBrowserLayout>? _folderBrowserLayouts;
    private FolderTree? _folderBrowserFileTree;
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

    public string FolderBrowserPath => _folderBrowserPath;

    public string FolderBrowserSummary => GetSelectedVolumeNote() ?? _folderBrowserSummary;

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

        _folderBrowserPath = normalized;
        _canFolderBrowserGoUp = node != 0;
        _showFolderBrowserVolumeSummary = node == 0;
        var allocation = tree.GetAllocatedBytes(node) is long allocatedBytes
            ? $" {ByteFormatter.Format(allocatedBytes)} of attributed allocation."
            : FolderBrowserUseAllocation ? " Allocation is unavailable for this scan." : string.Empty;
        var coverage = tree.HasIncompleteScan(node) ? " Some folder listings were incomplete." : string.Empty;
        if (tree.HasIncompleteAllocation(node))
        {
            coverage += " Allocation could not be fully measured.";
        }
        if (node == 0)
        {
            coverage += DiskUsageAnalyzer.DescribeIncompleteMftAllocation(tree);
        }

        _folderBrowserSummary =
            $"{ByteFormatter.Format(tree.GetTotalBytes(node))} of logical file size in {tree.GetFileCount(node):N0} files.{allocation}{coverage} " +
            $"Sized by {tree.EngineName} in {tree.Elapsed.TotalSeconds:F1}s.";
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
        if (BrowseFolder(path, skipUnchanged: true))
        {
            return;
        }

        var driveScanCompleted = scanCompleted || completedRoot is not null;
        if (string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase))
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

            var rows = new List<FolderBrowserRow>(preview.FolderNames.Count + (preview.HasMore ? 1 : 0));
            foreach (var name in preview.FolderNames)
            {
                rows.Add(new FolderBrowserRow(name, Path.Join(path, name), null, "Open to size this folder", 0, 0,
                    canOpen: true, canExpand: false, isExpanded: false, isFile: false));
            }

            if (preview.HasMore)
            {
                rows.Add(new FolderBrowserRow("Additional folders", path, null,
                    $"Preview limited to {FolderBrowserMaxRows:N0} folders", 0, 0,
                    canOpen: false, canExpand: false, isExpanded: false, isFile: false));
            }

            UpdateFolderBrowserRows(rows);
            if (!useAllocation && preview.FolderNames.Count > 0)
            {
                await LoadFolderPreviewSizesAsync(preview.FolderNames.Count, rows, started, cancellationToken);
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

    private Task LoadFolderPreviewSizesAsync(int folderCount, IReadOnlyList<FolderBrowserRow> rows, long started,
        CancellationToken cancellationToken)
    {
        var firstNonzeroSize = true;
        IProgress<(int Index, FolderPreviewSize Size)> progress = new Progress<(int Index, FolderPreviewSize Size)>(value =>
        {
            if (cancellationToken.IsCancellationRequested || _folderBrowserFileTree is not null || FolderBrowserUseAllocation ||
                value.Index >= _folderBrowserRows.Count || !ReferenceEquals(_folderBrowserRows[value.Index], rows[value.Index]))
            {
                return;
            }

            var row = rows[value.Index];
            _folderBrowserRows[value.Index] = new FolderBrowserRow(row.Name, row.Path, value.Size.Bytes,
                value.Size.IsComplete ? "Logical size from quick scan" : "Logical bytes counted so far",
                0, row.Depth, row.CanOpen, row.CanExpand, row.IsExpanded, row.IsFile, isLowerBound: !value.Size.IsComplete);
            if (firstNonzeroSize && value.Size.Bytes > 0)
            {
                firstNonzeroSize = false;
                AppLog.Info($"Folder Browser first nonzero preview size shown after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms.");
            }
        });

        return Task.Run(() =>
        {
            var count = Math.Min(folderCount, FolderBrowserPreviewSizeLimit);
            for (var index = 0; index < count; index++)
            {
                var size = FolderFileListing.ReadPreviewSize(rows[index].Path, FolderBrowserPreviewDirectoryLimit,
                    FolderBrowserPreviewEntryLimit, cancellationToken);
                if (size is { } measured)
                {
                    progress.Report((index, measured));
                }
            }
        }, cancellationToken);
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
        AppendLevel(tree, node, 0, Math.Max(1, total.Value), showAllocation, rows, pendingFolders);

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

    /// <summary>Adds the largest entries and recurses into expanded folders.</summary>
    private void AppendLevel(
        FolderTree tree, int node, int depth, long rootTotal, bool showAllocation,
        List<FolderBrowserRow> output, List<FolderFileRequest> pendingFolders)
    {
        var folderPath = tree.GetPath(node);
        var layout = GetFolderBrowserLayout(tree, node, showAllocation);
        var entries = new List<(long Bytes, string Name, FolderBrowserRow Row, int ChildNode)>();

        foreach (var child in layout.Children)
        {
            var size = showAllocation ? tree.GetAllocatedBytes(child)!.Value : tree.GetTotalBytes(child);
            var name = tree.GetName(child);
            var childPath = Path.Join(folderPath, name);
            var share = size * 100d / rootTotal;
            var expanded = _folderBrowserExpanded.Contains(childPath);
            var hasContent = tree.HasChildFolders(child) || (showAllocation ? size > 0 : tree.GetFileCount(child) > 0);
            var coverage = tree.HasIncompleteScan(child) ? " - incomplete listing" : string.Empty;
            if (tree.HasIncompleteAllocation(child))
            {
                coverage += " - incomplete allocation";
            }
            var row = new FolderBrowserRow(
                name,
                childPath,
                size,
                $"{tree.GetFileCount(child):N0} files - {share:0.0}%{coverage}",
                share,
                depth,
                canOpen: hasContent,
                canExpand: hasContent,
                isExpanded: expanded && hasContent,
                isFile: false);
            entries.Add((size, row.Name, row, expanded && hasContent ? child : -1));
        }

        var directBytes = layout.DirectBytes;
        if (layout.OtherFolderCount > 0)
        {
            var share = layout.OtherFolderBytes * 100d / rootTotal;
            entries.Add((layout.OtherFolderBytes, string.Empty, new FolderBrowserRow(
                "[other folders]", folderPath, layout.OtherFolderBytes, $"{layout.OtherFolderCount:N0} more folders - {share:0.0}%",
                share, depth, canOpen: false, canExpand: false, isExpanded: false, isFile: false), -1));
        }

        if (showAllocation)
        {
            if (directBytes > 0)
            {
                var share = directBytes * 100d / rootTotal;
                entries.Add((directBytes, string.Empty, new FolderBrowserRow(
                    "Direct allocation", folderPath, directBytes, $"Files, streams and folder metadata - {share:0.0}%",
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
                    var share = file.Bytes * 100d / rootTotal;
                    entries.Add((file.Bytes, file.Name, new FolderBrowserRow(
                        file.Name, file.Path, file.Bytes, $"file - {share:0.0}%",
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
            var share = bytes * 100d / rootTotal;
            entries.Add((bytes, string.Empty, new FolderBrowserRow(
                title, folderPath, bytes, $"{detail} - {share:0.0}%",
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
