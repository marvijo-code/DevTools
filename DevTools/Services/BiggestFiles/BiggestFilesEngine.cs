using DevTools.Infrastructure;
using DevTools.Services.BiggestFiles.Ntfs;

namespace DevTools.Services.BiggestFiles;

/// <summary>Describes which engine a scan will use and why.</summary>
public sealed record EngineSelection(bool UseMft, string EngineName, string Reason);

/// <summary>
/// Facade that picks the fastest available scan engine: the raw-MFT reader when the process is
/// elevated and the target volume is NTFS, otherwise a parallel directory enumeration. If the MFT
/// path throws, it logs and transparently falls back so the user always gets a result.
/// </summary>
public sealed class BiggestFilesEngine
{
    private readonly ParallelFileScanner _fallbackScanner = new();
    private readonly MftScanner _mftScanner = new();

    public static EngineSelection ResolveEngine(string scanRoot, bool allowMft)
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
            reason = "Not elevated: using parallel enumeration. Restart as admin for the MFT fast path.";
        }
        else
        {
            reason = "Volume is not NTFS: using parallel enumeration.";
        }

        return new EngineSelection(useMft, useMft ? "NTFS MFT" : "Parallel enumeration", reason);
    }

    public async Task<BiggestFilesResult> ScanAsync(
        BiggestFilesRequest request,
        IProgress<BiggestFilesProgress>? progress,
        CancellationToken cancellationToken)
    {
        var selection = ResolveEngine(request.ScanRoot, request.AllowMft);
        AppLog.Info($"BiggestFilesEngine scanning '{request.ScanRoot}' with {selection.EngineName}. {selection.Reason}");

        if (selection.UseMft)
        {
            try
            {
                return await _mftScanner.ScanAsync(request, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Error("MFT scan failed; falling back to parallel enumeration.", ex);
                var fallback = await _fallbackScanner.ScanAsync(request, progress, cancellationToken).ConfigureAwait(false);
                return fallback with { Warning = $"MFT scan failed ({ex.Message}); used fallback scanner." };
            }
        }

        return await _fallbackScanner.ScanAsync(request, progress, cancellationToken).ConfigureAwait(false);
    }
}
