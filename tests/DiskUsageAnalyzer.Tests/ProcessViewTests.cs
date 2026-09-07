using System.Text.Json;
using DevTools.Models;
using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class ProcessViewTests
{
    [Fact]
    public void CompactExecutableKeepsFullPathAndKnownRoleAvailable()
    {
        const string path = @"C:\Program Files\WindowsApps\OpenAI.Codex_26.901.4073.0_x64__2p2nqsd0c76g0\app\ChatGPT.exe";
        var command = $"\"{path}\" --type=gpu-process";
        var entry = new ProcessKillEntry(42, 1, "GPU Process", command, path,
            ProcessCommandLineResolver.FormatStartPathDisplay(command, path), 1000, "1 KB", "PID 42");
        Assert.Equal("ChatGPT.exe", entry.ExecutableName);
        Assert.Equal("Graphics process", entry.Role);
        Assert.Equal("PID 42", entry.ProcessIdLabel);
        Assert.Contains(path, entry.StartPath);
        Assert.Contains(path, entry.CopyText);
        Assert.Equal(command, entry.CommandLine);
    }

    [Fact]
    public void RefreshUpdatesCompactLabelsWithoutLosingSelection()
    {
        var entry = Entry(1, "Old", 100);
        entry.IsSelected = true;
        var notifications = new List<string?>();
        entry.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        entry.UpdateFrom(new(1, 0, "Firefox", @"C:\Firefox\firefox.exe", @"C:\Firefox\firefox.exe",
            @"C:\Firefox\firefox.exe", 200, "200 B", "PID 1"));
        Assert.Equal("firefox.exe", entry.ExecutableName);
        Assert.Equal("Browser main process", entry.Role);
        Assert.Equal("Actions for Firefox, PID 1", entry.ActionsLabel);
        Assert.True(entry.IsSelected);
        Assert.Contains(nameof(ProcessKillEntry.ExecutableName), notifications);
        Assert.Contains(nameof(ProcessKillEntry.Role), notifications);
        Assert.Contains(nameof(ProcessKillEntry.ActionsLabel), notifications);
    }

    [Fact]
    public void MissingExecutableHasAnExplicitLabelAndNoInventedPath()
    {
        var entry = Entry(1, "System", 0, "");
        Assert.Equal("Path unavailable", entry.ExecutableName);
        Assert.Empty(entry.Role);
        Assert.Equal("System (PID 1)", entry.CopyText);
    }

    private static ProcessKillEntry Entry(int pid, string name, long bytes, string path = "C:\\Apps\\app.exe") =>
        new(pid, 1, name, path, path, path, bytes, bytes.ToString(), "PID " + pid);

    [Theory]
    [InlineData(ProcessSortColumn.Process, false, 2)]
    [InlineData(ProcessSortColumn.Process, true, 1)]
    [InlineData(ProcessSortColumn.StartPath, false, 1)]
    [InlineData(ProcessSortColumn.StartPath, true, 2)]
    [InlineData(ProcessSortColumn.Memory, false, 2)]
    [InlineData(ProcessSortColumn.Memory, true, 1)]
    public void SortOrdersRowsAndRetainsExpansion(ProcessSortColumn column, bool descending, int firstPid)
    {
        var source = new ProcessKillSection("App", "summary", "C:\\Apps", [
            Entry(1, "Zulu", 1000, "C:\\A.exe"), Entry(2, "alpha", 20, "C:\\Z.exe")]) { IsExpanded = true };
        var sorted = ProcessKillFilter.Apply([source], null, column, descending);
        Assert.Equal(firstPid, sorted[0].Entries[0].ProcessId);
        Assert.True(sorted[0].IsExpanded);
        Assert.Equal("summary", sorted[0].Summary);
    }

    [Fact]
    public void RefreshUpdatesMemoryAndTitlesWithoutReplacingRowOrExpansion()
    {
        var original = Entry(1, "Old title", 100);
        var section = new ProcessKillSection("Firefox", "Old", "C:\\old", [original]) { IsExpanded = true };
        var notifications = new List<string?>();
        original.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        section.UpdateFrom(new("Firefox", "New", "C:\\new", [Entry(2, "Other", 300), Entry(1, "New title", 200)]));
        Assert.Same(original, section.Entries[1]);
        Assert.Equal(200, original.WorkingSetBytes);
        Assert.Equal("New title", original.DisplayName);
        Assert.True(section.IsExpanded);
        Assert.Contains(nameof(ProcessKillEntry.WorkingSetBytes), notifications);
    }

    [Fact]
    public void ViewStateRoundTripPreservesFilterSortAndExplicitCollapsedChoice()
    {
        var state = new ProcessViewState { FilterText = "Firefox", ExpandedSections = new() { ["Firefox"] = true, ["ChatGPT"] = false } };
        state.ToggleSort(ProcessSortColumn.Process);
        var restored = JsonSerializer.Deserialize<ProcessViewState>(JsonSerializer.Serialize(state))!;
        restored.Normalize();
        Assert.Equal("Firefox", restored.FilterText);
        Assert.Equal(ProcessSortColumn.Process, restored.SortColumn);
        Assert.False(restored.SortDescending);
        Assert.True(restored.ExpandedSections["firefox"]);
        Assert.False(restored.ExpandedSections["chatgpt"]);
        restored.ToggleSort(ProcessSortColumn.Process);
        Assert.True(restored.SortDescending);
    }

    [Fact]
    public void InvalidSavedSortReturnsToMemory()
    {
        var state = JsonSerializer.Deserialize<ProcessViewState>("{\"SortColumn\":999,\"ExpandedSections\":null,\"FilterText\":null}")!;
        state.Normalize();
        Assert.Equal(ProcessSortColumn.Memory, state.SortColumn);
        Assert.Empty(state.ExpandedSections);
        Assert.Equal(string.Empty, state.FilterText);
    }
}
