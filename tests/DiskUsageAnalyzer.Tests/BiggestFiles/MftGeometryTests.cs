using DevTools.Services.BiggestFiles.Ntfs;
using Xunit;

namespace DiskUsageAnalyzer.Tests.BiggestFiles;

public sealed class MftGeometryTests
{
    private static readonly NtfsVolumeData Geometry = new(4096, 1024, 76 * 1024, 60, 100);

    [Fact]
    public void GetRecordCount_RejectsInvalidVolumeGeometry()
    {
        Assert.Equal(76, MftTableLoader.GetRecordCount(Geometry));
        Assert.Throws<NotSupportedException>(() => MftTableLoader.GetRecordCount(Geometry with { MftStartLcn = 100 }));
        Assert.Throws<NotSupportedException>(() => MftTableLoader.GetRecordCount(Geometry with { MftValidDataLength = 4096L * 101 }));
        Assert.Throws<NotSupportedException>(() => MftTableLoader.GetRecordCount(Geometry with { MftValidDataLength = 76 * 1024 + 1 }));
        Assert.Throws<NotSupportedException>(() => MftTableLoader.GetRecordCount(Geometry with { TotalClusters = long.MaxValue }));
        Assert.Throws<NotSupportedException>(() => MftTableLoader.GetRecordCount(Geometry with { MftStartLcn = 99, BytesPerFileRecordSegment = 8192, MftValidDataLength = 8192 * 8 }));
    }

    [Fact]
    public void ValidateMftExtents_PreservesFragmentedLogicalOrder()
    {
        var extents = new List<(long Lcn, long ClusterCount)> { (60, 10), (2, 5), (96, 4) };
        MftTableLoader.ValidateMftExtents(Geometry, extents);
        Assert.Equal([(60L, 10L), (2L, 5L), (96L, 4L)], extents);
    }

    [Fact]
    public void ValidateMftExtents_RejectsOutOfRangeOverlappingOrTruncatedReads()
    {
        Assert.Throws<IOException>(() => MftTableLoader.ValidateMftExtents(Geometry, []));
        Assert.Throws<IOException>(() => MftTableLoader.ValidateMftExtents(Geometry, [(59, 20)]));
        Assert.Throws<IOException>(() => MftTableLoader.ValidateMftExtents(Geometry, [(60, 18)]));
        Assert.Throws<IOException>(() => MftTableLoader.ValidateMftExtents(Geometry, [(60, 12), (71, 8)]));
        Assert.Throws<IOException>(() => MftTableLoader.ValidateMftExtents(Geometry, [(60, 15), (96, 5)]));
        Assert.Throws<IOException>(() => MftTableLoader.ValidateMftExtents(Geometry, [(60, long.MaxValue)]));
    }
}
