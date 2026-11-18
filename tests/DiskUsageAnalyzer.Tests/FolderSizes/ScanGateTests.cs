using DevTools.Services.FolderSizes;
using Xunit;

namespace DiskUsageAnalyzer.Tests.FolderSizes;

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
