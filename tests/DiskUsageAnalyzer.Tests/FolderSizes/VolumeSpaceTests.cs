using System.Buffers.Binary;
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
    [InlineData(700L, true, "10 more used bytes")]
    [InlineData(450L, true, "240 fewer used bytes")]
    [InlineData(690L, true, null)]
    [InlineData(700L, false, null)]
    [InlineData(null, false, null)]
    public void DriveSummary_ReconcilesBitmapClustersAndReportsCapacityDifferences(
        long? windowsUsed, bool volumeWide, string? expectedDifference)
    {
        var drive = new DriveDescriptor(@"C:\", "Test", "NTFS",
            windowsUsed is long used ? new VolumeSpace(1000, 1000 - used, used, 0, 0, volumeWide) : null);
        var tree = new FolderTree(drive.RootPath, [-1], [""], [400], [1], "NTFS", TimeSpan.Zero,
            [414], new NtfsRecordAllocation(30, 20, new NtfsBitmapAllocation(690, 440, 4, 20UL)));
        var results = new Dictionary<string, DriveScanResult>
        {
            [drive.RootPath] = new DriveScanResult(drive, tree, "NTFS", "")
        };

        var note = Assert.Single(DiskUsage.BuildDriveSummarySection([drive], results).Entries).Note;

        Assert.Contains("The NTFS bitmap reported 690 allocated bytes.", note);
        Assert.Contains("440 appear in MFT runs and 250 have no matching run.", note);
        Assert.Contains("4 bytes in MFT runs were marked free", note);
        Assert.Contains("20 bytes of repeated cluster references", note);
        Assert.DoesNotContain("used bytes remain unattributed", note);
        Assert.DoesNotContain("Observed allocation exceeded", note);
        if (windowsUsed is not null)
        {
            Assert.Contains("414 bytes referenced by folder records, 30 by reserved NTFS records, and 20 by unlinked NTFS records.", note);
        }

        if (expectedDifference is null)
        {
            Assert.DoesNotContain("used bytes than the bitmap pass", note);
        }
        else
        {
            Assert.Contains($"Windows volume counters reported {expectedDifference} than the bitmap pass.", note);
        }
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

    [Fact]
    public void DriveSummary_ReconcilesPartitionRangesAndMatchedVolumeCapacity()
    {
        var logicalPartition = new DiskRange(200, 300);
        var otherPartition = new DiskRange(600, 100);
        var capacity = DiskCapacityInventory.Reconcile(0, 900, [new DiskRange(100, 500), logicalPartition, otherPartition],
        [
            new VolumeDiskExtent(0, logicalPartition, 280),
            new VolumeDiskExtent(0, logicalPartition, 280),
            new VolumeDiskExtent(1, otherPartition, 90)
        ]);

        Assert.Equal(new DiskCapacity(0, 900, 600, 300, 280), capacity);
        var summary = DiskUsage.BuildDriveSummarySection([], new Dictionary<string, DriveScanResult>(), [capacity]);
        Assert.Contains("Disk 0 has 900 bytes.", summary.Summary);
        Assert.Contains("280 bytes in matched filesystem capacities", summary.Summary);
        Assert.Contains("20 inside matched partitions beyond their filesystem capacities", summary.Summary);
        Assert.Contains("300 in other partition ranges and 300 outside partition ranges.", summary.Summary);
    }

    [Fact]
    public void PhysicalDiskCapacity_OmitsAmbiguousVolumeMappings()
    {
        var partitions = new HashSet<DiskRange> { new(100, 500), new(200, 300) };
        var extents = new VolumeDiskExtent[]
        {
            new(0, new DiskRange(100, 500), 450),
            new(0, new DiskRange(200, 300), 290)
        };

        Assert.Equal(new DiskCapacity(0, 900, 500, 0, 0), DiskCapacityInventory.Reconcile(0, 900, partitions, extents));
    }

    [Fact]
    public void ParseVolumeExtents_ReportsAllDisksAndRejectsIncompleteReplies()
    {
        var reply = new byte[8 + 2 * 24];
        BinaryPrimitives.WriteInt32LittleEndian(reply, 2);
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(8), 3);
        BinaryPrimitives.WriteInt64LittleEndian(reply.AsSpan(16), 10);
        BinaryPrimitives.WriteInt64LittleEndian(reply.AsSpan(24), 20);
        BinaryPrimitives.WriteInt32LittleEndian(reply.AsSpan(32), 7);
        BinaryPrimitives.WriteInt64LittleEndian(reply.AsSpan(40), 100);
        BinaryPrimitives.WriteInt64LittleEndian(reply.AsSpan(48), 200);

        Assert.Equal([(3, new DiskRange(10, 20)), (7, new DiskRange(100, 200))],
            DiskCapacityInventory.ParseVolumeExtents(reply));
        Assert.Empty(DiskCapacityInventory.ParseVolumeExtents(reply.AsSpan(0, reply.Length - 1)));
        BinaryPrimitives.WriteInt64LittleEndian(reply.AsSpan(48), long.MaxValue);
        Assert.Empty(DiskCapacityInventory.ParseVolumeExtents(reply));
    }

    [Fact]
    public void ReadPartitionRanges_RejectsTruncatedAndOutOfDiskPartitions()
    {
        var layout = new byte[48 + 2 * 144];
        BinaryPrimitives.WriteInt32LittleEndian(layout.AsSpan(4), 2);
        BinaryPrimitives.WriteInt64LittleEndian(layout.AsSpan(48 + 8), 100);
        BinaryPrimitives.WriteInt64LittleEndian(layout.AsSpan(48 + 16), 500);

        var ranges = DiskCapacityInventory.ReadPartitionRanges(layout, 900);
        Assert.Equal(new DiskRange(100, 500), Assert.Single(Assert.IsType<HashSet<DiskRange>>(ranges)));
        Assert.Null(DiskCapacityInventory.ReadPartitionRanges(layout.AsSpan(0, layout.Length - 1), 900));
        Assert.Null(DiskCapacityInventory.ReadPartitionRanges(layout, 599));
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
