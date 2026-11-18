using System.Buffers.Binary;
using System.Diagnostics;
using DevTools.Services.BiggestFiles.Ntfs;
using DevTools.Services.FolderSizes;
using Xunit;

namespace DiskUsageAnalyzer.Tests.BiggestFiles;

public sealed class MftExtentMapReaderTests
{
    private const int ClusterSize = 512;
    private const ulong BaseReference = 5UL << 48;
    private static readonly NtfsVolumeData Geometry = new(ClusterSize, 1024, 32_768, 10, 200);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Read_LoadsProgressiveContinuationsThroughMappedRuns(bool nonResidentList)
    {
        var disk = BuildDisk(nonResidentList);
        var readOffsets = new List<long>();

        var extents = MftExtentMapReader.Read(Geometry, (offset, destination) =>
        {
            Assert.Equal(0, offset % ClusterSize);
            Assert.Equal(0, destination.Length % ClusterSize);
            readOffsets.Add(offset / ClusterSize);
            disk.AsSpan((int)offset, destination.Length).CopyTo(destination);
        }, CancellationToken.None);
        MftTableLoader.ValidateMftExtents(Geometry, extents);

        Assert.Equal([(10L, 3L), (30L, 1L), (40L, 4L), (55L, 56L)], extents);
        Assert.Equal(nonResidentList ? new long[] { 10, 150, 12, 30, 42 } : [10, 12, 30, 42], readOffsets);
    }

    [Fact]
    public void Read_RejectsAContinuationWhoseSequenceHasChanged()
    {
        var disk = BuildDisk(nonResidentList: false, firstSequence: 8);

        var error = Assert.Throws<IOException>(() => MftExtentMapReader.Read(Geometry,
            (offset, destination) => disk.AsSpan((int)offset, destination.Length).CopyTo(destination), CancellationToken.None));

        Assert.Contains("no longer matches", error.Message);
    }

    [Fact]
    public void Read_RejectsAContinuationOutsideTheVerifiedPrefix()
    {
        var disk = BuildDisk(nonResidentList: false, firstRecordNumber: 2);

        var error = Assert.Throws<IOException>(() => MftExtentMapReader.Read(Geometry,
            (offset, destination) => disk.AsSpan((int)offset, destination.Length).CopyTo(destination), CancellationToken.None));

        Assert.Contains("outside the mapped extents", error.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LoadVolumeAsync_ScansFilesAndAllocationAfterTheMapContinuations(bool suppliesBitmap, bool incompleteRecords)
    {
        var directory = Directory.CreateTempSubdirectory("devtools-mft-volume-");
        var path = Path.Combine(directory.FullName, "image.bin");
        try
        {
            var disk = BuildDisk(nonResidentList: true, includeOverlappingRun: true);
            if (incompleteRecords)
            {
                // The mapping list starts with a valid run and then has an invalid offset width.
                new MftRecordBuilder().NamedData([0x11, 1, 116, 0x91, 1, 0]).Build().CopyTo(disk, 99 * ClusterSize);
                var torn = new MftRecordBuilder().NamedData([0x11, 1, 117, 0]).Build();
                MftRecordBuilder.CorruptSectorTail(torn, 0);
                torn.CopyTo(disk, 101 * ClusterSize);
                var unused = new MftRecordBuilder().InUse(false).Build();
                MftRecordBuilder.CorruptSectorTail(unused, 0);
                unused.CopyTo(disk, 103 * ClusterSize);
            }

            await File.WriteAllBytesAsync(path, disk);
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var allocated = new byte[25];
            NtfsVolumeBitmapTests.SetBits(allocated, 10, 3);
            NtfsVolumeBitmapTests.SetBits(allocated, 30, 1);
            NtfsVolumeBitmapTests.SetBits(allocated, 40, 4);
            NtfsVolumeBitmapTests.SetBits(allocated, 55, 56);
            NtfsVolumeBitmapTests.SetBits(allocated, 150, 1);
            NtfsVolumeBitmapTests.SetBits(allocated, 120, 15);
            NtfsVolumeBitmapTests.SetBits(allocated, 199, 1);
            if (incompleteRecords)
            {
                NtfsVolumeBitmapTests.SetBits(allocated, 116, 2);
            }

            var bitmapRequests = 0;
            var source = await MftTableLoader.LoadVolumeAsync(handle, @"C:\", Geometry, null,
                Stopwatch.StartNew(), CancellationToken.None, keepFileNames: false,
                bitmapQuery: (start, output) =>
                {
                    bitmapRequests++;
                    return suppliesBitmap ? NtfsVolumeBitmapTests.WriteReply(start, Geometry.TotalClusters, allocated, output) : 0;
                });
            var tree = MftFolderSizeScanner.BuildTree(source, @"C:\", "fixture", Stopwatch.StartNew());

            Assert.Equal(4130, tree.TotalBytes);
            Assert.Equal(1, tree.TotalFiles);
            Assert.Equal(8704, tree.GetAllocatedBytes(tree.Find(@"C:\Folder")));
            var expectedInUse = incompleteRecords ? 83 : 81;
            var expected = new NtfsBitmapAllocation(expectedInUse * ClusterSize, 80 * ClusterSize, ClusterSize, (uint)ClusterSize);
            Assert.Equal(new NtfsRecordAllocation(65 * ClusterSize, 0, suppliesBitmap ? expected : null, incompleteRecords ? 2 : 0),
                tree.NtfsAllocation);

            var drive = new DriveDescriptor(@"C:\", "", "NTFS", new VolumeSpace(200 * ClusterSize,
                (200 - expectedInUse) * ClusterSize, expectedInUse * ClusterSize, 0, 0, IsVolumeWide: true));
            var results = new Dictionary<string, DriveScanResult> { [drive.RootPath] = new(drive, tree, "NTFS", "") };
            var summary = DevTools.Services.DiskUsageAnalyzer.BuildDriveSummarySection([drive], results);
            var note = Assert.Single(summary.Entries).Note;
            Assert.Equal(incompleteRecords, note.Contains("Incomplete allocation in 2 NTFS records.", StringComparison.Ordinal));
            Assert.Equal(suppliesBitmap ? 2 : 1, bitmapRequests);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadVolumeAsync_KeepsUnverifiedExtensionAllocationOutOfFolderTotals(bool suppliesBitmap)
    {
        var directory = Directory.CreateTempSubdirectory("devtools-mft-extensions-");
        var path = Path.Combine(directory.FullName, "image.bin");
        try
        {
            var disk = BuildDisk(nonResidentList: true);
            AddExtension(99, (1UL << 48) | 25, 112); // Verified base record.
            AddExtension(101, (2UL << 48) | 25, 113); // Reused base record.
            AddExtension(103, (1UL << 48) | 99, 114); // Base outside the MFT.
            AddExtension(105, (1UL << 48) | 31, 115); // Empty base record.
            AddExtension(107, (1UL << 48) | 27, 116); // Base refers to an extension.

            void AddExtension(int recordOffset, ulong baseReference, byte allocationCluster)
            {
                new MftRecordBuilder().BaseRecord(baseReference)
                    .NamedData([0x11, 1, allocationCluster, 0]).Build().CopyTo(disk, recordOffset * ClusterSize);
            }

            await File.WriteAllBytesAsync(path, disk);
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var allocated = new byte[25];
            NtfsVolumeBitmapTests.SetBits(allocated, 10, 3);
            NtfsVolumeBitmapTests.SetBits(allocated, 30, 1);
            NtfsVolumeBitmapTests.SetBits(allocated, 40, 4);
            NtfsVolumeBitmapTests.SetBits(allocated, 55, 56);
            NtfsVolumeBitmapTests.SetBits(allocated, 150, 1);
            NtfsVolumeBitmapTests.SetBits(allocated, 120, 16);
            NtfsVolumeBitmapTests.SetBits(allocated, 112, 5);

            var table = await MftTableLoader.LoadVolumeAsync(handle, @"C:\", Geometry, null, Stopwatch.StartNew(),
                CancellationToken.None, keepFileNames: false,
                bitmapQuery: suppliesBitmap
                    ? (start, output) => NtfsVolumeBitmapTests.WriteReply(start, Geometry.TotalClusters, allocated, output)
                    : null);
            var tree = MftFolderSizeScanner.BuildTree(table, @"C:\", "fixture", Stopwatch.StartNew());

            Assert.Equal(4130, tree.TotalBytes);
            Assert.Equal(17 * ClusterSize, tree.GetAllocatedBytes(tree.Find(@"C:\Folder")));
            var bitmap = suppliesBitmap ? new NtfsBitmapAllocation(86 * ClusterSize, 86 * ClusterSize, 0) : (NtfsBitmapAllocation?)null;
            Assert.Equal(new NtfsRecordAllocation(65 * ClusterSize, 4 * ClusterSize, bitmap), tree.NtfsAllocation);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LoadVolumeAsync_UsesVerifiedNamesFromExtensions(bool keepFileNames, bool deferFileNames)
    {
        var directory = Directory.CreateTempSubdirectory("devtools-mft-names-");
        var path = Path.Combine(directory.FullName, "image.bin");
        try
        {
            var disk = BuildDisk(nonResidentList: true);
            new MftRecordBuilder().Directory(true).Sequence(3).Build().CopyTo(disk, 95 * ClusterSize);
            var modified = new DateTimeOffset(2020, 4, 5, 6, 7, 8, TimeSpan.Zero);
            new MftRecordBuilder().LastWrite(modified.ToFileTime()).Build().CopyTo(disk, 97 * ClusterSize);

            foreach (var (offset, baseReference, name, parent, nameNamespace) in new (int, ulong, string, ulong, byte)[]
            {
                (99, (1UL << 48) | 25, "stale-parent.bin", (2UL << 48) | 24, 1),
                (101, (1UL << 48) | 25, "DATA~1.BIN", (3UL << 48) | 24, 2),
                (103, (1UL << 48) | 25, "data.bin", (3UL << 48) | 24, 1),
                (105, (1UL << 48) | 25, "other-link.bin", (3UL << 48) | 24, 1),
                (107, (2UL << 48) | 24, "Reused folder record", (1UL << 48) | 5, 1),
                (109, (3UL << 48) | 24, "Folder", (1UL << 48) | 5, 1)
            })
            {
                var extension = new MftRecordBuilder().BaseRecord(baseReference).FileName(name, parent, nameNamespace);
                if (offset == 103)
                {
                    extension.NonResidentData(4130, 8192, runs: [0x11, 16, 120, 0], highestVcn: 15);
                }
                extension.Build().CopyTo(disk, offset * ClusterSize);
            }

            await File.WriteAllBytesAsync(path, disk);
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var table = await MftTableLoader.LoadVolumeAsync(handle, @"C:\", Geometry, null,
                Stopwatch.StartNew(), CancellationToken.None, keepFileNames, deferFileNames: deferFileNames);
            var tree = MftFolderSizeScanner.BuildTree(table, @"C:\", "fixture", Stopwatch.StartNew());

            Assert.True(table.IsNamedFile(25));
            Assert.Equal(24U, table.Parent[25]);
            Assert.Equal(keepFileNames ? "data.bin" : null, table.Names[25]);
            Assert.Equal(4130, tree.TotalBytes);
            Assert.Equal(1, tree.TotalFiles);
            var folder = tree.Find(@"C:\Folder");
            Assert.True(folder > 0);
            if (keepFileNames)
            {
                var ranked = MftScanner.RankFiles(table, @"C:\Folder", 1, Stopwatch.StartNew(), CancellationToken.None);
                var hit = Assert.Single(ranked.Top);
                Assert.Equal(@"C:\Folder\data.bin", hit.FullPath);
                Assert.Equal(4130, hit.SizeBytes);
                Assert.Equal(8192, hit.AllocatedBytes);
                Assert.Equal(modified, hit.ModifiedUtc);
            }
            else
            {
                Assert.Equal(16 * ClusterSize, tree.GetAllocatedBytes(folder));
                Assert.Equal(0, tree.NtfsAllocation?.UnlinkedBytes);
            }
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    [Fact]
    public void LoadDeferredFileDetails_CoalescesAdjacentWinnersAndSkipsGapsAcrossExtents()
    {
        const int recordSize = MftRecordBuilder.RecordSize;
        var extents = new List<(long Lcn, long ClusterCount)> { (20, 3), (50, 4), (100, 6) };
        var logical = new byte[13 * ClusterSize];
        var parentReferences = new uint[6];
        var sequences = new uint[6];
        int[] winners = [1, 2, 5];
        foreach (var index in winners)
        {
            new MftRecordBuilder().FileName($"file-{index}.bin", (7UL << 48) | 5, 1)
                .Build().CopyTo(logical, index * recordSize);
            parentReferences[index] = 5;
            sequences[index] = (1U << 16) | 7U;
        }

        var physical = new byte[106 * ClusterSize];
        var logicalOffset = 0;
        foreach (var (lcn, clusters) in extents)
        {
            var length = (int)clusters * ClusterSize;
            logical.AsSpan(logicalOffset, length).CopyTo(physical.AsSpan((int)lcn * ClusterSize));
            logicalOffset += length;
        }

        var table = new MftRecordTable(@"C:\", 6, parentReferences, new byte[6], new long[6], [], [], new string?[6]);
        var reads = new List<(long Offset, int Length)>();
        var details = MftTableLoader.LoadDeferredFileDetails(table, winners, extents, recordSize, ClusterSize, sequences,
            new Dictionary<int, long>(),
            (offset, destination) =>
            {
                reads.Add((offset, destination.Length));
                physical.AsSpan((int)offset, destination.Length).CopyTo(destination);
            }, CancellationToken.None);

        Assert.Equal(["file-1.bin", "file-2.bin", "file-5.bin"], winners.Select(index => details[index].Name));
        Assert.Equal([(22L * ClusterSize, ClusterSize), (50L * ClusterSize, 3 * ClusterSize),
            (103L * ClusterSize, 2 * ClusterSize)], reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RankFiles_ReadsWinnerDetailsAndRejectsChangedRecords(bool reuseRecord)
    {
        var directory = Directory.CreateTempSubdirectory("devtools-deferred-mft-names-");
        var path = Path.Combine(directory.FullName, "image.bin");
        try
        {
            var disk = BuildDisk(nonResidentList: true);
            var modified = new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero);
            new MftRecordBuilder().FileName("data.bin", (3UL << 48) | 24, 1).LastWrite(modified.ToFileTime())
                .NonResidentData(4130, 8192, runs: [0x11, 16, 120, 0], highestVcn: 15).Build().CopyTo(disk, 97 * ClusterSize);
            await File.WriteAllBytesAsync(path, disk);
            using var volume = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            var eager = await MftTableLoader.LoadVolumeAsync(volume, @"C:\", Geometry, null, Stopwatch.StartNew(),
                CancellationToken.None, keepFileNames: true);
            var deferred = await MftTableLoader.LoadVolumeAsync(volume, @"C:\", Geometry, null, Stopwatch.StartNew(),
                CancellationToken.None, keepFileNames: true, deferFileNames: true);
            Assert.Equal("Folder", deferred.Names[24]);
            Assert.Null(deferred.Names[25]);
            Assert.Empty(deferred.AllocSize);
            Assert.Empty(deferred.LastWrite);

            var first = MftScanner.RankFiles(eager, @"C:\Folder", 1, Stopwatch.StartNew(), CancellationToken.None);
            var second = MftScanner.RankFiles(deferred, @"C:\Folder", 1, Stopwatch.StartNew(), CancellationToken.None);
            var hit = Assert.Single(second.Top);
            Assert.Equal(Assert.Single(first.Top), hit);
            Assert.Equal(modified, hit.ModifiedUtc);
            Assert.Null(deferred.Names[25]);

            var stale = await MftTableLoader.LoadVolumeAsync(volume, @"C:\", Geometry, null, Stopwatch.StartNew(),
                CancellationToken.None, keepFileNames: true, deferFileNames: true);
            var replacement = new MftRecordBuilder().Sequence(reuseRecord ? (ushort)2 : (ushort)1)
                .FileName("data.bin", (3UL << 48) | 24, 1)
                .NonResidentData(reuseRecord ? 4130 : 8193, 12288).Build();
            RandomAccess.Write(volume, replacement, 97 * ClusterSize);
            var error = Assert.Throws<IOException>(() =>
                MftScanner.RankFiles(stale, @"C:\Folder", 1, Stopwatch.StartNew(), CancellationToken.None));
            Assert.Contains("NTFS record 25 changed", error.Message);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    [Theory]
    [InlineData(8, 6, 2)]
    [InlineData(4, 6, 4)]
    [InlineData(4, 1, 1)]
    public void LimitParserCount_UsesOnlyReadsThatTouchInitializedClusters(int clustersPerChunk, int limit, int expected)
    {
        var geometry = new NtfsVolumeData(512, 1024, 7 * 1024, 10, 200);
        var readers = MftTableLoader.LimitParserCount(geometry, [(10, 7), (50, 24), (100, 20)], clustersPerChunk, limit);
        Assert.Equal(expected, readers);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task LoadVolumeAsync_OneReadScanClipsTheLastExtentAndParsesRequestedMetadata(bool keepFileNames, bool suppliesBitmap)
    {
        const int clusterBytes = 4096;
        const int recordBytes = 1024;
        const int records = 7;
        const int allocatedClusters = 30;
        const int mftStart = 10;
        var geometry = new NtfsVolumeData(clusterBytes, recordBytes, records * recordBytes, mftStart, 200);
        var directory = Directory.CreateTempSubdirectory("devtools-mft-tail-");
        var path = Path.Combine(directory.FullName, "image.bin");
        try
        {
            // A truncated fixture makes any read beyond the last initialized cluster fail.
            var mftOffset = mftStart * clusterBytes;
            var disk = new byte[mftOffset + 2 * clusterBytes];
            new MftRecordBuilder().NonResidentData(records * recordBytes, allocatedClusters * clusterBytes,
                runs: [0x11, allocatedClusters, mftStart, 0], highestVcn: allocatedClusters - 1)
                .Build().CopyTo(disk, mftOffset);
            new MftRecordBuilder().Directory(true).FileName(".", (1UL << 48) | 5, 1)
                .Build().CopyTo(disk, mftOffset + 5 * recordBytes);
            new MftRecordBuilder().FileName("$Bitmap", (1UL << 48) | 5, 1)
                .Build().CopyTo(disk, mftOffset + 6 * recordBytes);

            await File.WriteAllBytesAsync(path, disk);
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bitmap = new byte[25];
            NtfsVolumeBitmapTests.SetBits(bitmap, mftStart, allocatedClusters);
            var table = await MftTableLoader.LoadVolumeAsync(handle, @"C:\", geometry, null,
                Stopwatch.StartNew(), CancellationToken.None, keepFileNames,
                bitmapQuery: suppliesBitmap
                    ? (start, output) => NtfsVolumeBitmapTests.WriteReply(start, geometry.TotalClusters, bitmap, output)
                    : null);

            Assert.Equal(records, table.RecordCount);
            Assert.True(table.IsNamedFile(6));
            Assert.Equal(keepFileNames ? "$Bitmap" : null, table.Names[6]);
            var allocatedBytes = allocatedClusters * clusterBytes;
            Assert.Equal(keepFileNames ? null : (long?)allocatedBytes, table.RecordAllocation?[0]);
            Assert.Equal(suppliesBitmap ? new NtfsBitmapAllocation(allocatedBytes, allocatedBytes, 0) : null,
                table.BitmapAllocation);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    private static byte[] BuildDisk(bool nonResidentList, ushort firstSequence = 7, ushort firstRecordNumber = 1,
        bool includeOverlappingRun = false)
    {
        var contents = new byte[32 * 3];
        WriteReference(contents.AsSpan(0), 0, BaseReference, 3);
        WriteReference(contents.AsSpan(32), 4, ((ulong)7 << 48) | firstRecordNumber, 4);
        WriteReference(contents.AsSpan(64), 8, ((ulong)9 << 48) | 3, 6);

        var recordZero = new MftRecordBuilder().Sequence(5)
            .NonResidentData(32_768, 32_768, runs: [0x11, 3, 10, 0x11, 1, 20, 0], highestVcn: 3, instance: 3);
        if (nonResidentList)
        {
            recordZero.NonResidentAttributeList(contents.Length, ClusterSize, [0x21, 1, 150, 0, 0]);
        }
        else
        {
            recordZero.AttributeList(contents);
        }

        var disk = new byte[200 * ClusterSize];
        recordZero.Build().CopyTo(disk, 10 * ClusterSize);
        var first = new MftRecordBuilder().Sequence(firstSequence).BaseRecord(BaseReference)
            .NonResidentData(0, 0, lowestVcn: 4, runs: [0x11, 4, 40, 0], highestVcn: 7, instance: 4).Build();
        first.AsSpan(0, ClusterSize).CopyTo(disk.AsSpan(12 * ClusterSize));
        first.AsSpan(ClusterSize).CopyTo(disk.AsSpan(30 * ClusterSize));
        new MftRecordBuilder().Sequence(9).BaseRecord(BaseReference)
            .NonResidentData(0, 0, lowestVcn: 8, runs: [0x11, 56, 55, 0], highestVcn: 63, instance: 6)
            .Build().CopyTo(disk, 42 * ClusterSize);
        new MftRecordBuilder().Directory(true).FileName(".", (1UL << 48) | 5, 1)
            .Build().CopyTo(disk, 57 * ClusterSize);
        new MftRecordBuilder().Directory(true).Sequence(3).FileName("Folder", (1UL << 48) | 5, 1)
            .Build().CopyTo(disk, 95 * ClusterSize);
        var file = new MftRecordBuilder().FileName("data.bin", (3UL << 48) | 24, 1)
            .NonResidentData(4130, 8192, runs: [0x11, 16, 120, 0], highestVcn: 15);
        if (includeOverlappingRun)
        {
            file.NamedData([0x11, 1, 120, 0]);
        }

        file.Build().CopyTo(disk, 97 * ClusterSize);
        contents.CopyTo(disk, 150 * ClusterSize);
        return disk;
    }

    private static void WriteReference(Span<byte> entry, long vcn, ulong reference, ushort instance)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(entry, 0x80);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 32);
        BinaryPrimitives.WriteInt64LittleEndian(entry[8..], vcn);
        BinaryPrimitives.WriteUInt64LittleEndian(entry[0x10..], reference);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[0x18..], instance);
    }
}
