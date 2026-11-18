namespace DevTools.Services.FolderSizes;

/// <summary>
/// Lets a folder the user just clicked be sized first. While any priority job holds a pause,
/// the background all-drive scans (folder walk and NTFS MFT read) wait at their next safe point.
/// </summary>
public static class ScanGate
{
    private static readonly object Sync = new();
    private static TaskCompletionSource? _resume;
    private static int _pauses;

    /// <summary>Pauses background scans until the returned handle is disposed.</summary>
    public static IDisposable Pause()
    {
        lock (Sync)
        {
            if (++_pauses == 1)
            {
                Volatile.Write(ref _resume, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }
        }

        return new Releaser();
    }

    /// <summary>Blocks while a priority job is running. Call between units of background work.</summary>
    public static void Wait(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (Volatile.Read(ref _resume) is { } resume)
        {
            resume.Task.WaitAsync(cancellationToken).GetAwaiter().GetResult();
        }
    }

    private sealed class Releaser : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            lock (Sync)
            {
                if (--_pauses == 0)
                {
                    var resume = _resume!;
                    Volatile.Write(ref _resume, null);
                    resume.SetResult();
                }
            }
        }
    }
}
