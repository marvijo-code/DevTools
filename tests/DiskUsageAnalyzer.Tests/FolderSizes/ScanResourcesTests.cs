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
            .MftPlan(mftMiB * 1024, mftMiB * MiB, includeFileNames: false);

        Assert.Equal(chunkMiB * MiB, plan.ChunkBytes);
        Assert.Equal(2, plan.Parsers);
    }

    [Fact]
    public void MftPlan_UsesSmallerBuffersWithLimitedMemory()
    {
        var high = new ScanResources(24, 4096 * MiB).MftPlan(1_000_000, 1024 * MiB, includeFileNames: false);
        var low = new ScanResources(24, 128 * MiB).MftPlan(1_000_000, 1024 * MiB, includeFileNames: false);

        Assert.True(low.ChunkBytes < high.ChunkBytes);
        Assert.True(low.Parsers < high.Parsers);
        Assert.True(low.QueueCapacity <= high.QueueCapacity);
    }

    [Fact]
    public void MftPlan_RejectsATableThatWouldExhaustMemory()
    {
        var resources = new ScanResources(12, 128 * MiB);
        Assert.Throws<InsufficientMemoryException>(() => resources.MftPlan(5_000_000, 5120 * MiB, includeFileNames: true));
    }
}
