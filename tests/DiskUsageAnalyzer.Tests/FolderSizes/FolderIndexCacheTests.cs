using DevTools.Services.FolderSizes;
using Xunit;

namespace DiskUsageAnalyzer.Tests.FolderSizes;

public sealed class FolderIndexCacheTests
{
    [Fact]
    public void SaveAndLoad_PreservesTreeHashUsnCursorAndRejectsCorruption()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var originalDir = FolderIndexCache.CacheDirectory;
        var tempCache = Path.Combine(Path.GetTempPath(), "devtools-cache-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            FolderIndexCache.CacheDirectory = tempCache;
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var builder = new FolderTreeBuilder(root, trackAllocation: true);
            var dev = builder.AddChildFolder(0, "dev");
            var app = builder.AddChildFolder(dev, "app");
            builder.AddFilesToFolder(app, bytes: 10_000, files: 5, allocatedBytes: 12_288);
            var tree = builder.Build("NTFS MFT", TimeSpan.FromSeconds(1.2));

            var cursor = new UsnJournalCursor(JournalId: 0x1234_5678_9ABC_DEF0UL, NextUsn: 42_000, LowestValidUsn: 1_000);
            Assert.True(FolderIndexCache.Save(root, tree, cursor, treeHash: 0xDEAD_BEEF_CAFE_0001UL));

            Assert.True(FolderIndexCache.TryLoad(root, out var loaded, out var metadata));
            Assert.NotNull(loaded);
            Assert.NotNull(metadata);
            Assert.Equal(tree.FolderCount, loaded!.FolderCount);
            Assert.Equal(10_000, loaded.TotalBytes);
            Assert.Equal(5, loaded.TotalFiles);
            Assert.Equal(12_288, loaded.GetAllocatedBytes(0));
            Assert.Equal(0xDEAD_BEEF_CAFE_0001UL, metadata!.TreeHash);
            Assert.Equal(cursor.JournalId, metadata.UsnJournalId);
            Assert.Equal(cursor.NextUsn, metadata.NextUsn);

            // Corrupt a single byte in the middle of the index file: SHA-256 verification must reject and delete it.
            var cachePath = FolderIndexCache.GetCachePath(root);
            var raw = File.ReadAllBytes(cachePath);
            raw[raw.Length / 2] ^= 0xFF;
            File.WriteAllBytes(cachePath, raw);

            Assert.False(FolderIndexCache.TryLoad(root, out _, out _));
            Assert.False(File.Exists(cachePath));
        }
        finally
        {
            FolderIndexCache.CacheDirectory = originalDir;
            if (Directory.Exists(tempCache))
            {
                Directory.Delete(tempCache, recursive: true);
            }
        }
    }

    [Fact]
    public void ApplyDirectFileUpdates_PropagatesDeltasUpToRoot()
    {
        var builder = new FolderTreeBuilder(@"C:\");
        var dev = builder.AddChildFolder(0, "dev");
        var proj = builder.AddChildFolder(dev, "proj");
        builder.AddFilesToFolder(dev, bytes: 100, files: 1);
        builder.AddFilesToFolder(proj, bytes: 500, files: 2);
        var tree = builder.Build("NTFS MFT", TimeSpan.FromSeconds(2));

        var updated = tree.ApplyDirectFileUpdates(
            [(proj, NewDirectBytes: 1_500, NewDirectFiles: 4)],
            "NTFS MFT (USN incremental)",
            TimeSpan.FromMilliseconds(25));

        Assert.Equal(1_600, updated.TotalBytes);
        Assert.Equal(5, updated.TotalFiles);
        Assert.Equal(1_600, updated.GetTotalBytes(dev));
        Assert.Equal(1_500, updated.GetTotalBytes(proj));
        Assert.Equal(4, updated.GetFileCount(proj));
    }
}
