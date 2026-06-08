using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class DiskUsageAnalyzerEverythingTests
{
    [Fact]
    public void TryParseEverythingCsvRows_ParsesQuotedPathsAndRawByteSizes()
    {
        const string csv = """
Filename,Size
"R:\Repos\ExampleApp\.git\objects\pack\pack-a07.pack",1889616954
"R:\Temp\path, with comma\file ""quoted"".zip",12345
""";

        var rows = DevTools.Services.DiskUsageAnalyzer.TryParseEverythingCsvRows(csv);

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal(@"R:\Repos\ExampleApp\.git\objects\pack\pack-a07.pack", row.Path);
                Assert.Equal(1889616954, row.SizeBytes);
            },
            row =>
            {
                Assert.Equal(@"R:\Temp\path, with comma\file ""quoted"".zip", row.Path);
                Assert.Equal(12345, row.SizeBytes);
            });
    }
}
