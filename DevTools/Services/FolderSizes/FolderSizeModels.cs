namespace DevTools.Services.FolderSizes;

/// <summary>A directory ranked by the total size of the files beneath it.</summary>
public sealed record FolderSizeHit(
    string FullPath,
    long SizeBytes,
    IReadOnlyList<FolderSizeHit> Children);

/// <summary>Parameters for a largest-folders scan.</summary>
public sealed record FolderSizeRequest(
    string ScanRoot,
    int TopCount,
    int ChildrenPerFolder = 10,
    bool AllowMft = true);

/// <summary>Streaming progress snapshot published during a folder-size scan.</summary>
public sealed record FolderSizeProgress(
    string Phase,
    long RecordsParsed,
    long RecordCount,
    TimeSpan Elapsed,
    IReadOnlyList<FolderSizeHit> CurrentTop,
    bool IsCompleted);

/// <summary>Final result of a folder-size scan.</summary>
public sealed record FolderSizeResult(
    IReadOnlyList<FolderSizeHit> Top,
    long RecordsParsed,
    TimeSpan Elapsed,
    string EngineName,
    string? Warning);

/// <summary>Describes which engine a folder-size scan will use and why.</summary>
public sealed record FolderSizeEngineSelection(bool UseMft, string EngineName, string Reason);

/// <summary>Progress of an all-folders scan of one drive.</summary>
public sealed record FolderScanProgress(
    string DriveRoot,
    string EngineName,
    string Message,
    long ItemsSeen,
    TimeSpan Elapsed);
