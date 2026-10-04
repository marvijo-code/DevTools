using System.Collections.ObjectModel;
using DevTools.Infrastructure;
using DevTools.Services.FolderSizes;

namespace DevTools.ViewModels;

/// <summary>One row of the folder browser: a subfolder, or the files that sit directly in the folder.</summary>
public sealed class FolderBrowserRow
{
    public FolderBrowserRow(string name, string path, long sizeBytes, long fileCount, double sharePercent, bool canOpen, bool isFilesRow)
    {
        Name = name;
        Path = path;
        SizeBytes = sizeBytes;
        SizeText = ByteFormatter.Format(sizeBytes);
        DetailText = isFilesRow
            ? $"{fileCount:N0} files directly in this folder - {sharePercent:0.0}%"
            : $"{fileCount:N0} files - {sharePercent:0.0}%";
        SharePercent = sharePercent;
        CanOpen = canOpen;
    }

    public string Name { get; }

    public string Path { get; }

    public long SizeBytes { get; }

    public string SizeText { get; }

    public string DetailText { get; }

    public double SharePercent { get; }

    /// <summary>True when the row is a folder with subfolders the user can open.</summary>
    public bool CanOpen { get; }
}

public sealed partial class MainViewModel
{
    private const int FolderBrowserMaxRows = 200;
    private readonly ObservableCollection<FolderBrowserRow> _folderBrowserRows = [];
    private readonly ObservableCollection<string> _folderBrowserDrives = [];
    private string _folderBrowserPath = string.Empty;
    private string _folderBrowserSummary = "The folder browser fills in when the first drive scan finishes.";
    private bool _canFolderBrowserGoUp;

    /// <summary>Rows of the folder currently open in the browser, largest first.</summary>
    public ObservableCollection<FolderBrowserRow> FolderBrowserRows => _folderBrowserRows;

    /// <summary>Drive roots that have a finished folder tree.</summary>
    public ObservableCollection<string> FolderBrowserDrives => _folderBrowserDrives;

    public string FolderBrowserPath => _folderBrowserPath;

    public string FolderBrowserSummary => _folderBrowserSummary;

    public bool CanFolderBrowserGoUp => _canFolderBrowserGoUp;

    /// <summary>Shows the subfolders of <paramref name="path"/> from the in-memory drive tree.</summary>
    public bool BrowseFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !_diskUsageAnalyzer.TryFindNode(path, out var tree, out var node) || tree is null)
        {
            return false;
        }

        var total = Math.Max(1, tree.GetTotalBytes(node));
        var rows = new List<FolderBrowserRow>();
        foreach (var child in tree.GetChildren(node, FolderBrowserMaxRows))
        {
            var size = tree.GetTotalBytes(child);
            rows.Add(new FolderBrowserRow(
                tree.GetName(child),
                tree.GetPath(child),
                size,
                tree.GetFileCount(child),
                size * 100d / total,
                tree.HasChildFolders(child),
                isFilesRow: false));
        }

        var directFiles = tree.GetDirectFileBytes(node);
        if (directFiles > 0)
        {
            var directFileCount = tree.GetFileCount(node) - tree.GetChildren(node).Sum(tree.GetFileCount);
            rows.Add(new FolderBrowserRow("[files in this folder]", tree.GetPath(node), directFiles, Math.Max(0, directFileCount), directFiles * 100d / total, canOpen: false, isFilesRow: true));
        }

        rows.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
        _folderBrowserRows.Clear();
        foreach (var row in rows)
        {
            _folderBrowserRows.Add(row);
        }

        _folderBrowserPath = tree.GetPath(node);
        _canFolderBrowserGoUp = node != 0;
        _folderBrowserSummary =
            $"{ByteFormatter.Format(tree.GetTotalBytes(node))} in {tree.GetFileCount(node):N0} files. " +
            $"Sized by {tree.EngineName} in {tree.Elapsed.TotalSeconds:F1}s. Click a folder to open it.";
        RaisePropertyChanged(nameof(FolderBrowserPath));
        RaisePropertyChanged(nameof(FolderBrowserSummary));
        RaisePropertyChanged(nameof(CanFolderBrowserGoUp));
        return true;
    }

    public void BrowseFolderUp()
    {
        if (!_canFolderBrowserGoUp || !_diskUsageAnalyzer.TryFindNode(_folderBrowserPath, out var tree, out var node) || tree is null)
        {
            return;
        }

        BrowseFolder(tree.GetPath(tree.GetParent(node)));
    }

    /// <summary>Called on the UI thread whenever a drive tree is added or replaced.</summary>
    private void RefreshFolderBrowserDrives()
    {
        var roots = _diskUsageAnalyzer.Trees.Select(tree => tree.RootPath).ToList();
        if (!roots.SequenceEqual(_folderBrowserDrives))
        {
            _folderBrowserDrives.Clear();
            foreach (var root in roots)
            {
                _folderBrowserDrives.Add(root);
            }
        }

        // Keep the open folder if it still exists in the new tree, else open the first drive.
        if (!BrowseFolder(_folderBrowserPath) && roots.Count > 0)
        {
            BrowseFolder(roots[0]);
        }
    }
}
