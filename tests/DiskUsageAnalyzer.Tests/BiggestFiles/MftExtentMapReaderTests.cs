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
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadVolumeAsync_ScansFilesAndAllocationAfterTheMapContinuations(bool suppliesBitmap)
    {
        var directory = Directory.CreateTempSubdirectory("devtools-mft-volume-");
        var path = Path.Combine(directory.FullName, "image.bin");
        try
        {
            await File.WriteAllBytesAsync(path, BuildDisk(nonResidentList: true, includeOverlappingRun: true));
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var allocated = new byte[25];
            NtfsVolumeBitmapTests.SetBits(allocated, 10, 3);
            NtfsVolumeBitmapTests.SetBits(allocated, 30, 1);
            NtfsVolumeBitmapTests.SetBits(allocated, 40, 4);
            NtfsVolumeBitmapTests.SetBits(allocated, 55, 56);
            NtfsVolumeBitmapTests.SetBits(allocated, 150, 1);
            NtfsVolumeBitmapTests.SetBits(allocated, 120, 15);
            NtfsVolumeBitmapTests.SetBits(allocated, 199, 1);
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
            var expected = new NtfsBitmapAllocation(81 * ClusterSize, 80 * ClusterSize, ClusterSize, (uint)ClusterSize);
            Assert.Equal(new NtfsRecordAllocation(65 * ClusterSize, 0, suppliesBitmap ? expected : null), tree.NtfsAllocation);
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
