using System.Collections.ObjectModel;
using DevTools.Infrastructure;
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
        long sizeBytes,
        string detail,
        double sharePercent,
        int depth,
        bool canOpen,
        bool canExpand,
        bool isExpanded,
        bool isFile)
    {
        Name = name;
        Path = path;
        SizeBytes = sizeBytes;
        SizeText = ByteFormatter.Format(sizeBytes);
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

    public long SizeBytes { get; }

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
}

public sealed partial class MainViewModel
{
    private const int FolderBrowserMaxRows = 200;
    private const int FolderBrowserMaxFiles = 100;

    private readonly ObservableCollection<FolderBrowserRow> _folderBrowserRows = [];
    private readonly ObservableCollection<FolderBrowserDrive> _folderBrowserDrives = [];
    private readonly HashSet<string> _folderBrowserExpanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FolderFileSnapshot?> _folderBrowserFiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _folderBrowserFilesLoading = new(StringComparer.Ordinal);
    private FolderTree? _folderBrowserFileTree;
    private string _folderBrowserPath = string.Empty;
    private string _folderBrowserSummary = "The folder browser fills in when the first drive scan finishes.";
    private bool _canFolderBrowserGoUp;
    private bool _folderBrowserUseAllocation;
    private CancellationTokenSource? _folderBrowserFileLoadCts;
    private CancellationTokenSource? _folderBrowserClickCts;


    /// <summary>Rows of the folder currently open in the browser, largest first at every level.</summary>
    public ObservableCollection<FolderBrowserRow> FolderBrowserRows => _folderBrowserRows;

    /// <summary>Drive roots that have a finished folder tree.</summary>
    public ObservableCollection<FolderBrowserDrive> FolderBrowserDrives => _folderBrowserDrives;

    public string FolderBrowserPath => _folderBrowserPath;

    public string FolderBrowserSummary => _folderBrowserSummary;

    public bool CanFolderBrowserGoUp => _canFolderBrowserGoUp;

    public bool FolderBrowserUseAllocation
    {
        get => _folderBrowserUseAllocation;
        set
        {
            if (SetProperty(ref _folderBrowserUseAllocation, value))
            {
                BrowseFolder(_folderBrowserPath);
            }
        }
    }

    /// <summary>Opens <paramref name="path"/> in the browser, with nothing expanded.</summary>
    public bool BrowseFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !_diskUsageAnalyzer.TryFindNode(path, out var tree, out var node) || tree is null)
        {
            return false;
        }

        var normalized = tree.GetPath(node);
        if (!string.Equals(normalized, _folderBrowserPath, StringComparison.Ordinal))
        {
            _folderBrowserExpanded.Clear();
            ResetFolderBrowserFileCache();
        }

        _folderBrowserPath = normalized;
        _canFolderBrowserGoUp = node != 0;
        var allocation = tree.GetAllocatedBytes(node) is long allocatedBytes
            ? $" {ByteFormatter.Format(allocatedBytes)} of attributed allocation."
            : FolderBrowserUseAllocation ? " Allocation is unavailable for this scan." : string.Empty;
        var coverage = tree.HasIncompleteScan(node) ? " Some folder listings were incomplete." : string.Empty;
        if (tree.HasIncompleteAllocation(node))
        {
            coverage += " Allocation could not be fully measured.";
        }
        _folderBrowserSummary =
            $"{ByteFormatter.Format(tree.GetTotalBytes(node))} of logical file size in {tree.GetFileCount(node):N0} files.{allocation}{coverage} " +
            $"Sized by {tree.EngineName} in {tree.Elapsed.TotalSeconds:F1}s.";
        RaisePropertyChanged(nameof(FolderBrowserPath));
        RaisePropertyChanged(nameof(FolderBrowserSummary));
        RaisePropertyChanged(nameof(CanFolderBrowserGoUp));

        RebuildFolderBrowserRows();
        return true;
    }

    /// <summary>
    /// Opens a folder, sizing it right now (ahead of the all-drive scan) when it is not known yet.
    /// A newer click cancels the previous one.
    /// </summary>
    public async Task BrowseFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _folderBrowserClickCts?.Cancel();
        if (BrowseFolder(path))
        {
            return;
        }

        if (IsDiskUsageRefreshing && _folderBrowserDrives.Any(drive => path.Equals(drive.Path, StringComparison.OrdinalIgnoreCase)))
        {
            ShowPendingBrowserDrive(path);
            return;
        }

        var cts = _folderBrowserClickCts = new CancellationTokenSource();
        _folderBrowserSummary = $"Sizing {path} first. The all-drive scan pauses until this finishes.";
        RaisePropertyChanged(nameof(FolderBrowserSummary));
        try
        {
            if (await _diskUsageAnalyzer.SizeFolderNowAsync(path, cts.Token) && !cts.IsCancellationRequested)
            {
                BrowseFolder(path);
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
        if (!_canFolderBrowserGoUp || !_diskUsageAnalyzer.TryFindNode(_folderBrowserPath, out var tree, out var node) || tree is null)
        {
            return;
        }

        _ = BrowseFolderAsync(tree.GetPath(tree.GetParent(node)));
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

        if (!roots.SequenceEqual(_folderBrowserDrives.Select(drive => drive.Path), StringComparer.OrdinalIgnoreCase))
        {
            _folderBrowserDrives.Clear();
            foreach (var root in roots)
            {
                var label = WindowsVolumePaths.GetVolumeGuid(root) is { } id ? $"Volume {id.ToString("N")[..8]}" : root;
                _folderBrowserDrives.Add(new FolderBrowserDrive(root, label));
            }
        }

        if (roots.Count == 0)
        {
            ShowPendingBrowserDrive(string.Empty, scanCompleted);
            return;
        }

        var path = selectedRoot is not null && roots.Contains(selectedRoot, StringComparer.OrdinalIgnoreCase)
            ? _folderBrowserPath
            : roots[0];
        var rootPath = Path.GetPathRoot(path)!;
        if (!BrowseFolder(path) &&
            (string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase) || !BrowseFolder(rootPath)))
        {
            ShowPendingBrowserDrive(rootPath, scanCompleted || completedRoot is not null);
        }
    }

    private void ShowPendingBrowserDrive(string path, bool scanCompleted = false)
    {
        _folderBrowserExpanded.Clear();
        ResetFolderBrowserFileCache();
        _folderBrowserFileTree = null;
        _folderBrowserRows.Clear();
        _folderBrowserPath = path;
        _canFolderBrowserGoUp = false;
        _folderBrowserSummary = string.IsNullOrEmpty(path)
            ? "No fixed volumes are available."
            : scanCompleted
                ? $"Folder sizes for {path} are unavailable."
                : $"Waiting for the drive scan of {path}.";
        RaisePropertyChanged(nameof(FolderBrowserPath));
        RaisePropertyChanged(nameof(FolderBrowserSummary));
        RaisePropertyChanged(nameof(CanFolderBrowserGoUp));
    }

    private void ResetFolderBrowserFileCache()
    {
        _folderBrowserFileLoadCts?.Cancel();
        _folderBrowserFileLoadCts?.Dispose();
        _folderBrowserFileLoadCts = null;
        _folderBrowserFiles.Clear();
        _folderBrowserFilesLoading.Clear();
    }

    private void RebuildFolderBrowserRows()
    {
        if (!_diskUsageAnalyzer.TryFindNode(_folderBrowserPath, out var tree, out var node) || tree is null)
        {
            return;
        }

        if (!ReferenceEquals(tree, _folderBrowserFileTree))
        {
            ResetFolderBrowserFileCache();
            _folderBrowserFileTree = tree;
        }

        var showAllocation = FolderBrowserUseAllocation;
        var total = showAllocation ? tree.GetAllocatedBytes(node) : tree.GetTotalBytes(node);
        if (total is null)
        {
            _folderBrowserRows.Clear();
            return;
        }

        var rows = new List<FolderBrowserRow>();
        var pendingFolders = new List<string>();
        AppendLevel(tree, node, 0, Math.Max(1, total.Value), showAllocation, rows, pendingFolders);

        _folderBrowserRows.Clear();
        foreach (var row in rows)
        {
            _folderBrowserRows.Add(row);
        }

        if (pendingFolders.Count > 0)
        {
            _folderBrowserFileLoadCts ??= new();
            _ = LoadFolderFilesAsync(pendingFolders, _folderBrowserFileLoadCts.Token);
        }
    }

    /// <summary>Adds the largest entries and recurses into expanded folders.</summary>
    private void AppendLevel(
        FolderTree tree, int node, int depth, long rootTotal, bool showAllocation,
        List<FolderBrowserRow> output, List<string> pendingFolders)
    {
        var folderPath = tree.GetPath(node);
        var entries = new List<(long Bytes, string Name, FolderBrowserRow Row, int ChildNode)>();

        var children = showAllocation
            ? tree.GetChildrenByAllocation(node, FolderBrowserMaxRows)
            : tree.GetChildren(node, FolderBrowserMaxRows);
        var listedFolderBytes = 0L;
        foreach (var child in children)
        {
            var size = showAllocation ? tree.GetAllocatedBytes(child)!.Value : tree.GetTotalBytes(child);
            listedFolderBytes += size;
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

        var directFiles = showAllocation ? default : tree.GetDirectFileStatistics(node);
        var directBytes = showAllocation ? tree.GetDirectAllocatedBytes(node)!.Value : directFiles.Bytes;
        var otherFolderCount = tree.GetChildFolderCount(node) - children.Count;
        if (otherFolderCount > 0)
        {
            var totalBytes = showAllocation ? tree.GetAllocatedBytes(node)!.Value : tree.GetTotalBytes(node);
            var otherFolderBytes = Math.Max(0, totalBytes - directBytes - listedFolderBytes);
            var share = otherFolderBytes * 100d / rootTotal;
            entries.Add((otherFolderBytes, string.Empty, new FolderBrowserRow(
                "[other folders]", folderPath, otherFolderBytes, $"{otherFolderCount:N0} more folders - {share:0.0}%",
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
        else if (directBytes > 0 || directFiles.FileCount > 0)
        {
            if (!_folderBrowserFiles.TryGetValue(folderPath, out var listing))
            {
                if (_folderBrowserFilesLoading.Add(folderPath))
                {
                    pendingFolders.Add(folderPath);
                }

                AddFileSummary("[files in this folder]", directBytes, "loading file names");
            }
            else if (listing is null)
            {
                AddFileSummary("[files in this scan]", directBytes, "live file listing unavailable");
            }
            else if (!listing.Matches(directBytes, directFiles.FileCount))
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

                var otherFiles = directFiles.FileCount - listing.Files.Count;
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

    private async Task LoadFolderFilesAsync(List<string> folders, CancellationToken cancellationToken)
    {
        Dictionary<string, FolderFileSnapshot?> loaded;
        try
        {
            loaded = await Task.Run(() => folders.ToDictionary(
                folder => folder,
                folder => FolderFileListing.ReadLargest(folder, FolderBrowserMaxFiles, cancellationToken),
                StringComparer.Ordinal), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        foreach (var (folder, files) in loaded)
        {
            _folderBrowserFilesLoading.Remove(folder);
            _folderBrowserFiles[folder] = files;
        }

        RebuildFolderBrowserRows();
    }
}
