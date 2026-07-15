using DevTools.Services.BiggestFiles;
using Xunit;

namespace DiskUsageAnalyzer.Tests.BiggestFiles;

public sealed class ParallelFileScannerTests : IDisposable
{
    private readonly string _root;

    public ParallelFileScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "biggest-files-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private void WriteFile(string relativePath, long size)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = new FileStream(full, FileMode.Create, FileAccess.Write);
        stream.SetLength(size);
    }

    [Fact]
    public async Task ReturnsLargestFilesAcrossNestedDirectories()
    {
        WriteFile("small.txt", 100);
        WriteFile("sub/medium.bin", 5_000);
        WriteFile("sub/deep/large.bin", 9_000_000);
        WriteFile("sub/deep/huge.bin", 40_000_000);
        WriteFile("other/mid.bin", 1_000_000);

        var scanner = new ParallelFileScanner();
        var result = await scanner.ScanAsync(new BiggestFilesRequest(_root, TopCount: 3), progress: null, CancellationToken.None);

        Assert.Equal("Parallel enumeration", result.EngineName);
        Assert.Equal(5, result.FilesSeen);
        Assert.Equal(3, result.Top.Count);
        Assert.Equal(
            new[] { 40_000_000L, 9_000_000L, 1_000_000L },
            result.Top.Select(hit => hit.SizeBytes).ToArray());
        Assert.EndsWith("huge.bin", result.Top[0].FullPath);
    }

    [Fact]
    public async Task ThrowsForMissingRoot()
    {
        var scanner = new ParallelFileScanner();
        var missing = Path.Combine(_root, "does-not-exist");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => scanner.ScanAsync(new BiggestFilesRequest(missing, 10), null, CancellationToken.None));
    }

    [Fact]
    public async Task HonorsCancellation()
    {
        for (var i = 0; i < 200; i++)
        {
            WriteFile($"bulk/file{i}.bin", 1_000 + i);
        }

        var scanner = new ParallelFileScanner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => scanner.ScanAsync(new BiggestFilesRequest(_root, 10), null, cts.Token));
    }

    [Fact]
    public async Task IncludesTopCountBound()
    {
        for (var i = 0; i < 20; i++)
        {
            WriteFile($"n{i}.bin", (i + 1) * 1000L);
        }

        var scanner = new ParallelFileScanner();
        var result = await scanner.ScanAsync(new BiggestFilesRequest(_root, TopCount: 5), null, CancellationToken.None);

        Assert.Equal(20, result.FilesSeen);
        Assert.Equal(5, result.Top.Count);
        Assert.Equal(20_000L, result.Top[0].SizeBytes);
        Assert.Equal(16_000L, result.Top[4].SizeBytes);
    }
}
