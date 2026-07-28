using DevTools.Models;

namespace DevTools.Services.Resources;

/// <summary>
/// Reads free space on every fixed volume. Kept separate from the process sampler because a volume that
/// goes offline throws per drive, and one bad drive must not cost the whole tick.
/// </summary>
public sealed class VolumeSampler
{
    public IReadOnlyList<VolumeUsage> Sample()
    {
        var volumes = new List<VolumeUsage>(4);

        DriveInfo[] drives;

        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return [];
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady || drive.TotalSize <= 0)
                {
                    continue;
                }

                volumes.Add(new VolumeUsage(
                    drive.Name,
                    string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A drive that disappears mid-enumeration is not worth failing the sample over.
            }
        }

        return volumes
            .OrderBy(volume => volume.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
