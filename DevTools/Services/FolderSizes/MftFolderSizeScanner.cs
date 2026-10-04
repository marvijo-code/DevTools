using System.Diagnostics;
using DevTools.Services.BiggestFiles;
using DevTools.Services.BiggestFiles.Ntfs;

namespace DevTools.Services.FolderSizes;

/// <summary>
/// Fast-path folder-size engine. It reads the same NTFS Master File Table as the biggest-files
/// scan, then rolls every file's logical size up through its ancestor directories. One pass over
/// the table yields the total for every folder in the scanned subtree, so the largest folders are
/// known without walking the directory tree.
/// </summary>
public sealed class MftFolderSizeScanner
{
    private const string EngineName = "NTFS MFT";

    public async Task<FolderSizeResult> ScanAsync(
        FolderSizeRequest request,
        IProgress<FolderSizeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(request.ScanRoot);
        var stopwatch = Stopwatch.StartNew();

        var table = await MftTableLoader.LoadAsync(
            root,
            (parsed, total, elapsed) => progress?.Report(new FolderSizeProgress(
                "Reading MFT", parsed, total, elapsed, Array.Empty<FolderSizeHit>(), IsCompleted: false)),
            stopwatch,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // Ancestry filter for subtree scans (volume-root scans include everything).
        var isSubtree = !string.Equals(
            root.TrimEnd(Path.DirectorySeparatorChar),
            table.VolumeRoot.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
        var targetRecord = isSubtree ? table.ResolveDirectoryRecord(root) : (uint?)null;
        if (isSubtree && targetRecord is null)
        {
            throw new DirectoryNotFoundException($"Could not resolve '{root}' in the MFT.");
        }

        var target = targetRecord ?? MftRecordTable.RootRecordNumber;
        var ancestry = targetRecord is null ? null : new byte[table.RecordCount];

        var directoryBytes = AggregateDirectoryBytes(table, target, ancestry);
        cancellationToken.ThrowIfCancellationRequested();

        var hits = RankChildFolders(table, target, directoryBytes, request.TopCount, request.ChildrenPerFolder);

        stopwatch.Stop();
        progress?.Report(new FolderSizeProgress(
            "Done", table.RecordCount, table.RecordCount, stopwatch.Elapsed, hits, IsCompleted: true));

        return new FolderSizeResult(hits, table.RecordCount, stopwatch.Elapsed, EngineName, Warning: null);
    }

    /// <summary>
    /// Reads the whole Master File Table of the drive and returns the size of every folder on it.
    /// All work runs on the thread pool, never on the caller's (UI) thread.
    /// </summary>
    public Task<FolderTree> ScanTreeAsync(
        string driveRoot,
        IProgress<FolderSizeProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            var root = FolderTree.NormalizeRoot(driveRoot);
            var stopwatch = Stopwatch.StartNew();
            var table = await MftTableLoader.LoadAsync(
                root,
                (parsed, total, elapsed) => progress?.Report(new FolderSizeProgress(
                    "Reading MFT", parsed, total, elapsed, Array.Empty<FolderSizeHit>(), IsCompleted: false)),
                stopwatch,
                cancellationToken,
                keepFileNames: false).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var tree = BuildTree(table, root, EngineName, stopwatch);
            return tree;
        }, cancellationToken);
    }

    /// <summary>
    /// Turns a loaded MFT table into a <see cref="FolderTree"/>: folders are numbered parent first
    /// (breadth first from the root), then every file adds its size to its folder and a reverse
    /// pass rolls the totals up. Hard links count once, like the biggest-files scan.
    /// </summary>
    internal static FolderTree BuildTree(MftRecordTable table, string rootPath, string engineName, Stopwatch stopwatch)
    {
        var recordCount = table.RecordCount;
        var root = (int)MftRecordTable.RootRecordNumber;

        // Child-directory index by parent record (counting sort).
        var childStart = new int[recordCount + 1];
        for (var r = 0; r < recordCount; r++)
        {
            if (r == root || !table.IsNamedDirectory(r))
            {
                continue;
            }

            var p = (int)table.Parent[r];
            if (p >= 0 && p < recordCount && p != r)
            {
                childStart[p + 1]++;
            }
        }

        for (var i = 0; i < recordCount; i++)
        {
            childStart[i + 1] += childStart[i];
        }

        var children = new int[childStart[recordCount]];
        var cursor = (int[])childStart.Clone();
        for (var r = 0; r < recordCount; r++)
        {
            if (r == root || !table.IsNamedDirectory(r))
            {
                continue;
            }

            var p = (int)table.Parent[r];
            if (p >= 0 && p < recordCount && p != r)
            {
                children[cursor[p]++] = r;
            }
        }

        // Breadth-first numbering so every parent node index is lower than its children.
        var recordToNode = new int[recordCount];
        Array.Fill(recordToNode, -1);
        var nodeParent = new List<int>(children.Length + 1) { 0 };
        var nodeNames = new List<string>(children.Length + 1) { string.Empty };
        var nodeRecords = new List<int>(children.Length + 1) { root };
        recordToNode[root] = 0;
        for (var head = 0; head < nodeRecords.Count; head++)
        {
            var record = nodeRecords[head];
            for (var i = childStart[record]; i < childStart[record + 1]; i++)
            {
                var child = children[i];
                if (recordToNode[child] >= 0)
                {
                    continue;
                }

                recordToNode[child] = nodeRecords.Count;
                nodeParent.Add(head);
                nodeNames.Add(table.Names[child] ?? "<unnamed>");
                nodeRecords.Add(child);
            }
        }

        var count = nodeRecords.Count;
        var totals = new long[count];
        var files = new long[count];
        for (var r = 0; r < recordCount; r++)
        {
            if (r < MftRecordTable.FirstUserRecordNumber || !table.IsNamedFile(r))
            {
                continue;
            }

            var p = (int)table.Parent[r];
            if (p < 0 || p >= recordCount)
            {
                continue;
            }

            var node = recordToNode[p];
            if (node < 0)
            {
                continue;
            }

            totals[node] += Math.Max(0, table.RealSize[r]);
            files[node]++;
        }

        var parentArray = nodeParent.ToArray();
        for (var i = count - 1; i > 0; i--)
        {
            totals[parentArray[i]] += totals[i];
            files[parentArray[i]] += files[i];
        }

        stopwatch.Stop();
        return new FolderTree(rootPath, parentArray, nodeNames.ToArray(), totals, files, engineName, stopwatch.Elapsed);
    }

    /// <summary>
    /// Returns the total logical bytes for every directory record under <paramref name="target"/>
    /// by adding each file's size to its ancestor chain. Indexed by MFT record number.
    /// </summary>
    internal static long[] AggregateDirectoryBytes(MftRecordTable table, uint target, byte[]? ancestry)
    {
        var directoryBytes = new long[table.RecordCount];
        for (var index = 0; index < table.RecordCount; index++)
        {
            if (index < MftRecordTable.FirstUserRecordNumber || !table.IsNamedFile(index))
            {
                continue;
            }

            if (ancestry is not null && !table.IsUnder(index, target, ancestry))
            {
                continue;
            }

            var size = table.RealSize[index];
            if (size <= 0)
            {
                continue;
            }

            var current = (int)table.Parent[index];
            for (var depth = 0; depth < MftRecordTable.MaxAncestryDepth; depth++)
            {
                if (current < 0 || current >= table.RecordCount)
                {
                    break;
                }

                if ((table.Flags[current] & MftRecordTable.FlagDirectory) == 0)
                {
                    break;
                }

                directoryBytes[current] += size;

                if ((uint)current == target)
                {
                    break;
                }

                var next = (int)table.Parent[current];
                if (next == current)
                {
                    break;
                }

                current = next;
            }
        }

        return directoryBytes;
    }

    /// <summary>
    /// Ranks the direct child folders of <paramref name="target"/> by size and attaches, to each
    /// winner, its own largest direct child folders so the UI can expand it.
    /// </summary>
    internal static IReadOnlyList<FolderSizeHit> RankChildFolders(
        MftRecordTable table,
        uint target,
        long[] directoryBytes,
        int topCount,
        int childrenPerFolder)
    {
        var topN = new TopNAccumulator<int>(topCount);
        for (var index = 0; index < table.RecordCount; index++)
        {
            if ((uint)index == target || !table.IsNamedDirectory(index) || table.Parent[index] != target)
            {
                continue;
            }

            var size = directoryBytes[index];
            if (size > 0)
            {
                topN.TryAdd(size, index);
            }
        }

        var winners = topN.Snapshot();
        var winnerRecords = new HashSet<int>(winners.Select(item => item.Item));
        var winnerChildren = new Dictionary<int, List<(long Size, int Index)>>(winners.Count);

        for (var index = 0; index < table.RecordCount; index++)
        {
            if ((uint)index == target || !table.IsNamedDirectory(index))
            {
                continue;
            }

            var parent = (int)table.Parent[index];
            if (!winnerRecords.Contains(parent))
            {
                continue;
            }

            var size = directoryBytes[index];
            if (size <= 0)
            {
                continue;
            }

            if (!winnerChildren.TryGetValue(parent, out var list))
            {
                list = [];
                winnerChildren[parent] = list;
            }

            list.Add((size, index));
        }

        var pathCache = new Dictionary<uint, string>();
        var hits = new List<FolderSizeHit>(winners.Count);
        foreach (var (size, index) in winners)
        {
            hits.Add(BuildHit(index, size, table, winnerChildren, pathCache, childrenPerFolder));
        }

        return hits;
    }

    private static FolderSizeHit BuildHit(
        int recordIndex,
        long size,
        MftRecordTable table,
        Dictionary<int, List<(long Size, int Index)>> winnerChildren,
        Dictionary<uint, string> pathCache,
        int childrenPerFolder)
    {
        var path = table.BuildDirectoryPath(recordIndex, pathCache, 0);
        var children = new List<FolderSizeHit>();
        if (childrenPerFolder > 0 && winnerChildren.TryGetValue(recordIndex, out var childRecords))
        {
            foreach (var (childSize, childIndex) in childRecords
                         .OrderByDescending(item => item.Size)
                         .ThenBy(item => item.Index)
                         .Take(childrenPerFolder))
            {
                var childPath = table.BuildDirectoryPath(childIndex, pathCache, 0);
                children.Add(new FolderSizeHit(childPath, childSize, []));
            }
        }

        return new FolderSizeHit(path, size, children);
    }
}
