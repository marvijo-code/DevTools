using DevTools.Models;
using DevTools.Services.FolderSizes;
using Xunit;

namespace DiskUsageAnalyzer.Tests.FolderSizes;

public sealed class FolderTreeTests
{
    [Fact]
    public void Builder_FromFiles_RollsSizesUpToEveryAncestor()
    {
        var builder = new FolderTreeBuilder(@"C:\");
        builder.AddFile(@"C:\dev\repo\node_modules\a.js", 300);
        builder.AddFile(@"C:\dev\repo\node_modules\b.js", 200);
        builder.AddFile(@"C:\dev\repo\readme.md", 10);
        builder.AddFile(@"C:\Users\user\video.mkv", 5000);
        builder.AddFile(@"C:\pagefile.sys", 1);

        var tree = builder.Build("test", TimeSpan.FromSeconds(1));

        Assert.Equal(5511, tree.TotalBytes);
        Assert.Equal(5, tree.TotalFiles);
        Assert.Equal(510, tree.GetTotalBytes(tree.Find(@"C:\dev")));
        Assert.Equal(500, tree.GetTotalBytes(tree.Find(@"C:\dev\repo\node_modules")));
        Assert.Equal(10, tree.GetDirectFileBytes(tree.Find(@"C:\dev\repo")));
        Assert.Equal(1, tree.GetDirectFileBytes(0));
        Assert.Equal(-1, tree.Find(@"C:\missing"));
        Assert.Equal(-1, tree.Find(@"D:\dev"));
        Assert.Equal([@"C:\Users", @"C:\dev"], tree.GetChildren(0).Select(tree.GetPath).ToArray());
    }

    [Fact]
    public void Builder_FromFolderTotals_UsesKnownSizesWithoutDoubleCounting()
    {
        // A folder-total listing (for example the raw MFT reader) reports every folder with its full total.
        var builder = new FolderTreeBuilder(@"G:\");
        builder.AddFolder(@"G:\Photos", 900);
        builder.AddFolder(@"G:\Photos\2025", 600);
        builder.AddFolder(@"G:\Photos\2026", 250);
        builder.AddFolder(@"G:\Docs", 40);

        var tree = builder.Build("NTFS MFT", TimeSpan.Zero);

        Assert.Equal(940, tree.TotalBytes);
        Assert.Equal(900, tree.GetTotalBytes(tree.Find(@"G:\Photos")));
        Assert.Equal(50, tree.GetDirectFileBytes(tree.Find(@"G:\Photos")));
        Assert.Equal(@"G:\Photos\2025", tree.GetPath(tree.GetChildren(tree.Find(@"G:\Photos"))[0]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(25)]
    [InlineData(200)]
    [InlineData(500)]
    public void GetChildren_WideFolderMatchesFullRanking(int max)
    {
        var builder = new FolderTreeBuilder(@"C:\");
        for (var i = 1999; i >= 0; i--)
        {
            builder.AddFolder($@"C:\Folder-{i:D4}", (i * 39) % 17);
        }

        builder.AddFile(@"C:\empty.txt", 0);
        var tree = builder.Build("test", TimeSpan.Zero);

        Assert.Equal(tree.GetChildren(0).Take(max).ToArray(), tree.GetChildren(0, max).ToArray());
        Assert.Equal(2000, tree.GetChildFolderCount(0));
        Assert.Equal(1, tree.GetDirectFileCount(0));
        Assert.Equal(0, tree.GetDirectFileBytes(0));
    }

    [Fact]
    public void Find_WideFolderKeepsExactAndAmbiguousCaseMatchesDuringConcurrentLookups()
    {
        var builder = new FolderTreeBuilder(@"C:\");
        var parent = builder.AddFolder(@"C:\wide");
        var names = Enumerable.Range(0, 1500).Select(i => $"child-{i:D4}").ToList();
        for (var variant = 0; variant < 64; variant++)
        {
            var name = "folders".ToCharArray();
            for (var position = 0; position < 6; position++)
            {
                if ((variant & (1 << position)) != 0)
                {
                    name[position] = char.ToUpperInvariant(name[position]);
                }
            }

            names[variant] = new string(name);
        }

        var first = builder.AppendEnumeratedChildren(parent, names);
        var tree = builder.Build("test", TimeSpan.Zero);

        Parallel.For(0, 64, variant =>
        {
            Assert.Equal(first + variant, tree.Find($@"C:\wide\{names[variant]}"));
            Assert.Equal(-1, tree.Find(@"C:\wide\FOLDERS"));
            Assert.Equal(first + 1000, tree.Find(@"C:\WIDE\CHILD-1000"));
            Assert.Equal(-1, tree.Find(@"C:\wide\missing"));
        });
    }

    [Fact]
    public void EnumeratedSiblings_KeepDistinctNamesAndTotalsThroughRefresh()
    {
        var builder = new FolderTreeBuilder(@"C:\", trackAllocation: true);
        var root = builder.AddFolder(@"C:\workspace");
        var first = builder.AppendEnumeratedChildren(root, ["source", "Source", "Other"]);
        builder.AddFilesToFolder(first, 12, 1, 4096);
        builder.AddFilesToFolder(first + 1, 4097, 1, 8192);
        var tree = builder.Build("fixture", TimeSpan.Zero);

        Assert.Equal(4109, tree.TotalBytes);
        Assert.Equal(12288, tree.GetAllocatedBytes(root));
        Assert.Equal(12, tree.GetTotalBytes(tree.Find(@"c:\WORKSPACE\source")));
        Assert.Equal(4097, tree.GetTotalBytes(tree.Find(@"C:/workspace/Source/")));
        Assert.Equal(first + 2, tree.Find(@"C:\workspace\OTHER"));
        Assert.Equal(-1, tree.Find(@"C:\workspace\SOURCE"));

        var paths = tree.GetChildren(root, 2).Select(tree.GetPath);
        var entries = paths.Select(path => DiskUsageEntry.CreateCompleted(path, false, null, "", null)).ToArray();
        var section = new DiskUsageSection("Largest Folders", "", [entries[0]]);
        section.UpdateFrom(new DiskUsageSection(section.Title, "", entries));

        Assert.Equal([@"C:\workspace\Source", @"C:\workspace\source"], section.Entries.Select(entry => entry.Path));
    }

    [Fact]
    public async Task Walker_ReportsAnUnavailableDirectoryAsIncomplete()
    {
        var fixture = Directory.CreateTempSubdirectory("devtools-unavailable-folder-");
        try
        {
            var missing = Path.Combine(fixture.FullName, "missing");
            var tree = await new ParallelFolderWalker().ScanAsync(missing, null, CancellationToken.None, workerCount: 1);
            var node = tree.Find(missing);

            Assert.Equal(1, tree.IncompleteDirectoryCount);
            Assert.True(tree.HasIncompleteScan(node));
            Assert.Equal(0, tree.GetFileCount(node));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Fact]
    public async Task Walker_SizesNestedFoldersAndSkipsJunctions()
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-walk-" + Guid.NewGuid().ToString("N"));
        var deep = Path.Combine(root, "a", "b", "c");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(Path.Combine(root, "z"));
        await File.WriteAllBytesAsync(Path.Combine(deep, "big.bin"), new byte[40000]);
        await File.WriteAllBytesAsync(Path.Combine(root, "a", "small.bin"), new byte[100]);
        await File.WriteAllBytesAsync(Path.Combine(root, "z", "z.bin"), new byte[5]);
        var link = Path.Combine(root, "link-to-a");
        // A junction needs no admin rights, unlike a symbolic link.
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(root, "a")}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!)
        {
            await mklink.WaitForExitAsync();
        }

        Assert.True(Directory.Exists(link));

        try
        {
            var tree = await new ParallelFolderWalker().ScanAsync(root, null, CancellationToken.None, workerCount: 2);

            var node = tree.Find(root);
            Assert.True(node >= 0);
            Assert.Equal(40105, tree.GetTotalBytes(node));
            Assert.Equal(40100, tree.GetTotalBytes(tree.Find(Path.Combine(root, "a"))));
            Assert.Equal(40000, tree.GetTotalBytes(tree.Find(deep)));
            Assert.Equal(-1, tree.Find(link));
        }
        finally
        {
            // Remove the junction itself first (never its target), then the temp tree.
            Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Walker_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ParallelFolderWalker().ScanAsync(Path.GetTempPath(), null, cts.Token));
    }
}
