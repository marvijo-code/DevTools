using System.IO.Enumeration;
using DevTools.Services.BiggestFiles;

namespace DevTools.Services.FolderSizes;

internal readonly record struct FolderFile(string Name, string Path, long Bytes);

internal sealed record FolderFileSnapshot(IReadOnlyList<FolderFile> Files, long TotalBytes, long FileCount)
{
    internal bool Matches(long bytes, long fileCount) => TotalBytes == bytes && FileCount == fileCount;
}

internal static class FolderFileListing
{
    private static readonly EnumerationOptions ListingOptions = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        BufferSize = 16 * 1024
    };

    public static FolderFileSnapshot? ReadLargest(string folder, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var largest = new TopNAccumulator<string>(limit);
        string? directory = null;
        long totalBytes = 0;
        long fileCount = 0;
        try
        {
            if ((File.GetAttributes(folder) & System.IO.FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            var entries = new FileSystemEnumerable<(string Name, long Bytes)>(
                folder,
                (ref FileSystemEntry entry) =>
                {
                    // Capture the enumerator's full directory once. Only the winners need full file paths.
                    directory ??= entry.Directory.ToString();
                    return (entry.FileName.ToString(), Math.Max(0, entry.Length));
                },
                ListingOptions);
            entries.ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                {
                    return false;
                }

                var bytes = Math.Max(0, entry.Length);
                totalBytes = checked(totalBytes + bytes);
                fileCount++;
                return bytes > largest.Threshold;
            };

            foreach (var file in entries)
            {
                largest.TryAdd(file.Bytes, file.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        {
            return null;
        }

        var files = directory is null
            ? []
            : largest.Snapshot().Select(entry =>
                new FolderFile(entry.Item, Path.Join(directory, entry.Item), entry.SizeBytes)).ToArray();
        return new FolderFileSnapshot(files, totalBytes, fileCount);
    }
}
