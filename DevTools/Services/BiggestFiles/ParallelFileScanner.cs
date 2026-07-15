using System.Diagnostics;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace DevTools.Services.BiggestFiles;

/// <summary>
/// Fallback scan engine that works on any filesystem without elevation. It walks the tree with a
/// pool of workers pulling directories off a channel; each worker enumerates a single directory
/// level with <see cref="FileSystemEnumerable{TResult}"/>, whose <c>FileSystemEntry.Length</c>
/// comes straight from the directory-read buffer (no per-file syscall).
/// </summary>
public sealed class ParallelFileScanner
{
    private const string EngineName = "Parallel enumeration";
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public async Task<BiggestFilesResult> ScanAsync(
        BiggestFilesRequest request,
        IProgress<BiggestFilesProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(request.ScanRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Scan root does not exist: {root}");
        }

        var topN = new TopNAccumulator<FileSizeHit>(request.TopCount);
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
        var writer = channel.Writer;
        var reader = channel.Reader;

        long filesSeen = 0;
        long bytesSeen = 0;
        var pending = 1;
        writer.TryWrite(root);

        var stopwatch = Stopwatch.StartNew();

        void ScanDirectory(string directory)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false
            };

            var enumerable = new FileSystemEnumerable<int>(
                directory,
                static (ref FileSystemEntry _) => 0,
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return false;
                    }

                    if (entry.IsDirectory)
                    {
                        if ((entry.Attributes & System.IO.FileAttributes.ReparsePoint) == 0)
                        {
                            Interlocked.Increment(ref pending);
                            if (!writer.TryWrite(entry.ToFullPath()))
                            {
                                Interlocked.Decrement(ref pending);
                            }
                        }

                        return false;
                    }

                    var length = entry.Length;
                    Interlocked.Increment(ref filesSeen);
                    Interlocked.Add(ref bytesSeen, length);

                    if (length > topN.Threshold)
                    {
                        DateTimeOffset? modified = null;
                        try
                        {
                            modified = entry.LastWriteTimeUtc;
                        }
                        catch
                        {
                            // Timestamps are best-effort.
                        }

                        // Directory enumeration does not expose on-disk allocation; report logical size for both.
                        topN.TryAdd(length, new FileSizeHit(entry.ToFullPath(), length, length, modified));
                    }

                    return false;
                }
            };

            foreach (var _ in enumerable)
            {
                // Enumeration drives the predicate's side effects; no results are materialized.
            }
        }

        async Task WorkerAsync()
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var directory))
                {
                    try
                    {
                        ScanDirectory(directory);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                                   or DirectoryNotFoundException or System.Security.SecurityException)
                    {
                        // Skip directories that vanish or deny access mid-scan.
                    }
                    finally
                    {
                        if (Interlocked.Decrement(ref pending) == 0)
                        {
                            writer.TryComplete();
                        }
                    }
                }
            }
        }

        var workerCount = Math.Clamp(Environment.ProcessorCount * 2, 4, 32);
        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(WorkerAsync, cancellationToken);
        }

        var allWorkers = Task.WhenAll(workers);

        while (!allWorkers.IsCompleted)
        {
            var delay = Task.Delay(ProgressInterval, cancellationToken);
            await Task.WhenAny(allWorkers, delay).ConfigureAwait(false);
            PublishProgress(isCompleted: false);
        }

        await allWorkers.ConfigureAwait(false);
        stopwatch.Stop();

        var finalTop = SnapshotTop();
        progress?.Report(new BiggestFilesProgress(
            "Done",
            Interlocked.Read(ref filesSeen),
            Interlocked.Read(ref bytesSeen),
            stopwatch.Elapsed,
            ComputeRate(Interlocked.Read(ref filesSeen), stopwatch.Elapsed),
            finalTop,
            IsCompleted: true));

        return new BiggestFilesResult(
            finalTop,
            Interlocked.Read(ref filesSeen),
            Interlocked.Read(ref bytesSeen),
            stopwatch.Elapsed,
            EngineName,
            Warning: null);

        void PublishProgress(bool isCompleted)
        {
            progress?.Report(new BiggestFilesProgress(
                "Enumerating",
                Interlocked.Read(ref filesSeen),
                Interlocked.Read(ref bytesSeen),
                stopwatch.Elapsed,
                ComputeRate(Interlocked.Read(ref filesSeen), stopwatch.Elapsed),
                SnapshotTop(),
                isCompleted));
        }

        IReadOnlyList<FileSizeHit> SnapshotTop()
        {
            return topN.Snapshot().Select(item => item.Item).ToList();
        }
    }

    private static double ComputeRate(long files, TimeSpan elapsed)
    {
        return elapsed.TotalSeconds <= 0 ? 0 : files / elapsed.TotalSeconds;
    }
}
