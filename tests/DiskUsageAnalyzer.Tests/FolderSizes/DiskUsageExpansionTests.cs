using DevTools.Models;
using DevTools.Services;
using DevTools.Services.FolderSizes;
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
    public async Task PriorityScan_ReusesTheSameTreeByDriveLetterAndVolumeGuid()
    {
        var analyzer = new DevTools.Services.DiskUsageAnalyzer();
        using var stopAfterInventory = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            analyzer.CaptureAsync(new CancelOnProgress(stopAfterInventory), stopAfterInventory.Token));
        Assert.Empty(analyzer.Trees);

        var fixture = Directory.CreateTempSubdirectory("devtools-overlay-alias-");
        var child = fixture.CreateSubdirectory("child");
        var file = Path.Combine(child.FullName, "data.bin");
        try
        {
            await File.WriteAllBytesAsync(file, new byte[512]);
            var volume = Assert.IsType<string>(WindowsVolumePaths.GetVolumeName(fixture.Root.FullName));
            var aliasPath = Path.Combine(volume, Path.GetRelativePath(fixture.Root.FullName, fixture.FullName));

            var entry = DiskUsageEntry.CreateCompleted(aliasPath, true, 512, "512 B");
            var children = await analyzer.ExpandEntryAsync(entry, CancellationToken.None);
            Assert.Equal(child.FullName, Assert.Single(children).Path);

            Assert.True(await analyzer.SizeFolderNowAsync(aliasPath, CancellationToken.None));
            Assert.True(analyzer.TryFindNode(child.FullName, out var byLetter, out var letterNode));
            Assert.True(analyzer.TryFindNode(Path.Combine(aliasPath, child.Name), out var byGuid, out var guidNode));
            Assert.Same(byLetter, byGuid);
            Assert.Equal(letterNode, guidNode);
            Assert.Equal(512, byLetter!.GetTotalBytes(letterNode));
        }
        finally
        {
            File.Delete(file);
            child.Delete();
            fixture.Delete();
        }
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

    private sealed class CancelOnProgress(CancellationTokenSource source) : IProgress<DiskUsageProgress>
    {
        public void Report(DiskUsageProgress value) => source.Cancel();
    }
}
