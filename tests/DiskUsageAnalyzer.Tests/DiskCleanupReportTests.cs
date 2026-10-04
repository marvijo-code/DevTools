using DevTools.Services.DiskCleanup;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class DiskCleanupReportTests
{
    private static readonly string[] PreviewOutput =
    [
        "C: free before:    37.60 GB",
        "Mode: DRY RUN (pass -Execute to delete)",
        "",
        "== Caches ==",
        "    0.50 GB  npm cache",
        "",
        "=== Summary ===",
        "    3.20 GB  Caches (4 items)",
        "    0.10 GB  BuildArtifacts (7 items)",
        "   10.88 GB  PublishedVideos (61 items)",
        "",
        "Largest items:",
        "      2.06 GB  C:\\dev\\video-episodes\\some-episode\\work  would delete",
        "",
        "Total reclaimable:     7.77 GB",
        "",
        "Still worth doing by hand:"
    ];

    private static readonly string[] ExecuteOutput =
    [
        "C: free before:    37.60 GB",
        "Mode: EXECUTE",
        "    3.20 GB  Caches (4 items)",
        "   10.88 GB  PublishedVideos (61 items)",
        "Total reclaimed (measured):     7.77 GB",
        "C: free after :    45.40 GB  (delta 7.80 GB)"
    ];

    [Fact]
    public void ParsesLaneTotalsFromAPreview()
    {
        var report = DiskCleanupReport.Parse(PreviewOutput);

        Assert.Equal(3, report.Lanes.Count);
        Assert.Equal("Caches", report.Lanes[0].Lane);
        Assert.Equal(4, report.Lanes[0].ItemCount);
        Assert.Equal("PublishedVideos", report.Lanes[2].Lane);
        Assert.Equal(61, report.Lanes[2].ItemCount);
        Assert.Equal((long)Math.Round(10.88 * 1024 * 1024 * 1024), report.Lanes[2].Bytes);
    }

    [Fact]
    public void ParsesTheReclaimableTotalAndFreeSpaceBefore()
    {
        var report = DiskCleanupReport.Parse(PreviewOutput);

        Assert.NotNull(report.TotalReclaimableBytes);
        Assert.Null(report.TotalReclaimedBytes);
        Assert.Equal((long)Math.Round(7.77 * 1024 * 1024 * 1024), report.TotalReclaimableBytes);
        Assert.Equal("37.60 GB", report.FreeBeforeText);
        Assert.Null(report.FreeAfterText);
    }

    [Fact]
    public void ParsesTheReclaimedTotalAndFreeSpaceAfter()
    {
        var report = DiskCleanupReport.Parse(ExecuteOutput);

        Assert.NotNull(report.TotalReclaimedBytes);
        Assert.Equal((long)Math.Round(7.77 * 1024 * 1024 * 1024), report.TotalReclaimedBytes);
        Assert.Null(report.TotalReclaimableBytes);
        Assert.Equal(2, report.Lanes.Count);
        Assert.Equal("45.40 GB  (delta 7.80 GB)", report.FreeAfterText);
    }

    [Fact]
    public void IgnoresNoiseAndHandlesEmptyInput()
    {
        var report = DiskCleanupReport.Parse(["", "Some unrelated line", "  ", "Total", "GB  bad line"]);

        Assert.Empty(report.Lanes);
        Assert.Null(report.TotalReclaimableBytes);
        Assert.Null(report.TotalReclaimedBytes);
    }

    [Fact]
    public void FormatsAnUnknownSizeReadably()
    {
        Assert.Equal("an unknown amount", DiskCleanupService.FormatSize(null));
        Assert.Equal("1.00 GB", DiskCleanupService.FormatSize(1024L * 1024 * 1024));
    }

    private static string ExistingShell => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void ReportsAMissingScriptInsteadOfCrashing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "devtools-missing-cleanup-script.ps1");
        var service = new DiskCleanupService(powerShellPath: ExistingShell, scriptPath: missing);

        var reason = service.GetUnavailableReason();

        Assert.NotNull(reason);
        Assert.Contains(missing, reason);
        Assert.False(service.IsAvailable);
    }

    [Fact]
    public void IsAvailableWhenBothTheShellAndTheScriptExist()
    {
        var script = Path.Combine(Path.GetTempPath(), $"devtools-cleanup-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, "# stub");
        try
        {
            var service = new DiskCleanupService(powerShellPath: ExistingShell, scriptPath: script);

            Assert.Null(service.GetUnavailableReason());
            Assert.True(service.IsAvailable);
            Assert.Equal(script, service.ScriptPath);
        }
        finally
        {
            File.Delete(script);
        }
    }
}
