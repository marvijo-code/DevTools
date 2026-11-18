using DevTools.Services.FolderSizes;
using Xunit;

namespace DiskUsageAnalyzer.Tests.FolderSizes;

public sealed class ScanGateTests
{
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
