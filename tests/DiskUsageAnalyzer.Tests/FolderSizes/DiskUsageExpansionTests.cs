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
        var progress = new CancelOnProgress(stopAfterInventory);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            analyzer.CaptureAsync(progress, stopAfterInventory.Token));
        var first = Assert.IsType<DiskUsageProgress>(progress.First);
        Assert.NotEmpty(Assert.IsAssignableFrom<IReadOnlyList<string>>(first.BrowseRoots));
        Assert.DoesNotContain("Disk ", Assert.Single(first.Sections, section => section.Title == "Drive Summary").Summary);
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
    public async Task Refresh_ReusesMountedOverlaysUntilTheVolumeTreeIsPublished()
    {
        var analyzer = new DevTools.Services.DiskUsageAnalyzer();
        var fixture = Directory.CreateTempSubdirectory("devtools-overlay-refresh-");
        var older = fixture.CreateSubdirectory("older");
        var current = fixture.CreateSubdirectory("current");
        var olderFile = Path.Combine(older.FullName, "old.bin");
        var currentFile = Path.Combine(current.FullName, "new.bin");
        try
        {
            await File.WriteAllBytesAsync(olderFile, new byte[512]);
            await File.WriteAllBytesAsync(currentFile, new byte[1024]);
            var drive = new DriveDescriptor(fixture.Root.FullName, "Fixture", "NTFS", null)
            {
                VolumeName = @"\\?\Volume{b9b6d0b5-6ffc-4088-b889-b293f453d90b}\"
            };
            var initial = analyzer.ReplaceVolumeInventory([drive], []);
            Assert.True(await analyzer.SizeFolderNowAsync(older.FullName, CancellationToken.None));
            Assert.True(analyzer.TryFindNode(older.FullName, out var oldTree, out _));

            var refresh = analyzer.ReplaceVolumeInventory([drive], []);
            Assert.True(analyzer.TryFindNode(older.FullName, out var pendingTree, out _));
            Assert.Same(oldTree, pendingTree);
            analyzer.PublishVolumeTree(initial, drive.RootPath, oldTree);
            Assert.Empty(analyzer.Trees);

            Assert.True(await analyzer.SizeFolderNowAsync(current.FullName, CancellationToken.None));
            Assert.True(analyzer.TryFindNode(current.FullName, out var newTree, out _));
            var fullTree = new FolderTreeBuilder(drive.RootPath).Build("Fixture", TimeSpan.Zero);
            analyzer.PublishVolumeTree(refresh, drive.RootPath, fullTree);
            Assert.False(analyzer.TryFindNode(older.FullName, out _, out _));
            Assert.True(analyzer.TryFindNode(current.FullName, out var keptTree, out _));
            Assert.Same(newTree, keptTree);

            var other = drive with { VolumeName = @"\\?\Volume{c403be59-3402-4f7f-b5fa-0a63e7ba88ee}\" };
            analyzer.ReplaceVolumeInventory([other], []);
            Assert.Empty(analyzer.Trees);
            Assert.False(analyzer.TryFindNode(current.FullName, out _, out _));
        }
        finally
        {
            File.Delete(currentFile);
            File.Delete(olderFile);
            current.Delete();
            older.Delete();
            fixture.Delete();
        }
    }

    [Fact]
    public async Task Refresh_PrefersACurrentAncestorScanOverAnOlderExactMatch()
    {
        var analyzer = new DevTools.Services.DiskUsageAnalyzer();
        var fixture = Directory.CreateTempSubdirectory("devtools-overlay-ancestor-");
        var child = fixture.CreateSubdirectory("child");
        var file = Path.Combine(child.FullName, "data.bin");
        try
        {
            var drive = new DriveDescriptor(fixture.Root.FullName, "Fixture", "NTFS", null)
            {
                VolumeName = @"\\?\Volume{b9b6d0b5-6ffc-4088-b889-b293f453d90b}\"
            };
            analyzer.ReplaceVolumeInventory([drive], []);
            await File.WriteAllBytesAsync(file, new byte[512]);
            Assert.True(await analyzer.SizeFolderNowAsync(child.FullName, CancellationToken.None));
            Assert.True(analyzer.TryFindNode(child.FullName, out var olderTree, out _));

            analyzer.ReplaceVolumeInventory([drive], []);
            await File.WriteAllBytesAsync(file, new byte[2048]);
            Assert.True(await analyzer.SizeFolderNowAsync(fixture.FullName, CancellationToken.None));

            var alternatePath = child.FullName.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            Assert.True(analyzer.TryFindNode(alternatePath, out var currentTree, out var node));
            Assert.NotSame(olderTree, currentTree);
            Assert.Equal(2048, currentTree!.GetTotalBytes(node));
            Assert.False(analyzer.TryFindNode(child.FullName + "-other", out _, out _));
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
        public DiskUsageProgress? First { get; private set; }

        public void Report(DiskUsageProgress value)
        {
            First ??= value;
            source.Cancel();
        }
    }
}
