using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class SystemInformerIntegrationTests
{
    [Fact]
    public void Inspect_UsesSeparateArgumentsAndTheSelectedProcess()
    {
        var start = SystemInformerIntegration.CreateStartInfo(@"C:\Program Files\SystemInformer\SystemInformer.exe", 1234);
        Assert.True(start.UseShellExecute);
        Assert.Equal(new[] { "-selectpid", "1234", "-selecttab", "Processes" }, start.ArgumentList);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Inspect_RejectsInvalidProcessIds(int processId) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SystemInformerIntegration.CreateStartInfo("SystemInformer.exe", processId));

    [Fact]
    public void Snapshot_PreservesAllDistinctProcessesWithIdenticalCommandLines()
    {
        var processes = Enumerable.Range(1, 75).Select(id => new RunningProcessSnapshot(
            id, 0, "firefox.exe", "firefox.exe -contentproc", "firefox.exe", "Firefox", id));
        var snapshot = RunningProcessCollector.OrderSnapshot(processes);
        Assert.Equal(75, snapshot.Count);
        Assert.Equal(75, snapshot.Select(process => process.ProcessId).Distinct().Count());
        Assert.Equal(75, snapshot[0].WorkingSetBytes);
    }
}
