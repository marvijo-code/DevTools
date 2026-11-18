using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using Microsoft.Win32.SafeHandles;

namespace DevTools.Services.FolderSizes;

/// <summary>
/// Last-resort engine: walks the folders with a few low-priority threads and builds the same
/// <see cref="FolderTree"/> as the MFT engine. It is the slow path (it reads
/// every directory), so it keeps CPU use low and can be cancelled at any time.
/// Junctions and symbolic links are not followed.
/// </summary>
public sealed class ParallelFolderWalker
{
    public const string EngineName = "Folder walk";
    private const int MaximumDirectoriesPerJob = 16;

    public Task<FolderTree> ScanAsync(
        string rootPath,
        IProgress<FolderScanProgress>? progress,
        CancellationToken cancellationToken,
        int? workerCount = null,
        bool prioritize = false,
        bool measureAllocation = false)
    {
        return Task.Run(() => Scan(rootPath, progress, cancellationToken, workerCount, prioritize, volumeShare: 1,
                measureAllocation),
            cancellationToken);
    }

    internal Task<FolderTree> ScanVolumeAsync(string rootPath, IProgress<FolderScanProgress>? progress,
        CancellationToken cancellationToken, int volumeShare)
    {
        return Task.Run(() => Scan(rootPath, progress, cancellationToken, workerCount: null, prioritize: false, volumeShare,
                measureAllocation: true),
            cancellationToken);
    }

    /// <param name="prioritize">
    /// True for a folder the user just clicked: normal thread priority, many threads, and it never waits
    /// on <see cref="ScanGate"/>. Background scans wait while one of these runs.
    /// </param>
    private static FolderTree Scan(string rootPath, IProgress<FolderScanProgress>? progress,
        CancellationToken cancellationToken, int? workerCount, bool prioritize, int volumeShare, bool measureAllocation)
    {
        using var errorMode = WindowsVolumePaths.SuppressCriticalErrors();
        var fullRoot = Path.GetFullPath(rootPath);
        var driveRoot = FolderTree.NormalizeRoot(fullRoot);
        var stopwatch = Stopwatch.StartNew();
        var resources = ScanResources.Capture();
        var fullVolume = string.Equals(fullRoot, driveRoot, StringComparison.OrdinalIgnoreCase);
        var volumeRoot = WindowsVolumePaths.GetContainingVolumeRoot(fullRoot, driveRoot);
        var fileSystem = (!prioritize || measureAllocation) && volumeRoot is not null && OperatingSystem.IsWindows()
            ? WindowsVolumePaths.ReadVolumeInformation(volumeRoot).FileSystem
            : string.Empty;
        var allocationIndex = volumeRoot is not null && string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase)
            ? DirectoryAllocationIndex.Create(volumeRoot, resources, fullVolume, volumeShare)
            : null;
        var trackAllocationWithoutFileIds = DirectoryAllocationReader.SupportsAllocationWithoutFileIds(fileSystem);
        var trackAllocation = allocationIndex is not null || trackAllocationWithoutFileIds;
        var allocationUnitBytes = allocationIndex is null ? 0 : WindowsVolumePaths.GetAllocationUnitBytes(volumeRoot!);
        var builder = new FolderTreeBuilder(driveRoot, trackAllocation);
        var builderLock = new object();
        var startNode = builder.AddFolder(Path.TrimEndingDirectorySeparator(fullRoot));

        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scanToken = workerCancellation.Token;
        using var queue = new BlockingCollection<WalkJob>();
        var pending = 1;
        var queuedStreamBatches = 0;
        long filesSeen = 0;
        queue.Add(new WalkJob(startNode, fullRoot), scanToken);

        var workers = workerCount is int requested
            ? Math.Clamp(requested, 1, 16)
            : resources.WalkWorkerCount(ScanResources.ReadSeekPenalty(volumeRoot), prioritize, volumeShare);
        DirectoryAllocationReader.TryScheduleStreams? scheduleStreams = allocationIndex is not null && workers > 1
            ? TryQueueStreams
            : null;
        var threads = new List<Thread>(workers);
        Exception? failure = null;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        // Stream jobs use the same workers as directory jobs. Bound the backlog so a wide
        // directory can spend spare capacity without flooding the queue with file IDs and handles.
        bool TryQueueStreams(int node, SafeFileHandle hint, ReadOnlySpan<ulong> ids)
        {
            if (queue.Count >= workers)
            {
                return false;
            }

            if (Interlocked.Increment(ref queuedStreamBatches) >= workers)
            {
                Interlocked.Decrement(ref queuedStreamBatches);
                return false;
            }

            SafeFileHandle? copy = null;
            var queued = false;
            try
            {
                copy = DirectoryAllocationReader.DuplicateVolumeHint(hint);
                if (copy is null)
                {
                    return false;
                }

                var batch = new StreamBatch(copy, ids.ToArray());
                Interlocked.Increment(ref pending);
                queue.Add(new WalkJob(node, null, Streams: batch), scanToken);
                queued = true;
                return true;
            }
            finally
            {
                if (!queued)
                {
                    copy?.Dispose();
                    Interlocked.Decrement(ref queuedStreamBatches);
                }
            }
        }

        void QueueChildJobs(int firstNode, string parentPath, List<string> names)
        {
            if (names.Count == 0)
            {
                return;
            }

            var batchSize = GetDirectoryBatchSize(names.Count, workers);
            Interlocked.Add(ref pending, 1 + (names.Count - 1) / batchSize);
            if (batchSize == 1)
            {
                for (var index = 0; index < names.Count; index++)
                {
                    queue.Add(new WalkJob(firstNode + index, Path.Combine(parentPath, names[index])), scanToken);
                }

                return;
            }

            // All batches share the parent path and the names. Form full paths only as workers open them.
            var children = names.ToArray();
            for (var offset = 0; offset < children.Length; offset += batchSize)
            {
                var length = Math.Min(batchSize, children.Length - offset);
                queue.Add(new WalkJob(firstNode + offset, parentPath, children.AsMemory(offset, length)), scanToken);
            }
        }

        void WaitForScanSlot()
        {
            if (!prioritize)
            {
                ScanGate.Wait(scanToken);
            }
            else
            {
                scanToken.ThrowIfCancellationRequested();
            }
        }

        void CompleteWork()
        {
            if (Interlocked.Decrement(ref pending) == 0)
            {
                queue.CompleteAdding();
            }
        }

        void ConsumeWork()
        {
            try
            {
                using var workerErrorMode = WindowsVolumePaths.SuppressCriticalErrors();
                var directoryReader = OperatingSystem.IsWindows()
                    ? new DirectoryAllocationReader(allocationIndex, allocationUnitBytes, workers, prioritize,
                        measureAllocation: trackAllocation)
                    : null;
                var subfolders = new List<string>();
                foreach (var (node, directory, children, streams) in queue.GetConsumingEnumerable(scanToken))
                {
                    using var hint = streams?.Hint;
                    if (streams is not null)
                    {
                        WaitForScanSlot();
                        Interlocked.Decrement(ref queuedStreamBatches);
                        var incompleteStreams = false;
                        var streamBytes = directoryReader!.ReadFileStreams(streams.Hint, streams.FileIds, scanToken, ref incompleteStreams);
                        lock (builderLock)
                        {
                            builder.AddFilesToFolder(node, 0, 0, streamBytes);
                            if (incompleteStreams)
                            {
                                builder.MarkIncompleteAllocation(node);
                            }
                        }

                        CompleteWork();
                        continue;
                    }

                    if (children.IsEmpty)
                    {
                        ProcessDirectory(node, directory!);
                    }
                    else
                    {
                        var names = children.Span;
                        for (var index = 0; index < names.Length; index++)
                        {
                            ProcessDirectory(node + index, Path.Combine(directory!, names[index]));
                        }
                    }

                    CompleteWork();
                }

                void ProcessDirectory(int node, string path)
                {
                    WaitForScanSlot();
                    long bytes = 0;
                    long files = 0;
                    long allocatedBytes = 0;
                    var incomplete = false;
                    var incompleteAllocation = false;
                    subfolders.Clear();
                    if (directoryReader is null ||
                        !directoryReader.TryRead(path, node, subfolders, scheduleStreams, scanToken,
                            out bytes, out allocatedBytes, out files, out incompleteAllocation))
                    {
                        var pathAllocation = trackAllocationWithoutFileIds ? directoryReader : null;
                        var fallback = ReadManagedDirectory(path, subfolders, options, pathAllocation, scanToken);
                        (bytes, files, incomplete) = (fallback.LogicalBytes, fallback.Files, fallback.Incomplete);
                        if (pathAllocation is not null)
                        {
                            // FAT has no shared file-ID claims. Replace any partial native total.
                            allocatedBytes = fallback.AllocatedBytes;
                            incompleteAllocation = fallback.IncompleteAllocation;
                        }
                        else
                        {
                            // NTFS has already claimed IDs from the native batches. Retain that partial allocation.
                            incompleteAllocation = trackAllocation;
                        }
                    }

                    int firstChild;
                    lock (builderLock)
                    {
                        builder.AddFilesToFolder(node, bytes, files, allocatedBytes);
                        if (incomplete)
                        {
                            builder.MarkIncompleteDirectory(node);
                        }

                        if (incompleteAllocation)
                        {
                            builder.MarkIncompleteAllocation(node);
                        }

                        firstChild = builder.AppendEnumeratedChildren(node, subfolders);
                    }

                    QueueChildJobs(firstChild, path, subfolders);
                    Interlocked.Add(ref filesSeen, files);
                }
            }
            catch (OperationCanceledException) when (scanToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
                workerCancellation.Cancel();
            }
        }

        try
        {
            for (var w = 0; w < workers; w++)
            {
                var thread = new Thread(ConsumeWork)
                {
                    IsBackground = true,
                    Priority = prioritize ? ThreadPriority.Normal : ThreadPriority.BelowNormal,
                    Name = $"FolderWalker-{w}"
                };
                threads.Add(thread);
                thread.Start();
            }

            while (threads.FirstOrDefault(thread => thread.IsAlive) is { } worker)
            {
                if (worker.Join(TimeSpan.FromMilliseconds(500)))
                {
                    continue;
                }

                progress?.Report(new FolderScanProgress(
                    driveRoot,
                    EngineName,
                    $"Walking folders: {Interlocked.Read(ref filesSeen):N0} files in {builder.FolderCount:N0} folders so far.",
                    Interlocked.Read(ref filesSeen),
                    stopwatch.Elapsed));
            }
        }
        finally
        {
            workerCancellation.Cancel();
            foreach (var thread in threads)
            {
                if (thread.IsAlive)
                {
                    thread.Join();
                }
            }

            while (queue.TryTake(out var remaining))
            {
                remaining.Streams?.Hint.Dispose();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            throw new InvalidOperationException($"Folder walk of {fullRoot} failed: {failure.Message}", failure);
        }

        stopwatch.Stop();
        return builder.Build(EngineName, stopwatch.Elapsed);
    }

    internal static (long LogicalBytes, long Files, long AllocatedBytes, bool Incomplete, bool IncompleteAllocation)
        ReadManagedDirectory(string path, List<string> subfolders, EnumerationOptions options,
            DirectoryAllocationReader? pathAllocation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        subfolders.Clear();
        long bytes = 0;
        long files = 0;
        long allocatedBytes = 0;
        var incompleteAllocation = false;
        try
        {
            // A queued directory can be replaced by a reparse point after its parent was read.
            if ((File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0)
            {
                return (0, 0, 0, false, pathAllocation is not null);
            }

            if (pathAllocation is not null)
            {
                incompleteAllocation = !pathAllocation.TryReadPathAllocation(path, expectedFileLength: null,
                    cancellationToken, out allocatedBytes);
            }

            var includeFileNames = pathAllocation is not null;
            var entries = new FileSystemEnumerable<(string? Name, bool IsDirectory, long Length, bool ReparsePoint)>(path,
                (ref FileSystemEntry entry) => (includeFileNames || entry.IsDirectory ? entry.FileName.ToString() : null,
                    entry.IsDirectory, entry.Length, (entry.Attributes & System.IO.FileAttributes.ReparsePoint) != 0),
                options);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                {
                    if (!entry.ReparsePoint)
                    {
                        subfolders.Add(entry.Name!);
                    }
                    else
                    {
                        incompleteAllocation |= pathAllocation is not null;
                    }

                    continue;
                }

                bytes += entry.Length;
                files++;
                if (pathAllocation is not null)
                {
                    if (entry.ReparsePoint || !pathAllocation.TryReadPathAllocation(Path.Combine(path, entry.Name!),
                        entry.Length, cancellationToken, out var allocation))
                    {
                        incompleteAllocation = true;
                    }
                    else
                    {
                        allocatedBytes += allocation;
                    }
                }
            }

            return (bytes, files, allocatedBytes, false, incompleteAllocation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (bytes, files, allocatedBytes, true, pathAllocation is not null);
        }
    }

    // Leave multiple jobs per worker on wide folders so a single slow directory cannot own the queue.
    internal static int GetDirectoryBatchSize(int childCount, int workers) =>
        childCount < MaximumDirectoriesPerJob ? 1 :
            Math.Min(MaximumDirectoriesPerJob, 1 + (childCount - 1) / (workers * 2));

    private readonly record struct WalkJob(int Node, string? Path, ReadOnlyMemory<string> Children = default,
        StreamBatch? Streams = null);

    private sealed record StreamBatch(SafeFileHandle Hint, ulong[] FileIds);
}
