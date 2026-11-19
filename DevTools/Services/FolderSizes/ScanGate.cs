namespace DevTools.Services.FolderSizes;

/// <summary>
/// Lets a clicked folder be sized first by pausing background work on shared physical disks.
/// When either side has unknown backing disks, background work waits conservatively.
/// </summary>
public static class ScanGate
{
    private static readonly object Sync = new();
    // Flow the volume's disk set to both async scan tasks and the walker's dedicated threads.
    private static readonly AsyncLocal<int[]?> ScanDisks = new();
    private static PauseHandle[] _active = [];

    /// <summary>Pauses background scans until the returned handle is disposed.</summary>
    public static IDisposable Pause() => Pause([]);

    internal static IDisposable Pause(int[] disks)
    {
        var pause = new PauseHandle(disks);
        lock (Sync)
        {
            Volatile.Write(ref _active, [.. _active, pause]);
        }

        return pause;
    }

    internal static DiskScope ForDisks(int[]? disks)
    {
        var previous = ScanDisks.Value;
        ScanDisks.Value = disks;
        return new DiskScope(previous);
    }

    /// <summary>Blocks while priority work uses a shared disk. Call between units of background work.</summary>
    public static void Wait(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (GetResumeTask() is { } resume)
        {
            resume.WaitAsync(cancellationToken).GetAwaiter().GetResult();
        }
    }

    /// <summary>Lets an asynchronous background worker yield until priority work finishes.</summary>
    internal static async ValueTask WaitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (GetResumeTask() is { } resume)
        {
            await resume.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task? GetResumeTask()
    {
        var active = Volatile.Read(ref _active);
        if (active.Length == 0)
        {
            return null;
        }

        var disks = ScanDisks.Value;
        foreach (var pause in active)
        {
            if (disks is not { Length: > 0 } || pause.Disks.Length == 0 || SharesDisk(disks, pause.Disks))
            {
                return pause.Resume.Task;
            }
        }

        return null;
    }

    private static bool SharesDisk(int[] scanDisks, int[] priorityDisks)
    {
        foreach (var disk in scanDisks)
        {
            if (Array.IndexOf(priorityDisks, disk) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    internal readonly struct DiskScope(int[]? previous) : IDisposable
    {
        public void Dispose() => ScanDisks.Value = previous;
    }

    private sealed class PauseHandle(int[] disks) : IDisposable
    {
        private int _released;

        internal int[] Disks { get; } = disks;
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            lock (Sync)
            {
                var index = Array.IndexOf(_active, this);
                Volatile.Write(ref _active, [.. _active.AsSpan(0, index), .. _active.AsSpan(index + 1)]);
            }

            Resume.SetResult();
        }
    }
}
