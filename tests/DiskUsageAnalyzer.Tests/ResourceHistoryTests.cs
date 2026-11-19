using DevTools.Models;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class ResourceHistoryTests
{
    [Fact]
    public void Series_ReturnsSamplesOldestFirst()
    {
        var history = new ResourceHistory(5);
        AddSamples(history, 1, 2, 3);

        Assert.Equal([1d, 2d, 3d], history.Series(sample => sample.CpuPercent));
    }

    [Fact]
    public void Series_KeepsChronologicalOrderAfterTheBufferWraps()
    {
        var history = new ResourceHistory(3);
        AddSamples(history, 1, 2, 3, 4, 5);

        Assert.Equal(3, history.Count);
        Assert.Equal(3d, history.Oldest!.CpuPercent);
        Assert.Equal(5d, history.Latest!.CpuPercent);
        Assert.Equal([3d, 4d, 5d], history.Series(sample => sample.CpuPercent));
    }

    [Fact]
    public void Latest_TracksTheMostRecentSample()
    {
        var history = new ResourceHistory(3);
        AddSamples(history, 10, 20);

        Assert.NotNull(history.Latest);
        Assert.Equal(20d, history.Latest!.CpuPercent);
    }

    [Fact]
    public void Average_UsesOnlyTheMostRecentSamples()
    {
        var history = new ResourceHistory(10);
        AddSamples(history, 100, 100, 100, 0, 0);

        Assert.Equal(0d, history.Average(sample => sample.CpuPercent, 2));
        Assert.Equal(60d, history.Average(sample => sample.CpuPercent, 5));
    }

    [Fact]
    public void Average_ClampsTheWindowToTheSamplesAvailable()
    {
        var history = new ResourceHistory(10);
        AddSamples(history, 40, 60);

        Assert.Equal(50d, history.Average(sample => sample.CpuPercent, 100));
    }

    [Fact]
    public void Peak_ReturnsTheHighestValueInTheWindow()
    {
        var history = new ResourceHistory(10);
        AddSamples(history, 10, 90, 20, 30);

        Assert.Equal(30d, history.Peak(sample => sample.CpuPercent, 2));
        Assert.Equal(90d, history.Peak(sample => sample.CpuPercent, 4));
    }

    [Fact]
    public void Clear_ResetsTheWindow()
    {
        var history = new ResourceHistory(4);
        AddSamples(history, 1, 2, 3);
        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.Null(history.Latest);
        Assert.Null(history.Oldest);
        Assert.Empty(history.Snapshot());
        Assert.Empty(history.Series(sample => sample.CpuPercent));
        Assert.Equal(0d, history.Average(sample => sample.CpuPercent, 4));
        Assert.Equal(0d, history.Peak(sample => sample.CpuPercent, 4));
        AddSamples(history, 7);
        Assert.Equal(7d, history.Oldest!.CpuPercent);
        Assert.Equal([7d], history.Series(sample => sample.CpuPercent));
    }

    [Fact]
    public void SystemVolume_PrefersTheWindowsVolumeAndFallsBackToTheFirstVolume()
    {
        var windowsRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        var otherRoot = windowsRoot.Equals(@"Z:\", StringComparison.OrdinalIgnoreCase) ? @"Y:\" : @"Z:\";
        var other = new VolumeUsage(otherRoot, "Other", 100, 20);
        var windows = new VolumeUsage(windowsRoot.ToLowerInvariant(), "Windows", 100, 10);
        var sample = ResourceSample.Empty with { Volumes = [other, windows] };

        Assert.Same(windows, sample.SystemVolume);
        Assert.Same(other, (sample with { Volumes = [other] }).SystemVolume);
        Assert.Null(ResourceSample.Empty.SystemVolume);
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourceHistory(0));
    }

    private static void AddSamples(ResourceHistory history, params double[] cpuPercentages)
    {
        foreach (var cpuPercent in cpuPercentages)
        {
            history.Add(ResourceSample.Empty with
            {
                Timestamp = DateTimeOffset.UnixEpoch,
                CpuPercent = cpuPercent,
                PhysicalTotalBytes = 16L * 1024 * 1024 * 1024
            });
        }
    }
}
