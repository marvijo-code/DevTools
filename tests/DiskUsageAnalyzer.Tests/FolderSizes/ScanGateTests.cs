using DevTools.Services.FolderSizes;
using Xunit;

namespace DiskUsageAnalyzer.Tests.FolderSizes;

[CollectionDefinition(nameof(ScanGateTests), DisableParallelization = true)]
public sealed class ScanGateCollection;

[Collection(nameof(ScanGateTests))]
public sealed class ScanGateTests
{
    [Fact]
    public async Task WaitAsync_YieldsUntilResumedAndKeepsCancellationIndependent()
    {
        using var pause = ScanGate.Pause();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var resumed = ScanGate.WaitAsync(deadline.Token).AsTask();
        var cancelled = ScanGate.WaitAsync(cancellation.Token).AsTask();

        Assert.False(resumed.IsCompleted);
        Assert.False(cancelled.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(resumed.IsCompleted);

        pause.Dispose();
        await resumed;
    }

    [Fact]
    public async Task WaitAsync_ResumesWhenTheLastOverlappingPauseEnds()
    {
        using var unrelatedPause = ScanGate.Pause([1]);
        using var sharedPause = ScanGate.Pause([2, 3]);
        using var secondSharedPause = ScanGate.Pause([4]);
        using var diskScope = ScanGate.ForDisks([3, 4]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var task = ScanGate.WaitAsync(deadline.Token).AsTask();
        Assert.False(task.IsCompleted);
        sharedPause.Dispose();
        var stillPaused = ScanGate.WaitAsync(deadline.Token).AsTask();
        Assert.False(stillPaused.IsCompleted);
        secondSharedPause.Dispose();
        await Task.WhenAll(task, stillPaused);
    }

    [Fact]
    public async Task UnrelatedDisksContinueOnAsyncAndDedicatedWorkers()
    {
        using var pause = ScanGate.Pause([1, 2]);
        using var diskScope = ScanGate.ForDisks([3]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await Task.Run(async () => await ScanGate.WaitAsync(deadline.Token), deadline.Token);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                ScanGate.Wait(deadline.Token);
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }) { IsBackground = true };
        worker.Start();
        await completion.Task;
    }

    [Fact]
    public async Task WaitAsync_PausesWhenEitherDiskSetIsUnknownAndRestoresOuterScope()
    {
        using var pause = ScanGate.Pause([1]);
        using var known = ScanGate.ForDisks([2]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task unknownScan;
        using (ScanGate.ForDisks([]))
        {
            unknownScan = ScanGate.WaitAsync(deadline.Token).AsTask();
            Assert.False(unknownScan.IsCompleted);
        }

        Assert.True(ScanGate.WaitAsync(deadline.Token).IsCompletedSuccessfully);
        using var unknownPause = ScanGate.Pause([]);
        var knownScan = ScanGate.WaitAsync(deadline.Token).AsTask();
        Assert.False(knownScan.IsCompleted);
        unknownPause.Dispose();
        await knownScan;
        pause.Dispose();
        await unknownScan;
    }

    [Fact]
    public void Wait_CancelsUntilTheLastPauseIsReleased()
    {
        using (ScanGate.Pause())
        {
            var nested = ScanGate.Pause();
            nested.Dispose();
            nested.Dispose();

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            Assert.ThrowsAny<OperationCanceledException>(() => ScanGate.Wait(cts.Token));
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        ScanGate.Wait(deadline.Token);
    }
}
