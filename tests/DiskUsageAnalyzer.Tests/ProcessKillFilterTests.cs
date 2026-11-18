using DevTools.Models;
using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class ProcessKillFilterTests
{
    [Fact]
    public void Apply_OrdersSectionsAndEntriesByMemoryDescending()
    {
        var sections =
            new List<ProcessKillSection>
            {
                CreateSection(
                    "Small App",
                    CreateEntry(1, "Small", 10 * 1024 * 1024),
                    CreateEntry(2, "Tiny", 4 * 1024 * 1024)),
                CreateSection(
                    "Large App",
                    CreateEntry(3, "Large", 900 * 1024 * 1024),
                    CreateEntry(4, "Medium", 200 * 1024 * 1024))
            };

        var filtered = ProcessKillFilter.Apply(sections, filterText: null);

        Assert.Equal("Large App", filtered[0].Title);
        Assert.Equal(900L * 1024 * 1024, filtered[0].Entries[0].WorkingSetBytes);
        Assert.Equal(200L * 1024 * 1024, filtered[0].Entries[1].WorkingSetBytes);
        Assert.Equal("Small App", filtered[1].Title);
        Assert.Equal(10L * 1024 * 1024, filtered[1].Entries[0].WorkingSetBytes);
    }

    [Fact]
    public void Apply_FiltersBySearchTerms()
    {
        var sections =
            new List<ProcessKillSection>
            {
                CreateSection(
                    "Codex",
                    CreateEntry(10, "Codex — Renderer", 100 * 1024 * 1024, "PID 10 | Codex.exe"),
                    CreateEntry(11, "Codex — GPU", 80 * 1024 * 1024, "PID 11 | Codex.exe")),
                CreateSection(
                    "Chrome",
                    CreateEntry(20, "Chrome Tab", 50 * 1024 * 1024, "PID 20 | chrome.exe"))
            };

        var filtered = ProcessKillFilter.Apply(sections, "renderer codex");

        Assert.Single(filtered);
        Assert.Equal("Codex", filtered[0].Title);
        Assert.Single(filtered[0].Entries);
        Assert.Equal(10, filtered[0].Entries[0].ProcessId);
    }

    [Theory]
    [InlineData("workspace\r\nHELPER 8411", true)]
    [InlineData("workspace helper 8412", false)]
    [InlineData("queuedworker", false)]
    public void Apply_MatchesTermsAcrossFieldsIncludingParentPid(string filter, bool shouldMatch)
    {
        var entry = new ProcessKillEntry(1234, 8411, "Worker", "app.exe --helper", "app.exe", "app.exe", 512,
            "worker", "queued");
        var section = new ProcessKillSection("Build server", "1 process", @"C:\Workspace", [entry]);

        var filtered = ProcessKillFilter.Apply([section], filter);

        if (shouldMatch)
        {
            Assert.Same(entry, Assert.Single(Assert.Single(filtered).Entries));
        }
        else
        {
            Assert.Empty(filtered);
        }
    }

    private static ProcessKillSection CreateSection(string title, params ProcessKillEntry[] entries)
    {
        return new ProcessKillSection(title, $"{entries.Length} process(es)", string.Empty, entries);
    }

    private static ProcessKillEntry CreateEntry(
        int processId,
        string displayName,
        long workingSetBytes,
        string? note = null,
        string? startPath = null)
    {
        return new ProcessKillEntry(
            processId,
            parentProcessId: 0,
            displayName,
            commandLine: displayName,
            executablePath: startPath ?? $@"C:\Apps\{displayName.Replace(' ', '-')}.exe",
            startPath: startPath ?? $@"C:\Apps\{displayName.Replace(' ', '-')}.exe",
            workingSetBytes,
            usage: $"{workingSetBytes / (1024 * 1024)} MB",
            note ?? $"PID {processId}");
    }
}
