using System.Buffers;
using DevTools.Services.FolderSizes;
using Xunit;

namespace DiskUsageAnalyzer.Tests.FolderSizes;

public sealed class ScanResourcesTests
{
    private const long MiB = 1024 * 1024;

    [Theory]
    [InlineData(32, 4096, true, false, 1)]
    [InlineData(32, 4096, true, true, 2)]
    [InlineData(32, 4096, false, false, 8)]
    [InlineData(32, 4096, null, false, 4)]
    [InlineData(32, 170, false, false, 1)]
    [InlineData(2, 4096, false, false, 1)]
    public void WalkWorkerCount_AdaptsToCpuMemoryAndDrive(int cores, long memory, bool? seek, bool prioritise, int workers)
    {
        var resources = new ScanResources(cores, memory * MiB);
        Assert.Equal(workers, resources.WalkWorkerCount(seek, prioritise));
    }

    [Theory]
    [InlineData(4, 2048L, 2, 2)]
    [InlineData(2, 4096L, 4, 1)]
    [InlineData(8, 1024L, 4, 1)]
    [InlineData(8, null, 4, 1)]
    [InlineData(8, 4096L, 1, 1)]
    [InlineData(8, 4096L, 0, 1)]
    [InlineData(8, 3072L, 4, 3)]
    [InlineData(12, 6144L, 6, 4)]
    public void VolumeWalkParallelism_UsesCpuMemoryAndIndependentDisks(
        int processors, long? memoryMiB, int independentDisks, int expected)
    {
        Assert.Equal(expected, new ScanResources(processors, memoryMiB * MiB).VolumeWalkParallelism(independentDisks));
    }

    [Theory]
    [InlineData(32, 4096, false, 2, 8)]
    [InlineData(32, 4096, false, 4, 4)]
    [InlineData(8, 4096, false, 2, 2)]
    [InlineData(32, 512, false, 2, 2)]
    [InlineData(32, 4096, true, 2, 1)]
    [InlineData(32, 4096, null, 2, 4)]
    public void WalkWorkerCount_SharesCpuAndMemoryBeforeApplyingTheDeviceLimit(
        int processors, long memoryMiB, bool? seekPenalty, int volumeShare, int expected)
    {
        var resources = new ScanResources(processors, memoryMiB * MiB);
        Assert.Equal(expected, resources.WalkWorkerCount(seekPenalty, prioritize: false, volumeShare));
    }

    [Fact]
    public void SeekPenaltyFallback_UsesEveryDistinctBackingDiskUntilOneReportsSeeks()
    {
        var queried = new List<int>();
        bool? Read(int disk)
        {
            queried.Add(disk);
            return disk switch
            {
                1 or 2 => false,
                3 => true,
                _ => null
            };
        }

        Assert.Null(ScanResources.ResolveBackingDiskSeekPenalty([], Read));
        Assert.False(ScanResources.ResolveBackingDiskSeekPenalty([1, 1, 2], Read));
        Assert.Equal([1, 2], queried);
        Assert.Null(ScanResources.ResolveBackingDiskSeekPenalty([1, 4], Read));
        Assert.True(ScanResources.ResolveBackingDiskSeekPenalty([4, 2, 3], Read));
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(32, 2)]
    [InlineData(64, 4)]
    [InlineData(128, 8)]
    public void MftPlan_SizesReadsToTheInitializedTable(int mftMiB, int chunkMiB)
    {
        var plan = new ScanResources(4, 4096 * MiB)
            .MftPlan(mftMiB * 1024, mftMiB * MiB, clusterBytes: 4096, recordBytes: 1024, includeFileNames: false);

        Assert.Equal(chunkMiB * MiB, plan.ChunkBytes);
        Assert.Equal(2, plan.Parsers);
        Assert.Equal(0, plan.CarryBytes);
    }

    [Fact]
    public void MftPlan_UsesSmallerBuffersWithLimitedMemory()
    {
        var high = new ScanResources(24, 4096 * MiB)
            .MftPlan(1_000_000, 1_024_000_000, clusterBytes: 4096, recordBytes: 1024, includeFileNames: false);
        var low = new ScanResources(24, 128 * MiB)
            .MftPlan(1_000_000, 1_024_000_000, clusterBytes: 4096, recordBytes: 1024, includeFileNames: false);

        Assert.True(low.ChunkBytes < high.ChunkBytes);
        Assert.True(low.Parsers < high.Parsers);
        Assert.True(low.QueueCapacity <= high.QueueCapacity);
    }

    [Theory]
    [InlineData(4096, 0, 4)]
    [InlineData(512, 512, 3)]
    [InlineData(1536, 512, 3)]
    public void MftPlan_BudgetsTheActualPoolBucketAndTheLargestPossibleCarry(int clusterBytes, int carryBytes, int parsers)
    {
        const int records = 1_000_000;
        const int recordBytes = 1024;
        var plan = new ScanResources(24, 128 * MiB)
            .MftPlan(records, (long)records * recordBytes, clusterBytes, recordBytes, includeFileNames: false);

        Assert.Equal(0, plan.ChunkBytes % clusterBytes);
        Assert.Equal(carryBytes, plan.CarryBytes);
        Assert.Equal(parsers, plan.Parsers);
        Assert.InRange(plan.QueueCapacity, 1, Math.Min(plan.Parsers, 4));

        var buffer = ArrayPool<byte>.Shared.Rent(plan.ChunkBytes + plan.CarryBytes);
        try
        {
            var liveBuffers = (long)buffer.Length * (plan.Parsers + plan.QueueCapacity + 1) + plan.CarryBytes;
            Assert.InRange(liveBuffers, 1L, 6 * MiB);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [Fact]
    public void MftPlan_TinyTableUsesLessThanOneMiBOfMemoryAndRejectsAnOversizeCluster()
    {
        var resources = new ScanResources(4, MiB);
        var plan = resources.MftPlan(32, 32 * 1024, clusterBytes: 4096, recordBytes: 1024, includeFileNames: false);
        Assert.Equal(32 * 1024, plan.ChunkBytes);
        Assert.Throws<InsufficientMemoryException>(() =>
            resources.MftPlan(32, 32 * 1024, clusterBytes: 2 * (int)MiB, recordBytes: 1024, includeFileNames: false));
    }

    [Fact]
    public void MftPlan_RejectsATableThatWouldExhaustMemory()
    {
        var resources = new ScanResources(12, 128 * MiB);
        Assert.Throws<InsufficientMemoryException>(() =>
            resources.MftPlan(5_000_000, 5120 * MiB, clusterBytes: 4096, recordBytes: 1024, includeFileNames: true));
    }
}
