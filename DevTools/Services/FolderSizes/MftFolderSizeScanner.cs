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
        using var scan = await MftTableLoader.EnterScanAsync(cancellationToken).ConfigureAwait(false);

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
        CancellationToken cancellationToken) =>
        ScanTreeAsync(driveRoot, progress, cancellationToken, volumeShare: 1);

    internal Task<FolderTree> ScanTreeAsync(
        string driveRoot,
        IProgress<FolderSizeProgress>? progress,
        CancellationToken cancellationToken,
        int volumeShare)
    {
        return Task.Run(async () =>
        {
            var root = FolderTree.NormalizeRoot(driveRoot);
            var stopwatch = Stopwatch.StartNew();
            using var scan = await MftTableLoader.EnterScanAsync(cancellationToken).ConfigureAwait(false);
            var table = await MftTableLoader.LoadAsync(
                root,
                (parsed, total, elapsed) => progress?.Report(new FolderSizeProgress(
                    "Reading MFT", parsed, total, elapsed, Array.Empty<FolderSizeHit>(), IsCompleted: false)),
                stopwatch,
                cancellationToken,
                keepFileNames: false, volumeShare: volumeShare).ConfigureAwait(false);
            return BuildTree(table, root, EngineName, stopwatch, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>
    /// Turns a loaded MFT table into a <see cref="FolderTree"/>: folders are numbered parent first
    /// (breadth first from the root), then every file adds its size to its folder and a reverse
    /// pass rolls the totals up. Hard links count once, like the biggest-files scan.
    /// </summary>
    internal static FolderTree BuildTree(
        MftRecordTable table, string rootPath, string engineName, Stopwatch stopwatch,
        CancellationToken cancellationToken = default)
    {
        ScanGate.Wait(cancellationToken);
        var recordCount = table.RecordCount;
        var root = (int)MftRecordTable.RootRecordNumber;

        // Positive entries point to the first child edge. Once a directory is attached
        // to the tree, the same entry stores the bitwise complement of its node number.
        var recordLinks = new int[recordCount];
        var directoryCount = 0;
        for (var record = 0; record < recordCount; record++)
        {
            WaitForTreeBatch(record, cancellationToken);
            directoryCount += table.IsNamedDirectory(record) ? 1 : 0;
        }

        var children = new (int Record, int Next)[directoryCount];
        var edgeCount = 0;
        for (var record = recordCount - 1; record >= 0; record--)
        {
            WaitForTreeBatch(record, cancellationToken);
            if (record == root || !table.IsNamedDirectory(record))
            {
                continue;
            }

            var parent = table.Parent[record];
            if (parent < (uint)recordCount && parent != record)
            {
                children[edgeCount] = (record, recordLinks[parent]);
                recordLinks[parent] = ++edgeCount;
            }
        }

        // Queue record numbers in the parent array. A queued folder's parent has already
        // been numbered, so replace the record number with the parent index when dequeuing.
        var capacity = edgeCount + 1;
        var nodeParents = new int[capacity];
        var nodeNames = new string[capacity];
        nodeParents[0] = root;
        var count = 1;
        for (var head = 0; head < count; head++)
        {
            WaitForTreeBatch(head, cancellationToken);
            var record = nodeParents[head];
            nodeParents[head] = head == 0 ? 0 : ~recordLinks[table.Parent[record]];
            nodeNames[head] = head == 0 ? string.Empty : table.Names[record] ?? "<unnamed>";

            // Each record has at most one parent edge and the root has no parent edge.
            // A cycle therefore cannot be reached from the root.
            var edge = recordLinks[record];
            recordLinks[record] = ~head;
            while (edge > 0)
            {
                WaitForTreeBatch(count, cancellationToken);
                var (child, next) = children[edge - 1];
                nodeParents[count++] = child;
                edge = next;
            }
        }

        if (count < capacity)
        {
            // Some directory records have no verified path from the root.
            Array.Resize(ref nodeParents, count);
            Array.Resize(ref nodeNames, count);
        }

        var totals = new long[count];
        var files = new long[count];
        var recordAllocation = table.RecordAllocation;
        var allocated = recordAllocation is null ? null : new long[count];
        long reservedClusters = 0;
        long unlinkedClusters = 0;
        var incompleteRecords = 0;
        for (var r = 0; r < recordCount; r++)
        {
            WaitForTreeBatch(r, cancellationToken);
            if ((table.Flags[r] & MftRecordTable.FlagIncompleteAllocation) != 0)
            {
                incompleteRecords++;
            }

            var parentNode = -1;
            var isUserFile = r >= MftRecordTable.FirstUserRecordNumber && table.IsNamedFile(r);
            if (isUserFile)
            {
                var parent = table.Parent[r];
                if (parent < (uint)recordCount && recordLinks[parent] < 0)
                {
                    parentNode = ~recordLinks[parent];
                    totals[parentNode] += Math.Max(0, table.RealSize[r]);
                    files[parentNode]++;
                }
            }

            var clusters = recordAllocation?.GetClusters(r) ?? 0;
            if (clusters <= 0)
            {
                continue;
            }

            if (r < MftRecordTable.FirstUserRecordNumber)
            {
                reservedClusters = checked(reservedClusters + clusters);
                continue;
            }

            var allocationNode = recordLinks[r] < 0 ? ~recordLinks[r] : isUserFile ? parentNode : -1;
            if (allocationNode >= 0)
            {
                ref var folderClusters = ref allocated![allocationNode];
                folderClusters = checked(folderClusters + clusters);
            }
            else
            {
                unlinkedClusters = checked(unlinkedClusters + clusters);
            }
        }

        for (var i = count - 1; i > 0; i--)
        {
            WaitForTreeBatch(i, cancellationToken);
            var parent = nodeParents[i];
            totals[parent] += totals[i];
            files[parent] += files[i];
            if (allocated is not null)
            {
                // Both nodes are still in clusters. Convert the child after its total reaches its parent.
                allocated[parent] = checked(allocated[parent] + allocated[i]);
                allocated[i] = recordAllocation!.ToBytes(allocated[i]);
            }
        }

        NtfsRecordAllocation? allocation = null;
        if (allocated is not null)
        {
            allocated[0] = recordAllocation!.ToBytes(allocated[0]);
            allocation = new NtfsRecordAllocation(recordAllocation.ToBytes(reservedClusters),
                recordAllocation.ToBytes(unlinkedClusters), table.BitmapAllocation, incompleteRecords);
        }

        ScanGate.Wait(cancellationToken);
        stopwatch.Stop();
        var tree = new FolderTree(rootPath, nodeParents, nodeNames, totals, files, engineName, stopwatch.Elapsed,
            allocated, allocation);
        cancellationToken.ThrowIfCancellationRequested();
        return tree;
    }

    private static void WaitForTreeBatch(int index, CancellationToken cancellationToken)
    {
        if ((index & 4095) == 0)
        {
            ScanGate.Wait(cancellationToken);
        }
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
