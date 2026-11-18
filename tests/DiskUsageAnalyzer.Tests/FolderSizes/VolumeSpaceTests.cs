using DiskUsage = DevTools.Services.DiskUsageAnalyzer;
using DevTools.Services.BiggestFiles.Ntfs;
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
    [InlineData(true, false, null, true, "Folder scan pending.")]
    [InlineData(false, false, null, true, "Folder scan pending.")]
    [InlineData(false, true, null, true, "700 used bytes have no folder attribution.")]
    [InlineData(true, true, null, true, "700 used bytes have no folder attribution.")]
    [InlineData(true, true, 500L, true, "200 used bytes remain unattributed.")]
    [InlineData(true, true, 800L, true, "Observed allocation exceeded reported usage by 100 bytes")]
    [InlineData(true, true, 500L, false, "Volume-wide usage is unavailable for reconciliation.")]
    [InlineData(false, true, null, false, "Folder allocation is unavailable.")]
    public void DriveSummary_DistinguishesPendingAndUnattributedUsage(
        bool hasDriveLetter, bool scanned, long? allocatedBytes, bool volumeWide, string expectedNote)
    {
        var space = new VolumeSpace(1_000, 300, 700, 0, 0, volumeWide);
        var drive = new DriveDescriptor(@"C:\", "Test", "NTFS", space, hasDriveLetter);
        var results = new Dictionary<string, DriveScanResult>();
        if (scanned)
        {
            var tree = new FolderTree(drive.RootPath, [-1], [""], [9_000], [2], "Fixture", TimeSpan.Zero,
                allocatedBytes is long allocated ? [allocated] : null);
            results.Add(drive.RootPath, new DriveScanResult(drive, tree, "Fixture", ""));
        }

        var summary = DiskUsage.BuildDriveSummarySection([drive], results);
        var note = Assert.Single(summary.Entries).Note;

        Assert.Contains(expectedNote, note);
        if (allocatedBytes.HasValue)
        {
            Assert.Contains($"{allocatedBytes:N0} bytes of allocation attributed by the scan.", note);
        }
    }

    [Fact]
    public void DriveSummary_ReconcilesFolderAllocationReservedRecordsAndUnlinkedRecords()
    {
        var drive = new DriveDescriptor(@"C:\", "Test", "NTFS",
            new VolumeSpace(1_000, 300, 700, 0, 0, IsVolumeWide: true));
        var tree = new FolderTree(drive.RootPath, [-1], [""], [400], [1], "NTFS", TimeSpan.Zero,
            [100], new NtfsRecordAllocation(150, 200));
        var results = new Dictionary<string, DriveScanResult>
        {
            [drive.RootPath] = new DriveScanResult(drive, tree, "NTFS", "")
        };

        var note = Assert.Single(DiskUsage.BuildDriveSummarySection([drive], results).Entries).Note;

        Assert.Contains("100 bytes assigned to folders", note);
        Assert.Contains("150 in reserved NTFS records", note);
        Assert.Contains("200 in NTFS records without a verified folder", note);
        Assert.Contains("250 used bytes remain unattributed", note);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DriveSummary_ReportsBitmapSeparatelyFromWindowsCapacityCounters(bool hasSpace)
    {
        var drive = new DriveDescriptor(@"C:\", "Test", "NTFS",
            hasSpace ? new VolumeSpace(1000, 300, 700, 0, 0, IsVolumeWide: true) : null);
        var tree = new FolderTree(drive.RootPath, [-1], [""], [400], [1], "NTFS", TimeSpan.Zero,
            [444], new NtfsRecordAllocation(0, 0, new NtfsBitmapAllocation(690, 440, 4)));
        var results = new Dictionary<string, DriveScanResult>
        {
            [drive.RootPath] = new DriveScanResult(drive, tree, "NTFS", "")
        };

        var note = Assert.Single(DiskUsage.BuildDriveSummarySection([drive], results).Entries).Note;

        Assert.Contains("The NTFS bitmap reported 690 allocated bytes.", note);
        Assert.Contains("440 appear in MFT runs and 250 have no matching run.", note);
        Assert.Contains("4 bytes in MFT runs were marked free", note);
    }

    [Fact]
    public void DriveSummary_AccountsForUsedBytesWhenTheScanFails()
    {
        var drive = new DriveDescriptor(@"C:\", "Test", "NTFS",
            new VolumeSpace(1_000, 300, 700, 0, 0, IsVolumeWide: true));
        var results = new Dictionary<string, DriveScanResult>
        {
            [drive.RootPath] = new DriveScanResult(drive, null, "None", "Access denied.")
        };

        var summary = DiskUsage.BuildDriveSummarySection([drive], results);

        Assert.Contains("Access denied. 700 used bytes have no folder attribution.", Assert.Single(summary.Entries).Note);
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
