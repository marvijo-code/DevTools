using DevTools.Services.BiggestFiles;

namespace DevTools.Services.FolderSizes;

/// <summary>
/// Facade that picks the fastest available folder-size engine: the raw-MFT reader when the process
/// is elevated and the target volume is NTFS. Callers check <see cref="ResolveEngine"/> first and
/// supply their own slower measurement path when the MFT fast path is unavailable.
/// </summary>
public sealed class FolderSizeEngine
{
    private readonly MftFolderSizeScanner _mftScanner = new();

    public static FolderSizeEngineSelection ResolveEngine(string scanRoot, bool allowMft = true)
    {
        var elevated = ElevationService.IsProcessElevated();
        var ntfs = ElevationService.IsNtfsVolume(scanRoot);
        var useMft = allowMft && elevated && ntfs;

        string reason;
        if (useMft)
        {
            reason = "Elevated + NTFS: reading the Master File Table directly.";
        }
        else if (!allowMft)
        {
            reason = "MFT fast path disabled for this scan.";
        }
        else if (!elevated)
        {
            reason = "Not elevated: measuring folders the slower way. Restart as admin for the instant NTFS scan.";
        }
        else
        {
            reason = "Volume is not NTFS: measuring folders the slower way.";
        }

        return new FolderSizeEngineSelection(useMft, useMft ? "NTFS MFT" : "Folder measurement", reason);
    }

    public Task<FolderSizeResult> ScanAsync(
        FolderSizeRequest request,
        IProgress<FolderSizeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var selection = ResolveEngine(request.ScanRoot, request.AllowMft);
        if (!selection.UseMft)
        {
            throw new InvalidOperationException(
                "The MFT folder-size engine is not available for this scan root. Call ResolveEngine first.");
        }

        return _mftScanner.ScanAsync(request, progress, cancellationToken);
    }
}
