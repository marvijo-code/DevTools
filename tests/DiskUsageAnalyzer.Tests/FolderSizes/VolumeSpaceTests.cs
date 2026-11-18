using DevTools.Services.FolderSizes;
using Xunit;

namespace DiskUsageAnalyzer.Tests.FolderSizes;

public sealed class VolumeSpaceTests
{
    [Fact]
    public void FixedVolumeInventory_UsesOneEntryForTheSystemDrive()
    {
        var path = Path.GetPathRoot(Environment.SystemDirectory)!;
        var identity = WindowsVolumePaths.GetVolumeName(path);
        Assert.False(string.IsNullOrWhiteSpace(identity));

        var all = DriveUsageService.GetFixedDrives(includeVolumesWithoutDriveLetters: true);

        var systemDrive = Assert.Single(all, drive => drive.RootPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        Assert.True(systemDrive.HasDriveLetter);
        Assert.DoesNotContain(all, drive => drive.RootPath.Equals(identity, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(7_000, 0)]
    [InlineData(6_999, 1)]
    [InlineData(7_001, -1)]
    public void UnclassifiedBytes_PreservesEveryByteAndReportedOverlap(long used, long expected)
    {
        var space = new VolumeSpace(10_000, 2_700, used, 200, 100, IsVolumeWide: true);

        Assert.Equal(expected, space.UnclassifiedBytes);
        Assert.Equal(space.TotalBytes,
            space.FreeBytes + space.UsedBytes + space.ReservedBytes + space.PoolUnavailableBytes + space.UnclassifiedBytes);
    }
}
