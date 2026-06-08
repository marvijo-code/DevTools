using DevTools.Models;
using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class ProcessKillCatalogTests
{
    [Theory]
    [InlineData("Codex", "Codex — GPU Process", "GPU Process")]
    [InlineData("Codex", "Codex", "Codex")]
    [InlineData("Cursor", "Cursor — Session Window", "Session Window")]
    public void StripChildDisplayName_RemovesParentPrefix(string groupTitle, string displayName, string expected)
    {
        var childName = ProcessKillCatalog.StripChildDisplayName(groupTitle, displayName);

        Assert.Equal(expected, childName);
    }

    [Fact]
    public void ResolveEntryDisplayName_UsesExecutableBaseNameWhenGroupMatchesDisplayName()
    {
        var process = new RunningProcessSnapshot(
            132992,
            100,
            "VBCSCompiler.exe",
            "\"C:\\Program Files\\dotnet\\sdk\\10.0.108\\Roslyn\\bincore\\VBCSCompiler.exe\" -pipename:abc",
            @"C:\Program Files\dotnet\sdk\10.0.108\Roslyn\bincore\VBCSCompiler.exe",
            "VBCSCompiler",
            20_000_000);

        var childName = ProcessKillCatalog.ResolveEntryDisplayName("VBCSCompiler", process);

        Assert.Equal("VBCSCompiler", childName);
    }

    [Fact]
    public void BuildCopyText_IncludesNamePidAndExecutablePath()
    {
        var copyText = ProcessKillEntry.BuildCopyText(
            "Session Window",
            131352,
            @"C:\Users\user\AppData\Local\Google\Chrome\Application\chrome.exe");

        Assert.Equal(
            $"Session Window (PID 131352){Environment.NewLine}C:\\Users\\user\\AppData\\Local\\Google\\Chrome\\Application\\chrome.exe",
            copyText);
    }

    [Fact]
    public void ResolveSectionStartPath_UsesParentExecutable()
    {
        var processes = new[]
        {
            new RunningProcessSnapshot(
                1,
                0,
                "Chrome.exe",
                "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\"",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                "Chrome",
                200_000_000),
            new RunningProcessSnapshot(
                2,
                1,
                "Chrome.exe",
                "\"C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe\" --type=renderer",
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                "Chrome — Session Window",
                120_000_000)
        };

        var startPath = ProcessKillCatalog.ResolveSectionStartPath("Chrome", processes);

        Assert.Equal(@"C:\Program Files\Google\Chrome\Application\chrome.exe", startPath);
    }
}
