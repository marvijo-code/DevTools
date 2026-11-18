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
        Assert.Equal(identity, systemDrive.VolumeName);
        Assert.Contains(WindowsVolumePaths.GetAccessPaths(identity!), accessPath => accessPath.Equals(path, StringComparison.OrdinalIgnoreCase));
        var aliases = WindowsVolumePaths.GetMountAliases(all, CancellationToken.None);
        var resolved = Assert.IsType<ResolvedVolumeMount>(WindowsVolumePaths.ResolveMountAlias(identity + "Windows", aliases));
        Assert.Equal(new VolumeMountAlias(identity!, path, identity!), resolved.Mount);
        Assert.Equal(Path.Combine(path, "Windows"), resolved.Path);
        Assert.DoesNotContain(all, drive => drive.RootPath.Equals(identity, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ContainingVolumeRoot_ResolvesDriveLetterAndGuidPaths()
    {
        var fixture = Directory.CreateTempSubdirectory("devtools-volume-root-");
        try
        {
            var root = fixture.Root.FullName;
            var volume = Assert.IsType<string>(WindowsVolumePaths.GetVolumeName(root));
            var leaf = Path.Combine(fixture.FullName, "missing");
            var byLetter = Assert.IsType<string>(WindowsVolumePaths.GetContainingVolumeRoot(leaf, root));
            var guidLeaf = Path.Combine(volume, Path.GetRelativePath(root, leaf));
            var byGuid = Assert.IsType<string>(WindowsVolumePaths.GetContainingVolumeRoot(guidLeaf, volume));

            Assert.Equal(volume, WindowsVolumePaths.GetVolumeName(byLetter), ignoreCase: true);
            Assert.Equal(volume, WindowsVolumePaths.GetVolumeName(byGuid), ignoreCase: true);
            Assert.Equal(root, WindowsVolumePaths.GetContainingVolumeRoot(root, root));
            Assert.Equal(volume, WindowsVolumePaths.GetContainingVolumeRoot(volume, volume));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Theory]
    [InlineData(@"C:\Mount\Disk", @"D:\")]
    [InlineData(@"c:/mount/disk/one", @"D:\one")]
    [InlineData(@"C:\Mount\Disk\Nested\one", @"E:\one")]
    [InlineData(@"C:\Mount\Disk-old\one", null)]
    [InlineData(@"C:\Mount\Disk\Nested-old\one", @"D:\Nested-old\one")]
    public void ResolveMountAlias_PrefersTheDeepestCompleteMountPath(string path, string? expected)
    {
        VolumeMountAlias[] aliases =
        [
            new(@"C:\Mount\Disk\", @"D:\", @"\\?\Volume{96175894-8c86-41c9-b1d5-6fa9ddba8c34}\"),
            new(@"C:\Mount\Disk\Nested\", @"E:\", @"\\?\Volume{d301cfcd-d09b-430b-90de-f44dd9e5610e}\")
        ];

        Assert.Equal(expected, WindowsVolumePaths.ResolveMountAlias(path, aliases)?.Path);
    }

    [Fact]
    public async Task VolumeScheduler_OverlapsSeparateDisksAndRunsUnknownDisksAlone()
    {
        int[][] disks = [[0], [0], [1], [1], []];
        var active = new HashSet<int>();
        var observedOrder = new List<int>();
        var diskOneAdvanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        async Task<DriveScanResult> ScanAsync(int index, int workerShare, CancellationToken token)
        {
            lock (active)
            {
                Assert.All(active, other =>
                {
                    Assert.NotEmpty(disks[index]);
                    Assert.NotEmpty(disks[other]);
                    Assert.Empty(disks[index].Intersect(disks[other]));
                });
                active.Add(index);
                observedOrder.Add(index);
            }

            try
            {
                if (index == 0)
                {
                    Assert.Equal(2, workerShare);
                    // Disk 1 must be able to start its second volume while disk 0 is still busy.
                    await diskOneAdvanced.Task.WaitAsync(token);
                }
                else if (index == 3)
                {
                    Assert.Equal(2, workerShare);
                    diskOneAdvanced.TrySetResult();
                }
                else if (disks[index].Length == 0)
                {
                    Assert.Equal(1, workerShare);
                }

                return VolumeResult(index);
            }
            finally
            {
                lock (active)
                {
                    active.Remove(index);
                }
            }
        }

        var results = new List<int>();
        await foreach (var (index, result) in DiskUsage.ScanVolumesAsync(disks, 2, ScanAsync, timeout.Token))
        {
            Assert.Equal(VolumeResult(index), result);
            results.Add(index);
        }

        Assert.Equal([0, 2, 3, 1, 4], observedOrder);
        Assert.Equal([0, 1, 2, 3, 4], results.Order().ToArray());
        Assert.Empty(active);
    }

    [Fact]
    public async Task VolumeScheduler_OverlapsSpannedVolumesOnlyWithDisjointDisks()
    {
        int[][] disks = [[0, 1], [1], [2, 3], []];
        var started = new List<int>();
        var separateVolumeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        async Task<DriveScanResult> ScanAsync(int index, int workerShare, CancellationToken token)
        {
            started.Add(index);
            if (index == 0)
            {
                Assert.Equal(3, workerShare);
                await separateVolumeStarted.Task.WaitAsync(token);
            }
            else if (index == 2)
            {
                Assert.Equal(3, workerShare);
                separateVolumeStarted.SetResult();
            }

            return VolumeResult(index);
        }

        var completed = new List<int>();
        await foreach (var (index, _) in DiskUsage.ScanVolumesAsync(disks, 4, ScanAsync, deadline.Token))
        {
            completed.Add(index);
        }

        Assert.Equal([0, 2, 1, 3], started);
        Assert.Equal([0, 1, 2, 3], completed.Order().ToArray());
    }

    [Fact]
    public async Task VolumeScheduler_UsesTheFullWorkerBudgetWhenRemainingSpansOverlap()
    {
        int[][] disks = [[0, 1], [1, 2]];
        Task<DriveScanResult> ScanAsync(int index, int workerShare, CancellationToken token)
        {
            Assert.Equal(1, workerShare);
            return Task.FromResult(VolumeResult(index));
        }

        var completed = new List<int>();
        await foreach (var (index, _) in DiskUsage.ScanVolumesAsync(disks, 3, ScanAsync, CancellationToken.None))
        {
            completed.Add(index);
        }

        Assert.Equal([0, 1], completed);
    }

    [Fact]
    public async Task VolumeScheduler_GivesTheLastDiskItsFullWorkerBudget()
    {
        int[][] disks = [[0], [1], [2], [0], [0], []];
        var workerShares = new int[disks.Length];
        var finishFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        async Task<DriveScanResult> ScanAsync(int index, int workerShare, CancellationToken token)
        {
            workerShares[index] = workerShare;
            if (index == 0)
            {
                await finishFirst.Task.WaitAsync(token);
            }

            return VolumeResult(index);
        }

        var otherDisksCompleted = 0;
        await foreach (var (index, _) in DiskUsage.ScanVolumesAsync(disks, 3, ScanAsync, timeout.Token))
        {
            if (index is 1 or 2 && ++otherDisksCompleted == 2)
            {
                finishFirst.SetResult();
            }
        }

        Assert.Equal([3, 3, 3, 1, 1, 1], workerShares);
    }

    [Fact]
    public async Task VolumeScheduler_CancelsAndJoinsBothRunningWalks()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var stopped = 0;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        async Task<DriveScanResult> ScanAsync(int index, int workerShare, CancellationToken token)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return VolumeResult(index);
            }
            finally
            {
                Interlocked.Increment(ref stopped);
            }
        }

        async Task ConsumeAsync()
        {
            await foreach (var _ in DiskUsage.ScanVolumesAsync([[0], [1], [1]], 2, ScanAsync, cancellation.Token))
            {
                throw new InvalidOperationException("A cancelled scan must not publish a result.");
            }
        }

        var consume = ConsumeAsync();
        await bothStarted.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consume);
        Assert.Equal(2, started);
        Assert.Equal(2, stopped);
    }

    private static DriveScanResult VolumeResult(int index) => new(
        new DriveDescriptor($"Volume {index}", string.Empty, "NTFS", null), null, "test", string.Empty);

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
    public void DriveSummary_DistinguishesIncompleteListingsAndAllocation()
    {
        var drive = new DriveDescriptor(@"C:\", "Test", "NTFS",
            new VolumeSpace(1000, 300, 700, 0, 0, IsVolumeWide: true));
        var builder = new FolderTreeBuilder(drive.RootPath, trackAllocation: true);
        var partial = builder.AddFolder(@"C:\documents\protected");
        var allocationOnly = builder.AddFolder(@"C:\data\partial");
        builder.AddFilesToFolder(partial, 32, 1, 64);
        builder.AddFilesToFolder(allocationOnly, 64, 1, 128);
        builder.MarkIncompleteDirectory(partial);
        builder.MarkIncompleteAllocation(allocationOnly);
        builder.MarkIncompleteAllocation(allocationOnly); // Several stream batches can miss metadata in the same folder.
        var tree = builder.Build("Folder walk", TimeSpan.Zero);
        var results = new Dictionary<string, DriveScanResult>
        {
            [drive.RootPath] = new DriveScanResult(drive, tree, tree.EngineName, "")
        };

        Assert.True(tree.HasIncompleteScan(partial));
        Assert.True(tree.HasIncompleteScan(tree.Find(@"C:\documents")));
        Assert.True(tree.HasIncompleteScan(0));
        Assert.False(tree.HasIncompleteScan(allocationOnly));
        Assert.True(tree.HasIncompleteAllocation(allocationOnly));
        Assert.True(tree.HasIncompleteAllocation(tree.Find(@"C:\data")));
        Assert.True(tree.HasIncompleteAllocation(0));
        Assert.False(tree.HasIncompleteAllocation(partial));
        Assert.Equal(1, tree.IncompleteAllocationDirectoryCount);
        Assert.Equal(96, tree.TotalBytes);
        Assert.Equal(192, tree.GetAllocatedBytes(0));
        var note = Assert.Single(DiskUsage.BuildDriveSummarySection([drive], results).Entries).Note;
        Assert.Contains("508 used bytes remain unattributed.", note);
        Assert.Contains("Incomplete listings in 1 directory.", note);
        Assert.Contains("Incomplete allocation in 1 directory.", note);
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

        Assert.Equal(new DiskCapacity(0, 900, 600, 300, 300, 280, 300), capacity);
        var summary = DiskUsage.BuildDriveSummarySection([], new Dictionary<string, DriveScanResult>(), [capacity]);
        Assert.Contains("Disk 0 has 900 bytes.", summary.Summary);
        Assert.Contains("280 bytes in matched filesystem capacities", summary.Summary);
        Assert.Contains("20 inside matched volume extents beyond their filesystem capacities", summary.Summary);
        Assert.Contains("300 in other partition ranges and 300 outside partition ranges.", summary.Summary);
    }

    [Fact]
    public void PhysicalDiskCapacity_CountsOverlappingExtentsOnceAndOmitsAmbiguousFilesystemMappings()
    {
        var partitions = new HashSet<DiskRange> { new(100, 500), new(200, 300) };
        var extents = new VolumeDiskExtent[]
        {
            new(0, new DiskRange(100, 500), 450),
            new(0, new DiskRange(200, 300), 290)
        };

        Assert.Equal(new DiskCapacity(0, 900, 500, 500, 0, 0, 500), DiskCapacityInventory.Reconcile(0, 900, partitions, extents));
    }

    [Fact]
    public void DriveSummary_AccountsForVolumeExtentsWithoutSplittingTheirFilesystemCapacity()
    {
        var capacity = DiskCapacityInventory.Reconcile(0, 1000, [new DiskRange(50, 800)],
        [
            new VolumeDiskExtent(0, new DiskRange(100, 100), 80),
            new VolumeDiskExtent(0, new DiskRange(250, 200), null),
            new VolumeDiskExtent(0, new DiskRange(500, 100), null),
            new VolumeDiskExtent(1, new DiskRange(300, 400), null),
            new VolumeDiskExtent(0, new DiskRange(900, 50), null),
            new VolumeDiskExtent(0, new DiskRange(975, 100), null)
        ]);

        Assert.Equal(new DiskCapacity(0, 1000, 800, 450, 100, 80, 400), capacity);
        var summary = DiskUsage.BuildDriveSummarySection([], new Dictionary<string, DriveScanResult>(), [capacity]);
        Assert.Contains("80 bytes in matched filesystem capacities", summary.Summary);
        Assert.Contains("20 inside matched volume extents beyond their filesystem capacities", summary.Summary);
        Assert.Contains("350 in other volume extents, 400 in other partition ranges and 150 outside partition and volume ranges.", summary.Summary);
        Assert.Contains("Of the reported volume extents, 50 bytes are outside partition ranges.", summary.Summary);
    }

    [Fact]
    public void DriveSummary_PreservesReportedExtentsWhenPartitionRangesAreUnavailable()
    {
        var capacity = DiskCapacityInventory.Reconcile(0, 1000, null,
        [
            new VolumeDiskExtent(0, new DiskRange(50, 100), 80),
            new VolumeDiskExtent(0, new DiskRange(200, 350), null),
            new VolumeDiskExtent(0, new DiskRange(400, 200), null)
        ]);

        Assert.Equal(new DiskCapacity(0, 1000, null, 500, 100, 80, 0), capacity);
        var summary = DiskUsage.BuildDriveSummarySection([], new Dictionary<string, DriveScanResult>(), [capacity]);
        Assert.Contains("80 bytes in matched filesystem capacities", summary.Summary);
        Assert.Contains("20 inside matched volume extents beyond their filesystem capacities", summary.Summary);
        Assert.Contains("400 in other volume extents, and 500 outside reported volume extents.", summary.Summary);
        Assert.Contains("Partition ranges are unavailable.", summary.Summary);
    }

    [Fact]
    public void PhysicalDiskCapacity_CountsExtentsThatPartlyCrossPartitionRanges()
    {
        var capacity = DiskCapacityInventory.Reconcile(0, 1000, [new DiskRange(100, 200), new DiskRange(500, 100)],
        [
            new VolumeDiskExtent(0, new DiskRange(50, 125), null),
            new VolumeDiskExtent(0, new DiskRange(250, 275), null),
            new VolumeDiskExtent(0, new DiskRange(850, 50), null)
        ]);

        Assert.Equal(new DiskCapacity(0, 1000, 300, 450, 0, 0, 150), capacity);
        var summary = DiskUsage.BuildDriveSummarySection([], new Dictionary<string, DriveScanResult>(), [capacity]);
        Assert.Contains("450 in other volume extents, 150 in other partition ranges and 400 outside partition and volume ranges.", summary.Summary);
        Assert.Contains("Of the reported volume extents, 300 bytes are outside partition ranges.", summary.Summary);
    }

    [Fact]
    public void BackingDiskNumbers_ContainsEachDiskOnce()
    {
        Assert.Equal([3], DiskCapacityInventory.GetBackingDiskNumbers([
            (3, new DiskRange(10, 20)), (3, new DiskRange(50, 20))]));
        Assert.Equal([3, 7], DiskCapacityInventory.GetBackingDiskNumbers([
            (3, new DiskRange(10, 20)), (7, new DiskRange(50, 20)), (3, new DiskRange(75, 20))]));
        Assert.Empty(DiskCapacityInventory.GetBackingDiskNumbers([]));
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
