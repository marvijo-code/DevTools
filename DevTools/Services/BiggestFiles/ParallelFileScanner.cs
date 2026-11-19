using System.Diagnostics;
using System.IO.Enumeration;
using System.Threading.Channels;

namespace DevTools.Services.BiggestFiles;

/// <summary>
/// Fallback scan engine that works on any filesystem without elevation. It walks the tree with a
/// pool of workers pulling directories off a channel; each worker enumerates a single directory
/// level with <see cref="FileSystemEnumerator{TResult}"/>, whose <c>FileSystemEntry.Length</c>
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

        var pending = 1;
        writer.TryWrite(root);

        var stopwatch = Stopwatch.StartNew();

        void QueueChild(string path)
        {
            Interlocked.Increment(ref pending);
            if (!writer.TryWrite(path))
            {
                Interlocked.Decrement(ref pending);
            }
        }

        Action<string> queueChild = QueueChild;

        async Task WorkerAsync(ScanTotals totals)
        {
            try
            {
                while (await reader.WaitToReadAsync(scanToken).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var directory))
                    {
                        try
                        {
                            scanToken.ThrowIfCancellationRequested();
                            using var scan = new DirectoryScan(directory, topN, queueChild, totals, scanToken);
                            scan.AddLargestFiles();
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
        var workerTotals = new ScanTotals[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            var totals = workerTotals[i] = new ScanTotals();
            workers[i] = Task.Run(() => WorkerAsync(totals), scanToken);
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
        var (files, bytes) = ReadTotals();
        progress?.Report(new BiggestFilesProgress(
            "Done", files, bytes, stopwatch.Elapsed, ComputeRate(files, stopwatch.Elapsed),
            finalTop, IsCompleted: true));

        return new BiggestFilesResult(finalTop, files, bytes, stopwatch.Elapsed, EngineName, Warning: null);

        (long Files, long Bytes) ReadTotals()
        {
            long files = 0;
            long bytes = 0;
            foreach (var totals in workerTotals)
            {
                files += totals.Files;
                bytes += totals.Bytes;
            }

            return (files, bytes);
        }

        void PublishProgress()
        {
            var (files, bytes) = ReadTotals();
            var elapsed = stopwatch.Elapsed;
            progress!.Report(new BiggestFilesProgress(
                "Enumerating", files, bytes, elapsed, ComputeRate(files, elapsed), SnapshotTop(), IsCompleted: false));
        }

        IReadOnlyList<FileSizeHit> SnapshotTop()
        {
            return topN.Snapshot().Select(item => item.Item).ToList();
        }
    }

    // Each worker owns its counters. The progress reader combines them without a shared update lock.
    private sealed class ScanTotals
    {
        private long _files;
        private long _bytes;

        internal long Files => Volatile.Read(ref _files);
        internal long Bytes => Volatile.Read(ref _bytes);

        internal void Add(int files, long bytes)
        {
            Volatile.Write(ref _files, _files + files);
            Volatile.Write(ref _bytes, _bytes + bytes);
        }
    }

    private sealed class DirectoryScan(
        string directory,
        TopNAccumulator<FileSizeHit> top,
        Action<string> queueChild,
        ScanTotals totals,
        CancellationToken cancellationToken) : FileSystemEnumerator<FileSizeHit>(directory, DirectoryOptions)
    {
        private int _files;
        private long _bytes;

        internal void AddLargestFiles()
        {
            try
            {
                while (MoveNext())
                {
                    var hit = Current;
                    top.TryAdd(hit.SizeBytes, hit);
                }
            }
            finally
            {
                if (_files > 0)
                {
                    totals.Add(_files, _bytes);
                }
            }
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsDirectory)
            {
                if ((entry.Attributes & System.IO.FileAttributes.ReparsePoint) == 0)
                {
                    queueChild(entry.ToFullPath());
                }

                return false;
            }

            var size = entry.Length;
            _bytes += size;
            if (++_files == ProgressBatchFiles)
            {
                totals.Add(_files, _bytes);
                _files = 0;
                _bytes = 0;
            }

            return size > top.Threshold;
        }

        protected override FileSizeHit TransformEntry(ref FileSystemEntry entry)
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

            // Directory enumeration exposes logical size.
            var size = entry.Length;
            return new FileSizeHit(entry.ToFullPath(), size, size, modified);
        }
    }

    private static double ComputeRate(long files, TimeSpan elapsed)
    {
        return elapsed.TotalSeconds <= 0 ? 0 : files / elapsed.TotalSeconds;
    }
}
