using System.Diagnostics;
using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

// Local integration checks are opt-in and use only an explicitly selected Firefox test port.
public sealed class LiveProcessIntegrationTests
{
    [LocalFirefoxFact]
    public async Task RealFirefoxTitlesJoinRealProcessMemory()
    {
        var port = int.Parse(Environment.GetEnvironmentVariable("DEVTOOLS_TEST_FIREFOX_PORT") ?? throw new InvalidOperationException("Set the isolated Firefox test port."));
        await FirefoxTabBridge.ConnectAsync(port);
        var titles = await FirefoxTabBridge.ReadProcessTitlesAsync();
        Assert.NotEmpty(titles);
        var snapshots = new RunningProcessCollector().Capture();
        Assert.Contains(snapshots, snapshot => titles.ContainsKey(snapshot.ProcessId) &&
            snapshot.CommandLine.Contains("-contentproc", StringComparison.OrdinalIgnoreCase));
        foreach (var pair in titles)
        {
            using var process = Process.GetProcessById(pair.Key);
            Assert.Equal("firefox", process.ProcessName, ignoreCase: true);
            var row = Assert.Single(snapshots, snapshot => snapshot.ProcessId == pair.Key);
            Assert.Equal(pair.Value, row.BrowserTabs);
            Assert.True(row.WorkingSetBytes > 0);
        }
        Assert.NotNull(SystemInformerIntegration.FindExecutable());
        Assert.Contains(snapshots, snapshot => snapshot.ProcessId == Environment.ProcessId);
        var sections = new ProcessKillCatalog().CaptureSections();
        Assert.All(titles.Keys, pid => Assert.Single(sections.SelectMany(section => section.Entries), entry => entry.ProcessId == pid));
    }
}

public sealed class LocalFirefoxFactAttribute : FactAttribute
{
    public LocalFirefoxFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_LIVE_TESTS") != "1")
            Skip = "Set RUN_LIVE_TESTS=1 and DEVTOOLS_TEST_FIREFOX_PORT for the isolated Firefox check.";
    }
}
