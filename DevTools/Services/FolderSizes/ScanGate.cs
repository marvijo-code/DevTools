namespace DevTools.Services.FolderSizes;

/// <summary>
/// Lets a folder the user just clicked be sized first. While any priority job holds a pause,
/// the background all-drive scans (folder walk and NTFS MFT read) wait at their next safe point.
/// </summary>
public static class ScanGate
{
    private static readonly ManualResetEventSlim Open = new(true);
    private static int _pauses;

    /// <summary>Pauses background scans until the returned handle is disposed.</summary>
    public static IDisposable Pause()
    {
        if (Interlocked.Increment(ref _pauses) == 1)
        {
            Open.Reset();
        }

        return new Releaser();
    }

    /// <summary>Blocks while a priority job is running. Call between units of background work.</summary>
    public static void Wait(CancellationToken cancellationToken)
    {
        Open.Wait(cancellationToken);
    }

    private sealed class Releaser : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0 && Interlocked.Decrement(ref _pauses) == 0)
            {
                Open.Set();
            }
        }
    }
}
