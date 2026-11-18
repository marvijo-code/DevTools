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

    [Fact]
    public async Task LoadVolumeAsync_ScansFilesAndAllocationAfterTheMapContinuations()
    {
        var directory = Directory.CreateTempSubdirectory("devtools-mft-volume-");
        var path = Path.Combine(directory.FullName, "image.bin");
        try
        {
            await File.WriteAllBytesAsync(path, BuildDisk(nonResidentList: true));
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var source = await MftTableLoader.LoadVolumeAsync(handle, @"C:\", Geometry, null,
                Stopwatch.StartNew(), CancellationToken.None, keepFileNames: false);
            var tree = MftFolderSizeScanner.BuildTree(source, @"C:\", "fixture", Stopwatch.StartNew());

            Assert.Equal(4130, tree.TotalBytes);
            Assert.Equal(1, tree.TotalFiles);
            Assert.Equal(8192, tree.GetAllocatedBytes(tree.Find(@"C:\Folder")));
            Assert.Equal(new NtfsRecordAllocation(65 * ClusterSize, 0), tree.NtfsAllocation);
        }
        finally
        {
            File.Delete(path);
            directory.Delete();
        }
    }

    private static byte[] BuildDisk(bool nonResidentList, ushort firstSequence = 7, ushort firstRecordNumber = 1)
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
        new MftRecordBuilder().FileName("data.bin", (3UL << 48) | 24, 1)
            .NonResidentData(4130, 8192, runs: [0x11, 16, 120, 0], highestVcn: 15)
            .Build().CopyTo(disk, 97 * ClusterSize);
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
