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
    public async Task ReadBatchesAsync_ParallelReadersCanPublishWhileTheFirstFolderIsBlocked()
    {
        using var blocked = new ManualResetEventSlim();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        FolderFileRequest[] folders = [new(1, "blocked", 0, 0), new(2, "ready", 0, 0), new(3, "later", 0, 0)];
        FolderFileSnapshot Snapshot(string path, CancellationToken token)
        {
            if (path == "blocked")
            {
                blocked.Wait(token);
            }

            return new FolderFileSnapshot([], 0, 0);
        }

        await using var batches = FolderFileListing.ReadBatchesAsync(folders, Snapshot, deadline.Token, static () => 2)
            .GetAsyncEnumerator();
        Assert.True(await batches.MoveNextAsync());
        var completed = batches.Current.Select(result => result.Folder.Node).ToList();
        Assert.DoesNotContain(1, completed);
        blocked.Set();

        while (await batches.MoveNextAsync())
        {
            completed.AddRange(batches.Current.Select(result => result.Folder.Node));
        }

        Assert.Equal([1, 2, 3], completed.Order());
    }

    [Fact]
    public async Task ReadBatchesAsync_ParallelFailureCancelsTheOtherReader()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var started = new ManualResetEventSlim();
        using var blocked = new ManualResetEventSlim();
        var failure = new InvalidOperationException("Folder reader failed.");
        var readerCancelled = false;
        FolderFileRequest[] folders = [new(1, "blocked", 0, 0), new(2, "failure", 0, 0)];
        FolderFileSnapshot Snapshot(string path, CancellationToken token)
        {
            if (path == "failure")
            {
                started.Wait(token);
                throw failure;
            }

            started.Set();
            try
            {
                blocked.Wait(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                readerCancelled = true;
                throw;
            }

            return new FolderFileSnapshot([], 0, 0);
        }

        await using var batches = FolderFileListing.ReadBatchesAsync(folders, Snapshot, deadline.Token, static () => 2)
            .GetAsyncEnumerator();
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => batches.MoveNextAsync().AsTask());
        Assert.Same(failure, observed);
        Assert.True(readerCancelled);
    }

    [Fact]
    public void ReadChildFolders_ReturnsASortedBoundedPreviewOfImmediateDirectories()
    {
        var fixture = Directory.CreateTempSubdirectory("devtools-folder-preview-");
        var directFile = Path.Combine(fixture.FullName, "ordinary.bin");
        var zulu = fixture.CreateSubdirectory("Zulu");
        var alpha = fixture.CreateSubdirectory("alpha");
        var beta = fixture.CreateSubdirectory("Beta");
        var nested = zulu.CreateSubdirectory("nested");
        try
        {
            File.WriteAllBytes(directFile, [42]);

            var complete = Assert.IsType<FolderPreview>(FolderFileListing.ReadChildFolders(
                fixture.FullName, 3, CancellationToken.None));
            Assert.Equal(["alpha", "Beta", "Zulu"], complete.FolderNames);
            Assert.False(complete.HasMore);

            var limited = Assert.IsType<FolderPreview>(FolderFileListing.ReadChildFolders(
                fixture.FullName, 2, CancellationToken.None));
            Assert.True(limited.HasMore);
            Assert.Equal(2, limited.FolderNames.Count);
            Assert.All(limited.FolderNames, name => Assert.Contains(name, complete.FolderNames));

            var empty = Assert.IsType<FolderPreview>(FolderFileListing.ReadChildFolders(
                beta.FullName, 2, CancellationToken.None));
            Assert.Empty(empty.FolderNames);
            Assert.False(empty.HasMore);
            Assert.Null(FolderFileListing.ReadChildFolders(Path.Combine(fixture.FullName, "missing"), 2, CancellationToken.None));
        }
        finally
        {
            File.Delete(directFile);
            nested.Delete();
            beta.Delete();
            alpha.Delete();
            zulu.Delete();
            fixture.Delete();
        }
    }

    [Fact]
    public void ReadChildFolders_HonoursCancellationBeforeAccessingTheFolder()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = Assert.Throws<OperationCanceledException>(() =>
            FolderFileListing.ReadChildFolders(Path.GetTempPath(), 2, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
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
