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

        var info = new MftRecordInfo();
        Assert.False(MftRecordParser.TryParseRecord(record, ref info));
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
