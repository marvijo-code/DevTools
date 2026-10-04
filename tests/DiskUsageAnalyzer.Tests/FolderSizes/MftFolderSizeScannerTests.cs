using DevTools.Services.BiggestFiles.Ntfs;
using DevTools.Services.FolderSizes;
using Xunit;

namespace DiskUsageAnalyzer.Tests.FolderSizes;

public sealed class MftFolderSizeScannerTests
{
    private const int Root = 5;

    [Fact]
    public void RankChildFolders_RanksDirectChildrenBySubtreeSize()
    {
        var table = CreateTable(160);
        AddDirectory(table, 24, Root, "Users");
        AddDirectory(table, 25, Root, "Windows");
        AddDirectory(table, 26, Root, "Small");
        AddFile(table, 40, 24, "video.mkv", 1000);
        AddFile(table, 41, 25, "system.bin", 500);
        AddFile(table, 42, 26, "note.txt", 10);

        var directoryBytes = MftFolderSizeScanner.AggregateDirectoryBytes(table, Root, ancestry: null);
        var hits = MftFolderSizeScanner.RankChildFolders(table, Root, directoryBytes, topCount: 20, childrenPerFolder: 10);

        Assert.Collection(
            hits,
            hit => Assert.Equal(@"C:\Users", hit.FullPath),
            hit => Assert.Equal(@"C:\Windows", hit.FullPath),
            hit => Assert.Equal(@"C:\Small", hit.FullPath));
        Assert.Equal(1000, hits[0].SizeBytes);
        Assert.Equal(500, hits[1].SizeBytes);
        Assert.Equal(10, hits[2].SizeBytes);
    }

    [Fact]
    public void RankChildFolders_RollsUpNestedSizesAndAttachesChildren()
    {
        var table = CreateTable(160);
        AddDirectory(table, 24, Root, "Users");
        AddDirectory(table, 25, Root, "Windows");
        AddDirectory(table, 26, 24, "marvi");
        AddDirectory(table, 27, 26, "AppData");
        AddFile(table, 40, 27, "cache.bin", 1000);
        AddFile(table, 41, 26, "profile.dat", 100);
        AddFile(table, 42, 25, "system.bin", 50);

        var directoryBytes = MftFolderSizeScanner.AggregateDirectoryBytes(table, Root, ancestry: null);
        var hits = MftFolderSizeScanner.RankChildFolders(table, Root, directoryBytes, topCount: 20, childrenPerFolder: 10);

        Assert.Equal(2, hits.Count);
        Assert.Equal(@"C:\Users", hits[0].FullPath);
        Assert.Equal(1100, hits[0].SizeBytes);
        Assert.Equal(@"C:\Windows", hits[1].FullPath);
        Assert.Equal(50, hits[1].SizeBytes);

        var child = Assert.Single(hits[0].Children);
        Assert.Equal(@"C:\Users\user", child.FullPath);
        Assert.Equal(1100, child.SizeBytes);
    }

    [Fact]
    public void AggregateDirectoryBytes_WithAncestryFilter_ExcludesFilesOutsideTarget()
    {
        var table = CreateTable(160);
        AddDirectory(table, 24, Root, "Users");
        AddDirectory(table, 25, Root, "Windows");
        AddDirectory(table, 26, 24, "marvi");
        AddFile(table, 40, 26, "inside.bin", 700);
        AddFile(table, 41, 25, "outside.bin", 900);

        var ancestry = new byte[table.RecordCount];
        var directoryBytes = MftFolderSizeScanner.AggregateDirectoryBytes(table, 26, ancestry);
        var hits = MftFolderSizeScanner.RankChildFolders(table, 26, directoryBytes, topCount: 20, childrenPerFolder: 10);

        Assert.Equal(700, directoryBytes[26]);
        Assert.Equal(0, directoryBytes[25]);
        Assert.Empty(hits);
    }

    [Fact]
    public void RankChildFolders_SkipsZeroSizedAndNonChildRecords()
    {
        var table = CreateTable(160);
        AddDirectory(table, 24, Root, "Empty");
        AddDirectory(table, 25, 24, "Nested");
        AddDirectory(table, 26, Root, "Full");
        AddFile(table, 40, 26, "data.bin", 42);

        var directoryBytes = MftFolderSizeScanner.AggregateDirectoryBytes(table, Root, ancestry: null);
        var hits = MftFolderSizeScanner.RankChildFolders(table, Root, directoryBytes, topCount: 20, childrenPerFolder: 10);

        var hit = Assert.Single(hits);
        Assert.Equal(@"C:\Full", hit.FullPath);
        Assert.Equal(42, hit.SizeBytes);
    }

    [Fact]
    public void BuildTree_SizesEveryFolderAtEveryDepth()
    {
        var table = CreateTable(160);
        AddDirectory(table, 30, 28, "deep");
        AddDirectory(table, 24, Root, "Users");
        AddDirectory(table, 25, Root, "Windows");
        AddDirectory(table, 26, 24, "marvi");
        AddDirectory(table, 28, 26, "AppData");
        AddFile(table, 40, 30, "cache.bin", 1000);
        AddFile(table, 41, 26, "profile.dat", 100);
        AddFile(table, 42, 25, "system.bin", 50);
        AddFile(table, 43, Root, "pagefile.sys", 7);

        var tree = MftFolderSizeScanner.BuildTree(table, @"C:\", "NTFS MFT", System.Diagnostics.Stopwatch.StartNew());

        Assert.Equal(1157, tree.TotalBytes);
        Assert.Equal(4, tree.TotalFiles);
        Assert.Equal(1100, tree.GetTotalBytes(tree.Find(@"C:\Users")));
        Assert.Equal(1000, tree.GetTotalBytes(tree.Find(@"C:\Users\user\AppData")));
        Assert.Equal(1000, tree.GetTotalBytes(tree.Find(@"C:\users\USER\appdata\deep")));
        Assert.Equal(7, tree.GetDirectFileBytes(0));
        Assert.Equal(@"C:\Users\user\AppData\deep", tree.GetPath(tree.Find(@"C:\Users\user\AppData\deep")));
        Assert.Equal([@"C:\Users", @"C:\Windows"], tree.GetChildren(0).Select(tree.GetPath).ToArray());
    }

    private static MftRecordTable CreateTable(int recordCount)
    {
        return new MftRecordTable(
            @"C:\",
            recordCount,
            new uint[recordCount],
            new byte[recordCount],
            new long[recordCount],
            new long[recordCount],
            new long[recordCount],
            new string?[recordCount]);
    }

    private static void AddDirectory(MftRecordTable table, int index, uint parent, string name)
    {
        table.Parent[index] = parent;
        table.Names[index] = name;
        table.Flags[index] = MftRecordTable.FlagInUse | MftRecordTable.FlagDirectory | MftRecordTable.FlagHasName;
    }

    private static void AddFile(MftRecordTable table, int index, uint parent, string name, long size)
    {
        table.Parent[index] = parent;
        table.Names[index] = name;
        table.RealSize[index] = size;
        table.AllocSize[index] = size;
        table.Flags[index] = MftRecordTable.FlagInUse | MftRecordTable.FlagHasName | MftRecordTable.FlagHasSize;
    }
}
