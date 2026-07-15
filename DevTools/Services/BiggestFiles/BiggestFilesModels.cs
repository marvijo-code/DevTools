namespace DevTools.Services.BiggestFiles;

/// <summary>A single file discovered during a scan, ranked by <see cref="SizeBytes"/>.</summary>
public sealed record FileSizeHit(
    string FullPath,
    long SizeBytes,
    long AllocatedBytes,
    DateTimeOffset? ModifiedUtc);

/// <summary>Parameters for a biggest-files scan.</summary>
public sealed record BiggestFilesRequest(string ScanRoot, int TopCount, bool AllowMft = true);

/// <summary>Streaming progress snapshot published during a scan.</summary>
public sealed record BiggestFilesProgress(
    string Phase,
    long FilesSeen,
    long BytesSeen,
    TimeSpan Elapsed,
    double FilesPerSecond,
    IReadOnlyList<FileSizeHit> CurrentTop,
    bool IsCompleted);

/// <summary>Final result of a scan.</summary>
public sealed record BiggestFilesResult(
    IReadOnlyList<FileSizeHit> Top,
    long FilesSeen,
    long BytesSeen,
    TimeSpan Elapsed,
    string EngineName,
    string? Warning);
