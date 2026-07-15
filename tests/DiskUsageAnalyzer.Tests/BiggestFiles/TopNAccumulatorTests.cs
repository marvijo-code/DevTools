using DevTools.Services.BiggestFiles;
using Xunit;

namespace DiskUsageAnalyzer.Tests.BiggestFiles;

public sealed class TopNAccumulatorTests
{
    [Fact]
    public void KeepsOnlyLargestItems()
    {
        var accumulator = new TopNAccumulator<string>(3);
        foreach (var (size, name) in new[] { (10L, "a"), (50L, "b"), (30L, "c"), (5L, "d"), (90L, "e") })
        {
            accumulator.TryAdd(size, name);
        }

        var snapshot = accumulator.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.Equal(new[] { "e", "b", "c" }, snapshot.Select(item => item.Item).ToArray());
        Assert.Equal(new[] { 90L, 50L, 30L }, snapshot.Select(item => item.SizeBytes).ToArray());
    }

    [Fact]
    public void ThresholdRisesOnceFull()
    {
        var accumulator = new TopNAccumulator<int>(2);
        Assert.Equal(long.MinValue, accumulator.Threshold);

        accumulator.TryAdd(100, 1);
        Assert.Equal(long.MinValue, accumulator.Threshold);

        accumulator.TryAdd(200, 2);
        Assert.Equal(100, accumulator.Threshold);

        Assert.False(accumulator.TryAdd(50, 3));
        Assert.True(accumulator.TryAdd(300, 4));
        Assert.Equal(200, accumulator.Threshold);
    }

    [Fact]
    public void ConcurrentAddsKeepExactTopSet()
    {
        const int capacity = 100;
        const int total = 100_000;
        var accumulator = new TopNAccumulator<int>(capacity);

        Parallel.For(0, total, i => accumulator.TryAdd(i, i));

        var snapshot = accumulator.Snapshot();
        Assert.Equal(capacity, snapshot.Count);

        var expected = Enumerable.Range(total - capacity, capacity).Reverse().ToArray();
        Assert.Equal(expected, snapshot.Select(item => item.Item).ToArray());
    }
}
