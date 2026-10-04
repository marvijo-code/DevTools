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
        // Everything with folder sizes lists every folder with its full total.
        var builder = new FolderTreeBuilder(@"G:\");
        builder.AddFolder(@"G:\Photos", 900);
        builder.AddFolder(@"G:\Photos\2025", 600);
        builder.AddFolder(@"G:\Photos\2026", 250);
        builder.AddFolder(@"G:\Docs", 40);

        var tree = builder.Build("Everything", TimeSpan.Zero);

        Assert.Equal(940, tree.TotalBytes);
        Assert.Equal(900, tree.GetTotalBytes(tree.Find(@"G:\Photos")));
        Assert.Equal(50, tree.GetDirectFileBytes(tree.Find(@"G:\Photos")));
        Assert.Equal(@"G:\Photos\2025", tree.GetPath(tree.GetChildren(tree.Find(@"G:\Photos"))[0]));
    }

    [Theory]
    [InlineData("        196608 C:\\ProgramData\\My SQL\\#ib.dblwr", 196608, "C:\\ProgramData\\My SQL\\#ib.dblwr")]
    [InlineData("3 C:\\a\\caf\u00e9-\u4e2d.txt\r", 3, "C:\\a\\caf\u00e9-\u4e2d.txt")]
    public void Everything_ParsesSizeAndPathLines(string line, long expectedSize, string expectedPath)
    {
        Assert.True(EverythingFolderScanner.TryParseLine(line, out var size, out var path));
        Assert.Equal(expectedSize, size);
        Assert.Equal(expectedPath, path);
    }

    [Theory]
    [InlineData("<DIR>          C:\\Windows")]
    [InlineData("")]
    [InlineData("Error 8: Everything IPC window not found.")]
    public void Everything_RejectsLinesWithoutASize(string line)
    {
        Assert.False(EverythingFolderScanner.TryParseLine(line, out _, out _));
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
