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
    private const int ProgressBatchFiles = 4096;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly EnumerationOptions DirectoryOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        BufferSize = 16 * 1024
    };

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
        using var scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scanToken = scanCancellation.Token;
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
            scanToken.ThrowIfCancellationRequested();
            var batchFiles = 0;
            long batchBytes = 0;

            void PublishBatch()
            {
                if (batchFiles == 0)
                {
                    return;
                }

                Interlocked.Add(ref filesSeen, batchFiles);
                Interlocked.Add(ref bytesSeen, batchBytes);
                batchFiles = 0;
                batchBytes = 0;
            }

            var enumerable = new FileSystemEnumerable<int>(
                directory,
                static (ref FileSystemEntry _) => 0,
                DirectoryOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    scanToken.ThrowIfCancellationRequested();

                    if (entry.IsDirectory)
                    {
                        if ((entry.Attributes & System.IO.FileAttributes.ReparsePoint) == 0)
                        {
                            var child = entry.ToFullPath();
                            Interlocked.Increment(ref pending);
                            if (!writer.TryWrite(child))
                            {
                                Interlocked.Decrement(ref pending);
                            }
                        }

                        return false;
                    }

                    var length = entry.Length;
                    batchBytes += length;
                    if (++batchFiles == ProgressBatchFiles)
                    {
                        PublishBatch();
                    }

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

            try
            {
                foreach (var _ in enumerable)
                {
                    // The predicate reads file metadata and only materializes candidates for the ranking.
                }
            }
            finally
            {
                PublishBatch();
            }
        }

        async Task WorkerAsync()
        {
            try
            {
                while (await reader.WaitToReadAsync(scanToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var directory))
                    {
                        try
                        {
                            ScanDirectory(directory);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
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
            catch (Exception error) when (error is not OperationCanceledException)
            {
                writer.TryComplete(error);
                scanCancellation.Cancel();
                throw;
            }
        }

        var workerCount = Math.Clamp(Environment.ProcessorCount * 2, 4, 32);
        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(WorkerAsync, scanToken);
        }

        var allWorkers = Task.WhenAll(workers);

        try
        {
            if (progress is not null && !allWorkers.IsCompleted)
            {
                using var timer = new PeriodicTimer(ProgressInterval);
                while (!allWorkers.IsCompleted)
                {
                    var tick = timer.WaitForNextTickAsync(cancellationToken).AsTask();
                    if (await Task.WhenAny(allWorkers, tick).ConfigureAwait(false) == allWorkers)
                    {
                        break;
                    }

                    await tick.ConfigureAwait(false);
                    PublishProgress();
                }
            }

            await allWorkers.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            scanCancellation.Cancel();
            try
            {
                await allWorkers.ConfigureAwait(false);
            }
            catch
            {
                // Drain all workers and keep the error that ended the scan.
            }

            throw;
        }

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

        void PublishProgress()
        {
            progress?.Report(new BiggestFilesProgress(
                "Enumerating",
                Interlocked.Read(ref filesSeen),
                Interlocked.Read(ref bytesSeen),
                stopwatch.Elapsed,
                ComputeRate(Interlocked.Read(ref filesSeen), stopwatch.Elapsed),
                SnapshotTop(),
                IsCompleted: false));
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
