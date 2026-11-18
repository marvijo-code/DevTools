using DevTools.Services.FolderSizes;
using Xunit;

namespace DiskUsageAnalyzer.Tests.FolderSizes;

public sealed class FolderFileListingTests
{
    [Fact]
    public async Task ReadBatchesAsync_YieldsAReadyFolderWhileTheNextFolderIsStillReading()
    {
        using var releaseSecond = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        FolderFileRequest[] folders = [new(1, "first", 0, 0), new(2, "second", 0, 0)];
        FolderFileSnapshot Snapshot(string path, CancellationToken token)
        {
            if (path == "second")
            {
                releaseSecond.Wait(token);
            }

            return new FolderFileSnapshot([], 0, 0);
        }

        await using var batches = FolderFileListing.ReadBatchesAsync(folders, Snapshot, deadline.Token).GetAsyncEnumerator();
        Assert.True(await batches.MoveNextAsync());
        Assert.Equal(1, Assert.Single(batches.Current).Folder.Node);

        releaseSecond.Set();
        Assert.True(await batches.MoveNextAsync());
        Assert.Equal(2, Assert.Single(batches.Current).Folder.Node);
        Assert.False(await batches.MoveNextAsync());
    }

    [Fact]
    public async Task ReadBatchesAsync_KeepsReadingWhileTheViewRenders()
    {
        using var continueReads = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var allRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var folders = Enumerable.Range(0, 40).Select(node => new FolderFileRequest(node, node.ToString(), 0, 0)).ToArray();
        FolderFileSnapshot Snapshot(string path, CancellationToken token)
        {
            if (path == folders[1].Path)
            {
                continueReads.Wait(token);
            }

            if (path == folders[^1].Path)
            {
                allRead.TrySetResult();
            }

            return new FolderFileSnapshot([], 0, 0);
        }

        await using var batches = FolderFileListing.ReadBatchesAsync(folders, Snapshot, deadline.Token).GetAsyncEnumerator();
        Assert.True(await batches.MoveNextAsync());
        Assert.Equal(0, Assert.Single(batches.Current).Folder.Node);

        continueReads.Set();
        await allRead.Task.WaitAsync(deadline.Token);
        var completed = new List<int> { 0 };
        while (await batches.MoveNextAsync())
        {
            completed.AddRange(batches.Current.Select(result => result.Folder.Node));
        }

        Assert.Equal(Enumerable.Range(0, 40), completed);
    }

    [Fact]
    public async Task ReadBatchesAsync_DisposingTheReaderCancelsAWaitingFolder()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var waiting = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        FolderFileRequest[] folders = [new(1, "first", 0, 0), new(2, "second", 0, 0)];
        FolderFileSnapshot Snapshot(string path, CancellationToken token)
        {
            if (path == "second")
            {
                entered.TrySetResult();
                try
                {
                    waiting.Wait(token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    stopped = true;
                    throw;
                }
            }

            return new FolderFileSnapshot([], 0, 0);
        }

        await using (var batches = FolderFileListing.ReadBatchesAsync(folders, Snapshot, deadline.Token).GetAsyncEnumerator())
        {
            Assert.True(await batches.MoveNextAsync());
            Assert.Equal(1, Assert.Single(batches.Current).Folder.Node);
            await entered.Task.WaitAsync(deadline.Token);
        }

        Assert.True(stopped);
        Assert.False(deadline.IsCancellationRequested);
    }

    [Fact]
    public void ReadLargest_HonoursCancellationBeforeAccessingTheFolder()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var error = Assert.Throws<OperationCanceledException>(() =>
            FolderFileListing.ReadLargest(Path.GetTempPath(), 10, cts.Token));
        Assert.Equal(cts.Token, error.CancellationToken);
    }

    [Fact]
    public void ReadLargest_DistinguishesAnEmptyFolderFromAnUnavailableListing()
    {
        var fixture = Directory.CreateTempSubdirectory("devtools-empty-file-listing-");
        try
        {
            var listing = Assert.IsType<FolderFileSnapshot>(FolderFileListing.ReadLargest(fixture.FullName, 5));
            Assert.True(listing.Matches(0, 0));
            Assert.Empty(listing.Files);
            Assert.Null(FolderFileListing.ReadLargest(Path.Combine(fixture.FullName, "missing"), 5));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadLargest_IncludesHiddenFilesButExcludesNestedFiles(bool relativePath)
    {
        var fixture = Directory.CreateTempSubdirectory("devtools-file-ranking-");
        var nested = Path.Combine(fixture.FullName, "nested");
        var hidden = Path.Combine(fixture.FullName, "hidden.bin");
        var visible = Path.Combine(fixture.FullName, "visible.bin");
        var small = Path.Combine(fixture.FullName, "small.bin");
        var empty = Path.Combine(fixture.FullName, "empty.bin");
        var deep = Path.Combine(nested, "deep.bin");

        try
        {
            Directory.CreateDirectory(nested);
            File.WriteAllBytes(hidden, new byte[10]);
            File.SetAttributes(hidden, FileAttributes.Hidden);
            File.WriteAllBytes(visible, new byte[5]);
            File.WriteAllBytes(small, new byte[2]);
            File.WriteAllBytes(empty, []);
            File.WriteAllBytes(deep, new byte[50]);

            var folder = relativePath ? Path.GetRelativePath(Environment.CurrentDirectory, fixture.FullName) : fixture.FullName;
            var listing = Assert.IsType<FolderFileSnapshot>(FolderFileListing.ReadLargest(folder, 2));

            Assert.Equal([hidden, visible], listing.Files.Select(item => item.Path));
            Assert.Equal([10L, 5L], listing.Files.Select(item => item.Bytes));
            Assert.Equal(17, listing.TotalBytes);
            Assert.Equal(4, listing.FileCount);
            Assert.True(listing.Matches(17, 4));
            Assert.False(listing.Matches(17, 3));
            Assert.False(listing.Matches(10, 4));
        }
        finally
        {
            File.Delete(hidden);
            File.Delete(visible);
            File.Delete(small);
            File.Delete(empty);
            if (Directory.Exists(nested))
            {
                File.Delete(deep);
                Directory.Delete(nested);
            }

            fixture.Delete();
        }
    }
}
