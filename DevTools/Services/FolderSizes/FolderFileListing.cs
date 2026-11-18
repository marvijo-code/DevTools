using System.IO.Enumeration;
using DevTools.Services.BiggestFiles;

namespace DevTools.Services.FolderSizes;

internal readonly record struct FolderFile(string Name, string Path, long Bytes);

internal static class FolderFileListing
{
    private static readonly EnumerationOptions ListingOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        BufferSize = 16 * 1024
    };

    public static IReadOnlyList<FolderFile> ReadLargest(string folder, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var largest = new TopNAccumulator<string>(limit);
        string? directory = null;
        try
        {
            if ((File.GetAttributes(folder) & System.IO.FileAttributes.ReparsePoint) != 0)
            {
                return [];
            }

            var entries = new FileSystemEnumerable<(string Name, long Bytes)>(
                folder,
                (ref FileSystemEntry entry) =>
                {
                    // Capture the enumerator's full directory once. Only the winners need full file paths.
                    directory ??= entry.Directory.ToString();
                    return (entry.FileName.ToString(), entry.Length);
                },
                ListingOptions);
            entries.ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return !entry.IsDirectory && entry.Length > largest.Threshold;
            };

            foreach (var file in entries)
            {
                largest.TryAdd(file.Bytes, file.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The browser still displays any unlisted bytes from its folder snapshot.
        }

        return directory is null
            ? []
            : largest.Snapshot().Select(entry =>
                new FolderFile(entry.Item, Path.Join(directory, entry.Item), entry.SizeBytes)).ToArray();
    }
}
