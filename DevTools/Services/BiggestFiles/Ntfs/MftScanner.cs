using System.Diagnostics;
using DevTools.Services.BiggestFiles;

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

        var table = await MftTableLoader.LoadAsync(
            root,
            (parsed, _ , elapsed) => progress?.Report(new BiggestFilesProgress(
                "Reading MFT", parsed, 0, elapsed,
                ComputeRate(parsed, elapsed), Array.Empty<FileSizeHit>(), IsCompleted: false)),
            stopwatch,
            cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        // Ancestry filter for subtree scans (volume-root scans include everything).
        var isSubtree = !string.Equals(TrimTrailingSeparators(root), TrimTrailingSeparators(table.VolumeRoot), StringComparison.OrdinalIgnoreCase);
        var targetRecord = isSubtree ? table.ResolveDirectoryRecord(root) : (uint?)null;
        if (isSubtree && targetRecord is null)
        {
            throw new DirectoryNotFoundException($"Could not resolve '{root}' in the MFT.");
        }

        var ancestry = targetRecord is null ? null : new byte[table.RecordCount];

        var topN = new TopNAccumulator<int>(request.TopCount);
        long filesSeen = 0;
        long bytesSeen = 0;

        for (var index = 0; index < table.RecordCount; index++)
        {
            if (!table.IsNamedFile(index))
            {
                continue;
            }

            if (index < MftRecordTable.FirstUserRecordNumber)
            {
                continue;
            }

            var size = table.RealSize[index];
            filesSeen++;
            bytesSeen += size;

            if (targetRecord is not null && !table.IsUnder(index, targetRecord.Value, ancestry!))
            {
                continue;
            }

            topN.TryAdd(size, index);
        }

        var winners = topN.Snapshot();
        var pathCache = new Dictionary<uint, string>();
        var hits = new List<FileSizeHit>(winners.Count);
        foreach (var (size, index) in winners)
        {
            var fullPath = table.BuildPath(index, pathCache);
            hits.Add(new FileSizeHit(fullPath, size, table.AllocSize[index], ToModified(table.LastWrite[index])));
        }

        stopwatch.Stop();

        progress?.Report(new BiggestFilesProgress(
            "Done", filesSeen, bytesSeen, stopwatch.Elapsed,
            ComputeRate(table.RecordCount, stopwatch.Elapsed), hits, IsCompleted: true));

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
