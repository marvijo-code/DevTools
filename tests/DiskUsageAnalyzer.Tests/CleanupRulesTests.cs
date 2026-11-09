using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class CleanupRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "devtools-cleanup-tests-" + Guid.NewGuid().ToString("N"));

    public CleanupRulesTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public void FindSupersededBrowserFolders_KeepsNewestOfEachBrowserAndMcpProfiles()
    {
        var superseded = CleanupRules.FindSupersededBrowserFolders(
        [
            "chromium-1208", "chromium-1217", "chromium_headless_shell-1208", "chromium_headless_shell-1217",
            "ffmpeg-1011", "winldd-1007", "mcp-chrome-15fda5f", "mcp-chrome-1234567", ".links", "b"
        ]);

        Assert.Equal(["chromium-1208", "chromium_headless_shell-1208"], superseded);
    }

    [Theory]
    [InlineData("21.94GB", 21_940_000_000L)]
    [InlineData("92.55MB", 92_550_000L)]
    [InlineData("2.002kB", 2_002L)]
    [InlineData("0B", 0L)]
    [InlineData("5.303GB (24%)", 5_303_000_000L)]
    public void TryParseDockerSize_ReadsDockerUnits(string text, long expected)
    {
        Assert.Equal(expected, CleanupRules.TryParseDockerSize(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("N/A")]
    public void TryParseDockerSize_ReturnsNullForUnreadableText(string? text)
    {
        Assert.Null(CleanupRules.TryParseDockerSize(text));
    }

    [Theory]
    [InlineData("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04", true)]
    [InlineData("nextcloud:33.0.3-apache", true)]
    [InlineData("postgres:16", true)]
    [InlineData("192.168.168.205:5000/webmvc:master", false)]
    [InlineData("redis:7", false)]
    public void IsDataHoldingImage_FlagsDatabaseImages(string image, bool expected)
    {
        Assert.Equal(expected, CleanupRules.IsDataHoldingImage(image));
    }

    [Fact]
    public void DeleteFiles_DryRunCountsOnlyOldFilesAndDeletesNothing()
    {
        var oldFile = WriteFile("old.log", 100, DateTime.UtcNow.AddDays(-5));
        WriteFile("new.log", 50, DateTime.UtcNow);
        var cutoff = DateTime.UtcNow.AddDays(-2);

        var tally = CleanupRules.DeleteFiles(_root, item => CleanupRules.IsOlderThan(item, cutoff), dryRun: true, CancellationToken.None);

        Assert.Equal(100, tally.Bytes);
        Assert.Equal(1, tally.FileCount);
        Assert.True(File.Exists(oldFile));
    }

    [Fact]
    public void DeleteFiles_RemovesOldFilesAndOldEmptyFoldersButKeepsRecentOnes()
    {
        var oldFile = WriteFile(Path.Combine("stale", "old.tmp"), 10, DateTime.UtcNow.AddDays(-5));
        var newFile = WriteFile(Path.Combine("live", "new.tmp"), 10, DateTime.UtcNow);
        Directory.SetLastWriteTimeUtc(Path.Combine(_root, "stale"), DateTime.UtcNow.AddDays(-5));
        var cutoff = DateTime.UtcNow.AddDays(-2);

        var tally = CleanupRules.DeleteFiles(_root, item => CleanupRules.IsOlderThan(item, cutoff), dryRun: false, CancellationToken.None);

        Assert.Equal(1, tally.FileCount);
        Assert.False(File.Exists(oldFile));
        Assert.False(Directory.Exists(Path.Combine(_root, "stale")));
        Assert.True(File.Exists(newFile));
    }

    [Fact]
    public void DeleteFiles_DoesNotFollowJunctions()
    {
        var outside = Path.Combine(_root, "outside");
        var protectedFile = Path.Combine(outside, "keep.txt");
        Directory.CreateDirectory(outside);
        File.WriteAllText(protectedFile, "keep");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(target, "link"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating links needs Developer Mode or admin rights; without them there is nothing to test.
            return;
        }

        CleanupRules.DeleteFiles(target, _ => true, dryRun: false, CancellationToken.None);

        Assert.True(File.Exists(protectedFile));
    }

    private string WriteFile(string relativePath, int length, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
