using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class FirefoxTabBridgeTests
{
    [Fact]
    public void Parse_MapsParentProcessAndPreservesMissingProcess()
    {
        var tabs = FirefoxTabBridge.ParseTabs("""
            [{"title":"Process Manager","processId":-1},{"title":"Unloaded","discarded":true}]
            """, 42);
        Assert.Equal(42, tabs[0].ProcessId);
        Assert.Equal(0, tabs[1].ProcessId);
        Assert.True(tabs[1].Discarded);
    }

    [Fact]
    public void SharedProcess_ListsBothTitlesWithoutDuplicatingMemoryRows()
    {
        var titles = FirefoxTabBridge.GroupTitles([
            new("First tab", 42, false, false), new("Second tab", 42, false, false),
            new("Private tab", 43, false, true), new("Unloaded tab", 0, true, false)]);
        Assert.Single(titles);
        Assert.Equal("First tab" + Environment.NewLine + "Second tab", titles[42]);
    }
}
