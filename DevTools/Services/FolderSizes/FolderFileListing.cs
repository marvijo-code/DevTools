using System.IO.Enumeration;
using DevTools.Services.BiggestFiles;

namespace DevTools.Services.FolderSizes;

internal readonly record struct FolderFile(string Name, string Path, long Bytes);

internal static class FolderFileListing
{
    public static IReadOnlyList<FolderFile> ReadLargest(string folder, int limit)
    {
        var largest = new TopNAccumulator<FolderFile>(limit);
        try
        {
            if ((File.GetAttributes(folder) & System.IO.FileAttributes.ReparsePoint) != 0)
            {
                return [];
            }

            var entries = new FileSystemEnumerable<FolderFile>(
                folder,
                (ref FileSystemEntry entry) => new(entry.FileName.ToString(), entry.ToFullPath(), entry.Length),
                new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 });
            entries.ShouldIncludePredicate = (ref FileSystemEntry entry) => !entry.IsDirectory && entry.Length > largest.Threshold;

            foreach (var file in entries)
            {
                largest.TryAdd(file.Bytes, file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The browser still displays any unlisted bytes from its folder snapshot.
        }

        return largest.Snapshot().Select(entry => entry.Item).ToArray();
    }
}
