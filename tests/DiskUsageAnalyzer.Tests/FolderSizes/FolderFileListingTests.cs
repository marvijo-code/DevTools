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
    public async Task NtfsFallback_PublishesLogicalTreeBeforeMeasuringAllocation()
    {
        var fixture = Directory.CreateTempSubdirectory("devtools-logical-first-");
        var child = fixture.CreateSubdirectory("child");
        var nested = child.CreateSubdirectory("nested");
        var leaf = nested.CreateSubdirectory("leaf");
        var file = Path.Combine(nested.FullName, "payload.bin");
        var leafFile = Path.Combine(leaf.FullName, "leaf.bin");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var continueWithAllocation = new ManualResetEventSlim();
        var logicalReady = new TaskCompletionSource<FolderTree>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<CompletedFolderSize>();
        var live = new List<LiveFolderSize>();
        ParallelFolderWalker.LogicalFolderWalk? liveWalk = null;
        Task<DriveScanResult>? scan = null;
        try
        {
            File.WriteAllBytes(file, new byte[8193]);
            File.WriteAllBytes(leafFile, new byte[11]);
            var progress = new InlineProgress<FolderScanProgress>(update =>
            {
                seen.AddRange(update.CompletedFolders);
                live.AddRange(update.LiveSubfolders);
                liveWalk ??= update.LiveWalk;
            });
            var drive = new DriveDescriptor(fixture.FullName, "Fixture", "NTFS", null);
            scan = new DriveUsageService().ScanDriveAsync(drive, progress, deadline.Token, volumeShare: 1, tryMft: false,
                publishLogicalTree: tree =>
                {
                    logicalReady.TrySetResult(tree);
                    continueWithAllocation.Wait(deadline.Token);
                });
            var logical = await logicalReady.Task.WaitAsync(deadline.Token);
            var node = logical.Find(child.FullName);
            Assert.Equal(8204, logical.GetTotalBytes(node));
            Assert.Null(logical.GetAllocatedBytes(node));
            Assert.False(scan.IsCompleted);
            var early = Assert.Single(seen);
            Assert.Equal(8204, early.LogicalBytes);
            Assert.False(early.AllocationAttempted);
            var liveView = Assert.IsType<ParallelFolderWalker.LogicalFolderWalk>(liveWalk)
                .Capture(fixture.FullName, ["child"], new HashSet<string> { child.FullName, nested.FullName },
                    [], new HashSet<string>(), 200);
            Assert.Equal(8204, liveView.ScannedBytes);
            Assert.Collection(liveView.Rows,
                row => { Assert.Equal(8204, row.ScannedBytes); Assert.True(row.HasExactSize); },
                row => { Assert.Equal(8204, row.ScannedBytes); Assert.True(row.HasExactSize); },
                row => { Assert.Equal(11, row.ScannedBytes); Assert.True(row.HasExactSize); });
            Assert.Collection(live,
                folder => Assert.Equal(new LiveFolderSize(leaf.FullName, 11), folder),
                folder => Assert.Equal(new LiveFolderSize(nested.FullName, 8204), folder));
            Assert.All(live, folder => Assert.Equal(logical.GetTotalBytes(logical.Find(folder.Path)), folder.Bytes));

            continueWithAllocation.Set();
            var final = Assert.IsType<FolderTree>((await scan.WaitAsync(deadline.Token)).Tree);
            var finalNode = final.Find(child.FullName);
            Assert.Equal(8204, final.GetTotalBytes(finalNode));
            Assert.Equal(2, live.Count);
            Assert.True(final.GetAllocatedBytes(finalNode) > 0);
            Assert.Contains(seen, folder => folder.Path == child.FullName && folder.AllocationAttempted);
        }
        finally
        {
            continueWithAllocation.Set();
            if (scan is not null)
            {
                try { await scan; }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            }

            File.Delete(leafFile);
            File.Delete(file);
            leaf.Delete();
            nested.Delete();
            child.Delete();
            fixture.Delete();
        }
    }

    [Fact]
    public void LogicalWalk_RollsEachListingUpEveryAncestorBeforeSettlingTheBranch()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var outerPath = Path.Join(root, "outer");
        var innerPath = Path.Join(outerPath, "inner");
        var builder = new FolderTreeBuilder(root);
        var walk = new ParallelFolderWalker.LogicalFolderWalk(root, 0, builder, new object());
        var expanded = new HashSet<string> { outerPath, innerPath };

        var outer = builder.AppendEnumeratedChildren(0, ["outer"]);
        walk.FinishListing(0, outer, 1, 3, incomplete: false);
        var inner = builder.AppendEnumeratedChildren(outer, ["inner"]);
        walk.FinishListing(outer, inner, 1, 5, incomplete: false);
        var sibling = builder.AppendEnumeratedChildren(inner, ["sibling", "leaf"]);
        walk.FinishListing(inner, sibling, 2, 7, incomplete: false);
        walk.FinishListing(sibling + 1, 0, 0, 11, incomplete: false);

        var leafPath = Path.Join(innerPath, "leaf");
        Assert.Equal(new LiveFolderSize(leafPath, 11), Assert.Single(walk.Drain()));
        var noOverrides = new HashSet<string>();
        var reading = walk.Capture(root, ["outer"], expanded, [], noOverrides, 200);
        Assert.Equal(26, reading.ScannedBytes);
        Assert.Equal(new long[] { 23, 18, 11, 0 }, reading.Rows.Select(row => row.ScannedBytes));
        Assert.Equal(new[] { 0, 1, 2, 2 }, reading.Rows.Select(row => row.Depth));
        Assert.False(reading.Rows[0].IsSettled);
        Assert.False(reading.Rows[1].IsSettled);
        Assert.True(reading.Rows[2].HasExactSize);

        var revealed = walk.Capture(root, ["unrelated"], noOverrides, [leafPath], noOverrides, 1);
        Assert.Equal(new[] { "outer", "inner", "leaf", "unrelated" }, revealed.Rows.Select(row => row.Name));
        Assert.Equal(new[] { 0, 1, 2, 0 }, revealed.Rows.Select(row => row.Depth));
        Assert.Equal(new long[] { 23, 18, 11, 0 }, revealed.Rows.Select(row => row.ScannedBytes));
        Assert.True(revealed.Rows[0].IsExpanded);
        Assert.True(revealed.Rows[1].IsExpanded);

        var collapsed = walk.Capture(root, ["outer"], noOverrides, [leafPath], new HashSet<string> { innerPath }, 1);
        Assert.Equal(new[] { "outer", "inner" }, collapsed.Rows.Select(row => row.Name));
        Assert.Equal(18, collapsed.Rows[1].ScannedBytes);
        Assert.False(collapsed.Rows[1].IsExpanded);

        walk.FinishListing(sibling, 0, 0, 13, incomplete: true);

        Assert.Empty(walk.Drain());
        var settled = walk.Capture(root, ["outer"], expanded, [], noOverrides, 200);
        Assert.Equal(39, settled.ScannedBytes);
        Assert.Equal(new long[] { 36, 31, 11, 13 }, settled.Rows.Select(row => row.ScannedBytes));
        Assert.All(settled.Rows, row => Assert.True(row.IsSettled));
        Assert.False(settled.Rows[0].HasExactSize);
        Assert.False(settled.Rows[1].HasExactSize);
    }

    [Fact]
    public async Task Walker_ReportsExactSubtreeTotalsBeforeReturningTheDriveTree()
    {
        var fixture = Directory.CreateTempSubdirectory("devtools-exact-progress-");
        var first = fixture.CreateSubdirectory("first");
        var nested = first.CreateSubdirectory("nested");
        var second = fixture.CreateSubdirectory("second");
        var firstFile = Path.Combine(first.FullName, "first.bin");
        var nestedFile = Path.Combine(nested.FullName, "nested.bin");
        var secondFile = Path.Combine(second.FullName, "second.bin");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = new List<CompletedFolderSize>();
        Task<FolderTree>? scan = null;
        try
        {
            File.WriteAllBytes(firstFile, new byte[7]);
            File.WriteAllBytes(nestedFile, new byte[13]);
            File.WriteAllBytes(secondFile, new byte[31]);
            var progress = new InlineProgress<FolderScanProgress>(update =>
            {
                if (update.CompletedFolders.Count > 0)
                {
                    completed.AddRange(update.CompletedFolders);
                    ready.TrySetResult();
                    release.Wait(deadline.Token);
                }
            });
            scan = new ParallelFolderWalker().ScanAsync(fixture.FullName, progress, deadline.Token, workerCount: 4,
                measureAllocation: true);
            await ready.Task.WaitAsync(deadline.Token);
            Assert.False(scan.IsCompleted);
            release.Set();

            var tree = await scan.WaitAsync(deadline.Token);
            Assert.Equal(2, completed.Count);
            var firstSize = Assert.Single(completed, entry => entry.Path == first.FullName);
            var secondSize = Assert.Single(completed, entry => entry.Path == second.FullName);
            Assert.Equal(20, firstSize.LogicalBytes);
            Assert.Equal(31, secondSize.LogicalBytes);
            Assert.Equal(2, firstSize.FileCount);
            foreach (var folder in completed)
            {
                var node = tree.Find(folder.Path);
                Assert.Equal(tree.GetTotalBytes(node), folder.LogicalBytes);
                if (folder.AllocatedBytes.HasValue)
                {
                    Assert.Equal(tree.GetAllocatedBytes(node), folder.AllocatedBytes);
                }
            }
        }
        finally
        {
            release.Set();
            if (scan is not null)
            {
                try { await scan; }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            }

            File.Delete(firstFile);
            File.Delete(nestedFile);
            File.Delete(secondFile);
            nested.Delete();
            first.Delete();
            second.Delete();
            fixture.Delete();
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public void ReadExactPreviewSize_SuppressesTotalsWhenEitherBudgetIsExceeded()
    {
        var fixture = Directory.CreateTempSubdirectory("devtools-folder-size-preview-");
        var nested = fixture.CreateSubdirectory("nested");
        var direct = Path.Combine(fixture.FullName, "direct.bin");
        var child = Path.Combine(nested.FullName, "child.bin");
        try
        {
            File.WriteAllBytes(direct, new byte[7]);
            File.WriteAllBytes(child, new byte[13]);
            Assert.Equal(20, FolderFileListing.ReadExactPreviewSize(fixture.FullName, 2, 3, CancellationToken.None));
            Assert.Null(FolderFileListing.ReadExactPreviewSize(fixture.FullName, 1, 3, CancellationToken.None));
            Assert.Null(FolderFileListing.ReadExactPreviewSize(fixture.FullName, 2, 2, CancellationToken.None));
            Assert.Null(FolderFileListing.ReadExactPreviewSize(Path.Join(fixture.FullName, "missing"), 2, 3, CancellationToken.None));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => FolderFileListing.ReadExactPreviewSize(fixture.FullName, 2, 3, cts.Token));
        }
        finally
        {
            File.Delete(child);
            nested.Delete();
            File.Delete(direct);
            fixture.Delete();
        }
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
