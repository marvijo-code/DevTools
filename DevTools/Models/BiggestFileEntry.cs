using DevTools.Infrastructure;
using DevTools.Services.BiggestFiles;

namespace DevTools.Models;

/// <summary>A single ranked file shown in the Biggest Files list.</summary>
public sealed class BiggestFileEntry : ObservableObject
{
    private bool _existsOnDisk = true;

    public BiggestFileEntry(string path, long sizeBytes, long allocatedBytes, DateTimeOffset? modifiedUtc)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(FileName))
        {
            FileName = path;
        }

        DirectoryPath = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        SizeBytes = sizeBytes;
        Usage = ByteFormatter.Format(sizeBytes);
        Note = BuildNote(sizeBytes, allocatedBytes, modifiedUtc);
    }

    public string Path { get; }

    public string FileName { get; }

    public string DirectoryPath { get; }

    public long SizeBytes { get; }

    public string Usage { get; }

    public string Note { get; }

    public string CopyText => Path;

    public bool ExistsOnDisk
    {
        get => _existsOnDisk;
        set => SetProperty(ref _existsOnDisk, value);
    }

    public static BiggestFileEntry FromHit(FileSizeHit hit)
    {
        return new BiggestFileEntry(hit.FullPath, hit.SizeBytes, hit.AllocatedBytes, hit.ModifiedUtc);
    }

    private static string BuildNote(long sizeBytes, long allocatedBytes, DateTimeOffset? modifiedUtc)
    {
        var parts = new List<string>(2);
        if (modifiedUtc is not null)
        {
            parts.Add($"Modified {modifiedUtc.Value.LocalDateTime:yyyy-MM-dd}");
        }

        // Surface on-disk allocation only when it differs meaningfully (compressed/sparse/resident).
        if (allocatedBytes > 0 && Math.Abs(allocatedBytes - sizeBytes) > 4096)
        {
            parts.Add($"on disk {ByteFormatter.Format(allocatedBytes)}");
        }

        return string.Join(" · ", parts);
    }
}
