using System.Buffers.Binary;
using DevTools.Services.BiggestFiles.Ntfs;
using Xunit;

namespace DiskUsageAnalyzer.Tests.BiggestFiles;

public sealed class MftRecordParserTests
{
    [Fact]
    public void TryParseRecord_RejectsBadSignature()
    {
        var record = new byte[MftRecordBuilder.RecordSize];
        record[0] = (byte)'B';
        record[1] = (byte)'A';
        record[2] = (byte)'A';
        record[3] = (byte)'D';

        var info = new MftRecordInfo();
        Assert.False(MftRecordParser.TryParseRecord(record, ref info));
    }

    [Fact]
    public void TryParseRecord_RejectsTornRecord()
    {
        var record = new MftRecordBuilder()
            .FileName("torn.bin", parent: 5, nameNamespace: 1)
            .NonResidentData(realSize: 100, allocatedSize: 128)
            .Build();

        MftRecordBuilder.CorruptSectorTail(record, sector: 1);
        var original = (byte[])record.Clone();

        var info = new MftRecordInfo();
        Assert.False(MftRecordParser.TryParseRecord(record, ref info));
        Assert.Equal(original, record);
    }

    [Theory]
    [InlineData(0x30, 1)]
    [InlineData(0x30, 2)]
    [InlineData(0x1fe, 3)]
    public void TryParseRecord_RejectsIncompleteOrMisplacedFixups(int offset, int count)
    {
        var record = new MftRecordBuilder().ResidentData(5).Build();
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), (ushort)offset);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)count);

        var info = new MftRecordInfo();
        Assert.False(MftRecordParser.TryParseRecord(record, ref info));
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void TryParseRecord_RestoresEveryProtectedSector(int size)
    {
        const int sectorSize = 512;
        const int usaOffset = 0x30;
        const ushort sequence = 0x1020;
        var record = new byte[size];
        "FILE"u8.CopyTo(record);
        var sectors = size / sectorSize;
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), usaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), (ushort)(sectors + 1));
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x14), 0x80);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(0x16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x18), 0x80);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset), sequence);
        for (var sector = 1; sector <= sectors; sector++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(usaOffset + 2 * sector), (ushort)(0xCC00 + sector));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(sector * sectorSize - 2), sequence);
        }

        var info = new MftRecordInfo();
        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        for (var sector = 1; sector <= sectors; sector++)
        {
            Assert.Equal((ushort)(0xCC00 + sector),
                BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(sector * sectorSize - 2)));
        }
    }

    [Fact]
    public void TryParseRecord_AppliesFixupsAndReadsNonResidentSizes()
    {
        var record = new MftRecordBuilder()
            .Sequence(7)
            .FileName("movie.mkv", parent: 42, nameNamespace: 1)
            .NonResidentData(realSize: 8_000_000_000, allocatedSize: 8_001_257_472)
            .LastWrite(133_000_000_000_000_000)
            .Build();

        var info = new MftRecordInfo();
        Assert.True(MftRecordParser.TryParseRecord(record, ref info));

        Assert.True(info.InUse);
        Assert.False(info.IsDirectory);
        Assert.True(info.HasName);
        Assert.Equal(42UL, info.ParentRecordNumber);
        Assert.True(info.HasUnnamedData);
        Assert.True(info.DataSizesValid);
        Assert.Equal(8_000_000_000, info.DataRealSize);
        Assert.Equal(8_001_257_472, info.DataAllocatedSize);
        Assert.Equal(133_000_000_000_000_000, info.LastWriteFileTime);

        var name = System.Text.Encoding.Unicode.GetString(record.AsSpan(info.NameOffsetInRecord, info.NameLengthChars * 2));
        Assert.Equal("movie.mkv", name);
    }

    [Fact]
    public void TryParseRecord_ScanSkipsInactiveRecordsBeforeMutatingTheBuffer()
    {
        var record = new MftRecordBuilder().InUse(false).ResidentData(size: 4).Build();
        var original = (byte[])record.Clone();
        var info = new MftRecordInfo();

        Assert.False(MftRecordParser.TryParseRecord(record, ref info, requireInUse: true, readLastWrite: false));
        Assert.Equal(original, record);
    }

    [Fact]
    public void TryParseRecord_FolderScanReadsSizesWithoutTimestamps()
    {
        var record = new MftRecordBuilder()
            .FileName("test.bin", parent: 5, nameNamespace: 1)
            .NonResidentData(realSize: 50, allocatedSize: 4096)
            .LastWrite(133_000_000_000_000_000)
            .Build();
        var info = new MftRecordInfo();

        Assert.True(MftRecordParser.TryParseRecord(record, ref info, requireInUse: true, readLastWrite: false));
        Assert.True(info.HasName);
        Assert.Equal(50, info.DataRealSize);
        Assert.Equal(4096, info.DataAllocatedSize);
        Assert.Equal(0, info.LastWriteFileTime);
    }

    [Fact]
    public void TryParseRecord_ReadsResidentDataSize()
    {
        var record = new MftRecordBuilder()
            .FileName("tiny.txt", parent: 5, nameNamespace: 1)
            .ResidentData(size: 480)
            .Build();

        var info = new MftRecordInfo();
        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        Assert.True(info.DataSizesValid);
        Assert.Equal(480, info.DataRealSize);
    }

    [Fact]
    public void TryParseRecord_IgnoresNamedDataStream()
    {
        // Only a named ADS is present (no unnamed $DATA): sizes must remain unset.
        var record = new MftRecordBuilder()
            .FileName("host.dat", parent: 5, nameNamespace: 1)
            .NamedData()
            .Build();

        var info = new MftRecordInfo();
        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        Assert.False(info.DataSizesValid);
        Assert.Equal(0, info.DataRealSize);
    }

    [Fact]
    public void TryParseRecord_AccountsForPhysicalRunsInAllStreams()
    {
        var record = new MftRecordBuilder()
            .NonResidentData(32_768, 32_768, runs: [0x11, 3, 4, 0x01, 5, 0])
            .NamedData([0x11, 2, 9, 0])
            .Build();
        var info = new MftRecordInfo();

        Assert.True(MftRecordParser.TryParseRecord(record, ref info, requireInUse: true, readLastWrite: false, countAllocation: true));
        Assert.Equal(32_768, info.DataRealSize);
        Assert.Equal(5, info.AllocatedClusters);
    }

    [Fact]
    public void TryParseRecord_DoesNotAddSeparateAllocationForResidentData()
    {
        var record = new MftRecordBuilder().ResidentData(size: 25).Build();
        var info = new MftRecordInfo();

        Assert.True(MftRecordParser.TryParseRecord(record, ref info, requireInUse: true, readLastWrite: false, countAllocation: true));
        Assert.Equal(25, info.DataRealSize);
        Assert.Equal(0, info.AllocatedClusters);
    }

    [Fact]
    public void TryParseRecord_PreservesTheFullParentReference()
    {
        const ulong parentNumber = (1UL << 37) | 24;
        const ushort parentSequence = 0xB079;
        var record = new MftRecordBuilder()
            .FileName("data.bin", ((ulong)parentSequence << 48) | parentNumber, nameNamespace: 1)
            .Build();
        var info = new MftRecordInfo();

        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        Assert.Equal(parentNumber, info.ParentRecordNumber);
        Assert.Equal(parentSequence, info.ParentRecordSequence);
    }

    [Fact]
    public void TryParseRecord_RecognisesExtensionsOfRecordZero()
    {
        var record = new MftRecordBuilder().BaseRecord(7UL << 48).Build();
        var info = new MftRecordInfo();

        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        Assert.True(info.IsExtension);
        Assert.Equal(0UL, info.BaseRecordNumber);
        Assert.Equal(7, info.BaseRecordSequence);
    }

    [Fact]
    public void TryParseRecord_RejectsANameBeyondItsDeclaredValueAndKeepsTheValidDataSize()
    {
        var record = new MftRecordBuilder()
            .FileName("valid-layout.txt", parent: 42, nameNamespace: 1)
            .ResidentData(32)
            .Build();
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 0x10), 0x42);
        var info = new MftRecordInfo();

        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        Assert.False(info.HasName);
        Assert.Equal(32, info.DataRealSize);
    }

    [Fact]
    public void TryParseRecord_IgnoresAResidentValueOutsideItsAttribute()
    {
        var record = new MftRecordBuilder().ResidentData(16).Build();
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 0x10), uint.MaxValue);
        var info = new MftRecordInfo();

        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        Assert.False(info.DataSizesValid);
    }

    [Fact]
    public void TryParseRecord_IgnoresANonResidentAttributeWithATruncatedHeader()
    {
        var record = new MftRecordBuilder().NonResidentData(4096, 4096).Build();
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 4), 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset + 0x20), uint.MaxValue);
        var info = new MftRecordInfo();

        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        Assert.False(info.DataSizesValid);
    }

    [Fact]
    public void TryParseRecord_DoesNotReadUnusedSpaceInAnMftRecord()
    {
        var record = new MftRecordBuilder()
            .NonResidentData(4096, 4096, runs: [0x11, 1, 8, 0]).Build();
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x14));
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(0x18), (uint)(offset + 8));
        var info = new MftRecordInfo();

        Assert.False(MftRecordParser.TryReadUnnamedDataRuns(record.ToArray(), []));
        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        Assert.False(info.DataSizesValid);
    }

    [Theory]
    [InlineData(0xff, 0)]
    [InlineData(98, 2)]
    [InlineData(99, 0)]
    public void TryParseRecord_IgnoresAllocationOutsideTheVolume(byte relativeLcn, long expected)
    {
        var record = new MftRecordBuilder()
            .NonResidentData(4096, 8192, runs: [0x11, 2, relativeLcn, 0]).Build();
        var info = new MftRecordInfo();

        Assert.True(MftRecordParser.TryParseRecord(record, ref info, requireInUse: true,
            readLastWrite: false, countAllocation: true, volumeClusters: 100));
        Assert.Equal(4096, info.DataRealSize);
        Assert.Equal(expected, info.AllocatedClusters);
    }

    [Fact]
    public void TryParseRecord_NeverUsesFileNameSizes()
    {
        // The builder stamps 999_999 into the stale $FILE_NAME size fields; the parser must ignore them.
        var record = new MftRecordBuilder()
            .FileName("data.bin", parent: 5, nameNamespace: 1)
            .NonResidentData(realSize: 123, allocatedSize: 4096)
            .Build();

        var info = new MftRecordInfo();
        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        Assert.Equal(123, info.DataRealSize);
        Assert.NotEqual(999_999, info.DataRealSize);
    }

    [Fact]
    public void TryParseRecord_IgnoresSizesFromNonZeroLowestVcnFragment()
    {
        var record = new MftRecordBuilder()
            .FileName("frag.bin", parent: 5, nameNamespace: 1)
            .NonResidentData(realSize: 555, allocatedSize: 4096, lowestVcn: 200)
            .Build();

        var info = new MftRecordInfo();
        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        Assert.True(info.HasUnnamedData);
        Assert.False(info.DataSizesValid);
        Assert.Equal(0, info.DataRealSize);
    }

    [Fact]
    public void TryParseRecord_PrefersWin32NamespaceOverDos()
    {
        var record = new MftRecordBuilder()
            .FileName("LONGNA~1.TXT", parent: 5, nameNamespace: 2) // DOS first
            .FileName("LongName.txt", parent: 5, nameNamespace: 1) // Win32 second
            .ResidentData(size: 10)
            .Build();

        var info = new MftRecordInfo();
        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        var name = System.Text.Encoding.Unicode.GetString(record.AsSpan(info.NameOffsetInRecord, info.NameLengthChars * 2));
        Assert.Equal("LongName.txt", name);
    }

    [Fact]
    public void TryParseRecord_SurfacesExtensionBaseNumber()
    {
        var record = new MftRecordBuilder()
            .BaseRecord(99)
            .NonResidentData(realSize: 4096, allocatedSize: 4096)
            .Build();

        var info = new MftRecordInfo();
        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        Assert.Equal(99UL, info.BaseRecordNumber);
        Assert.True(info.DataSizesValid);
    }

    [Fact]
    public void TryParseRecord_FlagsDirectoryAndNotInUse()
    {
        var record = new MftRecordBuilder()
            .InUse(false)
            .Directory(true)
            .FileName("dead", parent: 5, nameNamespace: 1)
            .Build();

        var info = new MftRecordInfo();
        Assert.True(MftRecordParser.TryParseRecord(record, ref info));
        Assert.False(info.InUse);
        Assert.True(info.IsDirectory);
    }

    [Theory]
    [InlineData(new byte[] { 0x00 }, new long[] { }, new long[] { })]
    // Single run: length 0x08 clusters at LCN 0x000000 (relative offset 0x34) -> 52.
    [InlineData(new byte[] { 0x11, 0x08, 0x34, 0x00 }, new long[] { 0x34 }, new long[] { 0x08 })]
    public void TryDecodeRunList_DecodesRuns(byte[] runList, long[] expectedLcns, long[] expectedCounts)
    {
        var extents = new List<(long Lcn, long ClusterCount)>();
        Assert.True(MftRecordParser.TryDecodeRunList(runList, extents));
        Assert.Equal(expectedLcns.Length, extents.Count);
        for (var i = 0; i < expectedLcns.Length; i++)
        {
            Assert.Equal(expectedLcns[i], extents[i].Lcn);
            Assert.Equal(expectedCounts[i], extents[i].ClusterCount);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryReadUnnamedDataRuns_RejectsMalformedOrSparseMftMap(bool sparseRun)
    {
        byte[] runs = sparseRun ? [0x11, 2, 9, 0x01, 3, 0] : [0x11, 2, 9, 0x09, 0];
        var record = new MftRecordBuilder().NonResidentData(20_480, 20_480, runs: runs).Build();
        var extents = new List<(long Lcn, long ClusterCount)>();

        Assert.False(MftRecordParser.TryReadUnnamedDataRuns(record, extents));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void TryDecodeRunList_ReadsEverySupportedFieldWidth(int width)
    {
        var runBytes = 1 + 2 * width;
        var bytes = new byte[2 * runBytes + 1];
        bytes[0] = bytes[runBytes] = (byte)((width << 4) | width);
        var clusterCount = (1L << (8 * (width - 1))) + 1;
        var firstLcn = (1L << (8 * width - 2)) + 16;
        BitConverter.GetBytes(clusterCount).AsSpan(0, width).CopyTo(bytes.AsSpan(1));
        BitConverter.GetBytes(firstLcn).AsSpan(0, width).CopyTo(bytes.AsSpan(1 + width));
        BitConverter.GetBytes(1L).AsSpan(0, width).CopyTo(bytes.AsSpan(runBytes + 1));
        BitConverter.GetBytes(-7L).AsSpan(0, width).CopyTo(bytes.AsSpan(runBytes + 1 + width));
        var extents = new List<(long Lcn, long ClusterCount)>();

        Assert.True(MftRecordParser.TryDecodeRunList(bytes, extents));
        Assert.Equal([(firstLcn, clusterCount), (firstLcn - 7, 1L)], extents);
    }

    [Fact]
    public void TryDecodeRunList_IsolatesPackedFieldsAndKeepsTheLcnAcrossSparseRuns()
    {
        byte[] runs =
        [
            0x12, 0x66, 0x00, 0x74, // 102 clusters at LCN 116.
            0x01, 0x06,             // Six sparse clusters.
            0x31, 0x0C, 0xF0, 0xFF, 0xFF, // 12 clusters after a negative delta of 16.
            0x11, 0x04, 0x7F,       // Four clusters after a positive delta of 127.
            0x00, 0xFF, 0xFF, 0xFF
        ];
        var extents = new List<(long Lcn, long ClusterCount)>();

        Assert.True(MftRecordParser.TryDecodeRunList(runs, extents));
        Assert.Equal([(116L, 102L), (100L, 12L), (227L, 4L)], extents);
    }

    [Fact]
    public void TryDecodeRunList_RejectsLengthsOutsideTheSignedClusterRange()
    {
        byte[] runs = [0x08, 0, 0, 0, 0, 0, 0, 0, 0x80, 0];
        Assert.False(MftRecordParser.TryDecodeRunList(runs, []));
    }

    [Fact]
    public void TryDecodeRunList_HandlesNegativeRelativeOffsets()
    {
        // Run 1: 0x11 0x20 0x00  -> length 0x20 at LCN 0x00 ... wait offsetSize 1, offset 0x00 => LCN 0.
        // Run 2: 0x11 0x10 0xF0  -> length 0x10, signed offset 0xF0 = -16 => LCN 0 + (-16) = -16.
        var runList = new byte[] { 0x11, 0x20, 0x40, 0x11, 0x10, 0xF0, 0x00 };
        var extents = new List<(long Lcn, long ClusterCount)>();
        Assert.True(MftRecordParser.TryDecodeRunList(runList, extents));

        Assert.Equal(2, extents.Count);
        Assert.Equal(0x40, extents[0].Lcn);
        Assert.Equal(0x20, extents[0].ClusterCount);
        Assert.Equal(0x40 - 16, extents[1].Lcn);
        Assert.Equal(0x10, extents[1].ClusterCount);
    }
}
