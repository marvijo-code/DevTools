using DevTools.Models;
using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests.FolderSizes;

public sealed class DiskUsageExpansionTests
{
    [Fact]
    public async Task ExpandEntryAsync_ReturnsChildFoldersSortedBySize()
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-expand-" + Guid.NewGuid().ToString("N"));
        var small = Path.Combine(root, "small");
        var large = Path.Combine(root, "large");
        Directory.CreateDirectory(small);
        Directory.CreateDirectory(large);
        await File.WriteAllBytesAsync(Path.Combine(small, "a.bin"), new byte[1024]);
        await File.WriteAllBytesAsync(Path.Combine(large, "b.bin"), new byte[65536]);

        try
        {
            var analyzer = new DevTools.Services.DiskUsageAnalyzer();
            var entry = DiskUsageEntry.CreateCompleted(root, true, 66560, "65 KB");
            entry.SetExpandable(true);

            var children = await analyzer.ExpandEntryAsync(entry, CancellationToken.None);

            var largeEntry = Assert.Single(children, child => child.Path.Equals(large, StringComparison.OrdinalIgnoreCase));
            var smallEntry = Assert.Single(children, child => child.Path.Equals(small, StringComparison.OrdinalIgnoreCase));
            Assert.True(largeEntry.SizeBytes > smallEntry.SizeBytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExpandEntryAsync_ReturnsEmptyWhenFolderDoesNotExist()
    {
        var analyzer = new DevTools.Services.DiskUsageAnalyzer();
        var entry = DiskUsageEntry.CreateCompleted(
            Path.Combine(Path.GetTempPath(), "devtools-missing-" + Guid.NewGuid().ToString("N")),
            false,
            null,
            "Not found");
        entry.SetExpandable(true);

        var children = await analyzer.ExpandEntryAsync(entry, CancellationToken.None);

        Assert.Empty(children);
    }

    [Fact]
    public void SetChildren_MarksChildrenAsLoadedSoExpansionIsNotRepeated()
    {
        var entry = DiskUsageEntry.CreateCompleted(@"C:\Users", true, 100, "100 B");
        entry.SetExpandable(true);

        Assert.False(entry.HasLoadedChildren);
        Assert.True(entry.CanExpand);

        entry.SetChildren([DiskUsageEntry.CreateCompleted(@"C:\Users\user", true, 80, "80 B")]);

        Assert.True(entry.HasLoadedChildren);
        Assert.True(entry.HasChildren);
    }
}
