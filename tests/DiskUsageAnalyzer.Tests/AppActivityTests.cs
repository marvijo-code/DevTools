using System.Buffers.Binary;
using System.Text;
using DevTools.Models;
using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class AppActivityTests
{
    [Fact]
    public void OnlyLiveFirefoxProcessLinksEnableTheEndAction()
    {
        var savedTab = new AppActivityEntry("saved", "Tab", "Firefox tab", "", "", "", null);
        var liveTab = new AppActivityEntry("live", "Tab", "Firefox tab", "", "", "", 42);
        var thread = new AppActivityEntry("thread", "Thread", "Codex thread", "", "", "", null);
        Assert.False(savedTab.CanEndProcess);
        Assert.Contains("Connect Firefox", savedTab.EndProcessHint);
        Assert.True(liveTab.CanEndProcess);
        Assert.Contains("shared", liveTab.EndProcessHint);
        Assert.False(thread.CanEndProcess);
        Assert.False(thread.IsFirefoxTab);
    }

    [Fact]
    public void RefreshRetainsTheSelectedThreadObjectAndUpdatesItsTitle()
    {
        var selected = new AppActivityEntry("one", "Old name", "Codex thread", "Old time", "Details", "one", null) { IsSelected = true };
        var section = new ProcessKillSection("Codex", "summary", "", [], [selected], "Recent threads");
        section.UpdateFrom(new("Codex", "summary", "", [],
            [new("one", "New name", "Codex thread", "New time", "Details", "one", null)], "Recent threads"));
        Assert.Same(selected, Assert.Single(section.Activities));
        Assert.True(selected.IsSelected);
        Assert.Equal("New name", selected.Title);
        Assert.Equal("New time", selected.Subtitle);
    }

    [Fact]
    public void SortDirectionAppearsInTheColumnHeader()
    {
        var app = new ProcessKillSection("Codex", "", "", [],
            [new("one", "Thread", "Codex thread", "", "", "", null)], "Recent threads");
        var result = Assert.Single(ProcessKillFilter.Apply([app], null, ProcessSortColumn.Memory, false));
        Assert.Equal("Memory ↑", result.MemoryColumnLabel);
        Assert.Equal("Process", result.ProcessColumnLabel);
    }

    [Fact]
    public void FirefoxSnapshotUsesCurrentHistoryEntryAndExcludesClosedAndPrivateTabs()
    {
        var json = """
            {"windows":[{"tabs":[
              {"index":2,"entries":[{"title":"Previous page"},{"title":"Current page","url":"https://example.org/current"}]},
              {"isPrivate":true,"entries":[{"title":"Private tab"}]}
            ],"_closedTabs":[{"entries":[{"title":"Closed tab"}]}]},
            {"isPrivate":true,"tabs":[{"entries":[{"title":"Private window"}]}]}],
            "_closedWindows":[{"tabs":[{"entries":[{"title":"Closed window"}]}]}]}
            """;
        var tabs = FirefoxSessionReader.ParseTabs(Encoding.UTF8.GetBytes(json), "test-profile", new DateTime(2026, 9, 7, 12, 0, 0));
        var tab = Assert.Single(tabs);
        Assert.Equal("Current page", tab.Title);
        Assert.Equal("https://example.org/current", tab.Reference);
        Assert.Null(tab.ProcessId);
        Assert.Equal("Not available", tab.MemoryLabel);
    }

    [Fact]
    public void DecoderHandlesOverlappingMatchesAndRejectsBadOffsets()
    {
        var data = new byte[17];
        "mozLz40\0"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 5);
        data[12] = 0x10; data[13] = (byte)'a'; data[14] = 1; data[15] = 0; data[16] = 0;
        Assert.Equal("aaaaa", Encoding.UTF8.GetString(FirefoxSessionReader.Decode(data)));
        data[14] = 0;
        Assert.Throws<InvalidDataException>(() => FirefoxSessionReader.Decode(data));
        Assert.Throws<InvalidDataException>(() => FirefoxSessionReader.Decode([1, 2, 3]));
    }

    [Fact]
    public void DecoderHandlesLiteralOnlyBlockAndRejectsTruncation()
    {
        var text = Encoding.UTF8.GetBytes("{\"windows\":[]}");
        var data = new byte[13 + text.Length];
        "mozLz40\0"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), text.Length);
        data[12] = (byte)(text.Length << 4);
        text.CopyTo(data.AsSpan(13));
        Assert.Equal(text, FirefoxSessionReader.Decode(data));
        Assert.Throws<InvalidDataException>(() => FirefoxSessionReader.Decode(data[..^1]));
    }

    [Fact]
    public void ThreadSearchRetainsTheAppAndItsSharedMemoryWithoutInventingThreadMemory()
    {
        var thread = new AppActivityEntry("codex:one", "Fix mouse double-click issue", "Codex thread", "Updated today", "Recent thread", "one", null);
        var process = new ProcessKillEntry(42, 0, "Session Window", "app.exe", "app.exe", "app.exe", 512, "512 B", "PID 42");
        var app = new ProcessKillSection("Codex", "1 process", "app.exe", [process], [thread], "Recent threads", "Shared app processes");
        var result = Assert.Single(ProcessKillFilter.Apply([app], "mouse"));
        Assert.Equal(thread, Assert.Single(result.Activities));
        Assert.Equal(512, Assert.Single(result.Entries).WorkingSetBytes);
        Assert.Null(thread.ProcessId);
    }

    [Fact]
    public void RecentCodexTitlesAreDistinctAndNeverAssignedByMemorySize()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, """
                {"id":"one","thread_name":"Fix mouse double-click issue","updated_at":"2026-09-07T10:00:00Z"}
                {"id":"two","thread_name":"Fix BT Raca","updated_at":"2026-09-07T11:00:00Z"}
                {"id":"one","thread_name":"Fix mouse double-click issue","updated_at":"2026-09-07T12:00:00Z"}
                """);
            var catalog = CodexSessionCatalog.FromFile(file);
            Assert.Equal(2, catalog.RecentThreads().Count);
            Assert.Equal("Fix mouse double-click issue", catalog.RecentThreads()[0].Title);
            var name = ProcessDisplayNameResolver.Resolve(@"""C:\Program Files\WindowsApps\OpenAI.Codex\app\ChatGPT.exe"" --type=renderer --renderer-client-id=3",
                "ChatGPT.exe", int.MaxValue, 1, 500_000_000, catalog);
            Assert.DoesNotContain("Fix mouse", name);
            Assert.DoesNotContain("Fix BT", name);
        }
        finally { File.Delete(file); }
    }
}
