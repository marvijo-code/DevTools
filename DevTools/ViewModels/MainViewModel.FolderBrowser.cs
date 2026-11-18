using System.Collections.ObjectModel;
using DevTools.Infrastructure;
using DevTools.Services.FolderSizes;

namespace DevTools.ViewModels;

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
    private readonly ObservableCollection<string> _folderBrowserDrives = [];
    private readonly HashSet<string> _folderBrowserExpanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<FolderFile>> _folderBrowserFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folderBrowserFilesLoading = new(StringComparer.OrdinalIgnoreCase);
    private FolderTree? _folderBrowserFileTree;
    private string _folderBrowserPath = string.Empty;
    private string _folderBrowserSummary = "The folder browser fills in when the first drive scan finishes.";
    private bool _canFolderBrowserGoUp;
    private int _folderBrowserFileGeneration;
    private CancellationTokenSource? _folderBrowserClickCts;


    /// <summary>Rows of the folder currently open in the browser, largest first at every level.</summary>
    public ObservableCollection<FolderBrowserRow> FolderBrowserRows => _folderBrowserRows;

    /// <summary>Drive roots that have a finished folder tree.</summary>
    public ObservableCollection<string> FolderBrowserDrives => _folderBrowserDrives;

    public string FolderBrowserPath => _folderBrowserPath;

    public string FolderBrowserSummary => _folderBrowserSummary;

    public bool CanFolderBrowserGoUp => _canFolderBrowserGoUp;

    /// <summary>Opens <paramref name="path"/> in the browser, with nothing expanded.</summary>
    public bool BrowseFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !_diskUsageAnalyzer.TryFindNode(path, out var tree, out var node) || tree is null)
        {
            return false;
        }

        var normalized = tree.GetPath(node);
        if (!string.Equals(normalized, _folderBrowserPath, StringComparison.OrdinalIgnoreCase))
        {
            _folderBrowserExpanded.Clear();
            ResetFolderBrowserFileCache();
        }

        _folderBrowserPath = normalized;
        _canFolderBrowserGoUp = node != 0;
        var allocation = tree.GetAllocatedBytes(node) is long allocatedBytes
            ? $" {ByteFormatter.Format(allocatedBytes)} of attributed allocation."
            : string.Empty;
        _folderBrowserSummary =
            $"{ByteFormatter.Format(tree.GetTotalBytes(node))} of logical file size in {tree.GetFileCount(node):N0} files.{allocation} " +
            $"Sized by {tree.EngineName} in {tree.Elapsed.TotalSeconds:F1}s. Use the arrows to expand folders or the name to open one.";
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

        if (IsDiskUsageRefreshing && _folderBrowserDrives.Contains(path, StringComparer.OrdinalIgnoreCase))
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

        if (!roots.SequenceEqual(_folderBrowserDrives))
        {
            _folderBrowserDrives.Clear();
            foreach (var root in roots)
            {
                _folderBrowserDrives.Add(root);
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
            ? "No fixed drives with drive letters are available."
            : scanCompleted
                ? $"Folder sizes for {path} are unavailable."
                : $"Waiting for the drive scan of {path}.";
        RaisePropertyChanged(nameof(FolderBrowserPath));
        RaisePropertyChanged(nameof(FolderBrowserSummary));
        RaisePropertyChanged(nameof(CanFolderBrowserGoUp));
    }

    private void ResetFolderBrowserFileCache()
    {
        _folderBrowserFileGeneration++;
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

        var rows = new List<FolderBrowserRow>();
        var pendingFolders = new List<string>();
        AppendLevel(tree, node, 0, Math.Max(1, tree.GetTotalBytes(node)), rows, pendingFolders);

        _folderBrowserRows.Clear();
        foreach (var row in rows)
        {
            _folderBrowserRows.Add(row);
        }

        if (pendingFolders.Count > 0)
        {
            _ = LoadFolderFilesAsync(pendingFolders, _folderBrowserFileGeneration);
        }
    }

    /// <summary>Adds the children of <paramref name="node"/> (folders and files, largest first) and recurses into expanded folders.</summary>
    private void AppendLevel(FolderTree tree, int node, int depth, long rootTotal, List<FolderBrowserRow> output, List<string> pendingFolders)
    {
        var folderPath = tree.GetPath(node);
        var entries = new List<(long Bytes, string Name, FolderBrowserRow Row, int ChildNode)>();

        var children = tree.GetChildren(node, FolderBrowserMaxRows);
        var listedFolderBytes = 0L;
        foreach (var child in children)
        {
            var size = tree.GetTotalBytes(child);
            listedFolderBytes += size;
            var childPath = tree.GetPath(child);
            var expanded = _folderBrowserExpanded.Contains(childPath);
            var hasContent = tree.HasChildFolders(child) || tree.GetFileCount(child) > 0;
            var row = new FolderBrowserRow(
                tree.GetName(child),
                childPath,
                size,
                $"{tree.GetFileCount(child):N0} files - {size * 100d / rootTotal:0.0}%",
                size * 100d / rootTotal,
                depth,
                canOpen: hasContent,
                canExpand: hasContent,
                isExpanded: expanded && hasContent,
                isFile: false);
            entries.Add((size, row.Name, row, expanded && hasContent ? child : -1));
        }

        var directFiles = tree.GetDirectFileBytes(node);
        var otherFolderCount = tree.GetChildFolderCount(node) - children.Count;
        if (otherFolderCount > 0)
        {
            var otherFolderBytes = Math.Max(0, tree.GetTotalBytes(node) - directFiles - listedFolderBytes);
            var share = otherFolderBytes * 100d / rootTotal;
            entries.Add((otherFolderBytes, string.Empty, new FolderBrowserRow(
                "[other folders]", folderPath, otherFolderBytes, $"{otherFolderCount:N0} more folders - {share:0.0}%",
                share, depth, canOpen: false, canExpand: false, isExpanded: false, isFile: false), -1));
        }

        if (directFiles > 0 || tree.GetDirectFileCount(node) > 0)
        {
            if (_folderBrowserFiles.TryGetValue(folderPath, out var files))
            {
                var listed = 0L;
                foreach (var file in files)
                {
                    listed += file.Bytes;
                    entries.Add((file.Bytes, file.Name, new FolderBrowserRow(
                        file.Name,
                        file.Path,
                        file.Bytes,
                        $"file - {file.Bytes * 100d / rootTotal:0.0}%",
                        file.Bytes * 100d / rootTotal,
                        depth,
                        canOpen: false,
                        canExpand: false,
                        isExpanded: false,
                        isFile: true), -1));
                }

                var rest = directFiles - listed;
                if (rest > 0)
                {
                    entries.Add((rest, string.Empty, new FolderBrowserRow(
                        "[other files]",
                        folderPath,
                        rest,
                        $"smaller or unreadable files directly in this folder - {rest * 100d / rootTotal:0.0}%",
                        rest * 100d / rootTotal,
                        depth,
                        canOpen: false,
                        canExpand: false,
                        isExpanded: false,
                        isFile: true), -1));
                }
            }
            else
            {
                // Real file names load in the background; show the aggregate meanwhile.
                if (_folderBrowserFilesLoading.Add(folderPath))
                {
                    pendingFolders.Add(folderPath);
                }

                entries.Add((directFiles, string.Empty, new FolderBrowserRow(
                    "[files in this folder]",
                    folderPath,
                    directFiles,
                    $"loading file names - {directFiles * 100d / rootTotal:0.0}%",
                    directFiles * 100d / rootTotal,
                    depth,
                    canOpen: false,
                    canExpand: false,
                    isExpanded: false,
                    isFile: true), -1));
            }
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
                AppendLevel(tree, entry.ChildNode, depth + 1, rootTotal, output, pendingFolders);
            }
        }
    }

    private async Task LoadFolderFilesAsync(List<string> folders, int generation)
    {
        var loaded = await Task.Run(() => folders.ToDictionary(
            folder => folder,
            folder => FolderFileListing.ReadLargest(folder, FolderBrowserMaxFiles),
            StringComparer.OrdinalIgnoreCase));

        if (generation != _folderBrowserFileGeneration)
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
