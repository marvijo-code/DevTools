using System.Diagnostics;
using DevTools.Services.BiggestFiles;
using DevTools.Services.FolderSizes;

namespace DevTools.Services.BiggestFiles.Ntfs;

/// <summary>
/// Fast-path scan engine that reads the NTFS Master File Table directly from a raw volume handle
/// and reconstructs file sizes and paths in memory. Requires elevation and an NTFS volume.
/// </summary>
public sealed class MftScanner
{
    private const string EngineName = "NTFS MFT";

    public async Task<BiggestFilesResult> ScanAsync(
        BiggestFilesRequest request,
        IProgress<BiggestFilesProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(request.ScanRoot);
        var stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        using var diskScope = ScanGate.ForDisks(DiskCapacityInventory.ReadBackingDiskNumbers(Path.GetPathRoot(root)));
        using var scan = await MftTableLoader.EnterScanAsync(cancellationToken).ConfigureAwait(false);

        var result = await MftTableLoader.UseVolumeAsync(
            root,
            (parsed, _ , elapsed) => progress?.Report(new BiggestFilesProgress(
                "Reading MFT", parsed, 0, elapsed,
                ComputeRate(parsed, elapsed), Array.Empty<FileSizeHit>(), IsCompleted: false)),
            stopwatch,
            cancellationToken,
            keepFileNames: true,
            requestedLargestFiles: request.TopCount,
            table => RankFiles(table, root, request.TopCount, stopwatch, cancellationToken)).ConfigureAwait(false);
        progress?.Report(new BiggestFilesProgress(
            "Done", result.FilesSeen, result.BytesSeen, result.Elapsed,
            ComputeRate(result.FilesSeen, result.Elapsed), result.Top, IsCompleted: true));

        return result;
    }

    internal static BiggestFilesResult RankFiles(MftRecordTable table, string root, int topCount, Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(topCount, 1);

        // Ancestry filter for subtree scans (volume-root scans include everything).
        var isSubtree = !string.Equals(TrimTrailingSeparators(root), TrimTrailingSeparators(table.VolumeRoot), StringComparison.OrdinalIgnoreCase);
        var targetRecord = isSubtree ? table.ResolveDirectoryRecord(root) : (uint?)null;
        if (isSubtree && targetRecord is null)
        {
            throw new DirectoryNotFoundException($"Could not resolve '{root}' in the MFT.");
        }

        var ancestry = targetRecord is null ? null : new byte[table.RecordCount];

        var top = new PriorityQueue<int, long>(topCount);
        var threshold = long.MinValue;
        long filesSeen = 0;
        long bytesSeen = 0;

        for (var index = (int)MftRecordTable.FirstUserRecordNumber; index < table.RecordCount; index++)
        {
            if ((index & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // Each file is visited once. Cache ancestry at its parent for reuse by all sibling files.
            if (!table.IsNamedFile(index) ||
                (targetRecord is uint target && !table.IsUnder((int)table.Parent[index], target, ancestry!)))
            {
                continue;
            }

            var size = table.RealSize[index];
            filesSeen++;
            bytesSeen += size;
            if (size <= threshold)
            {
                continue;
            }

            if (top.Count < topCount)
            {
                top.Enqueue(index, size);
            }
            else
            {
                top.EnqueueDequeue(index, size);
            }

            if (top.Count == topCount)
            {
                top.TryPeek(out _, out threshold);
            }
        }

        var winners = top.UnorderedItems.ToList();
        var details = table.LoadRankedFileDetails(winners.Select(winner => winner.Element), cancellationToken);
        winners.Sort(static (left, right) => right.Priority.CompareTo(left.Priority));
        var pathCache = new Dictionary<uint, string>();
        var hits = new List<FileSizeHit>(winners.Count);
        foreach (var (index, size) in winners)
        {
            var file = details is null
                ? new MftRankedFileDetails(table.Names[index] ?? "<unknown>", table.AllocSize[index], table.LastWrite[index])
                : details[index];
            var fullPath = table.BuildPath(index, pathCache, file.Name);
            hits.Add(new FileSizeHit(fullPath, size, file.AllocatedBytes, ToModified(file.LastWriteFileTime)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        stopwatch.Stop();
        return new BiggestFilesResult(hits, filesSeen, bytesSeen, stopwatch.Elapsed, EngineName, Warning: null);
    }

    private static DateTimeOffset? ToModified(long fileTime)
    {
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTime);
        }
        catch
        {
            return null;
        }
    }

    private static double ComputeRate(long items, TimeSpan elapsed)
    {
        return elapsed.TotalSeconds <= 0 ? 0 : items / elapsed.TotalSeconds;
    }

    private static string TrimTrailingSeparators(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
