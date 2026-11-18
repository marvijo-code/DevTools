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
            ? DirectoryAllocationIndex.Create(volumeRoot, resources, fullVolume)
            : null;
        var trackAllocation = allocationIndex is not null || DirectoryAllocationReader.SupportsAllocationWithoutFileIds(fileSystem);
        var allocationUnitBytes = allocationIndex is null ? 0 : WindowsVolumePaths.GetAllocationUnitBytes(volumeRoot!);
        var builder = new FolderTreeBuilder(driveRoot, trackAllocation);
        var builderLock = new object();
        var startNode = builder.AddFolder(Path.TrimEndingDirectorySeparator(fullRoot));

        using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var scanToken = workerCancellation.Token;
        using var queue = new BlockingCollection<(int Node, string? Path, StreamBatch? Streams)>();
        var pending = 1;
        var queuedStreamBatches = 0;
        long filesSeen = 0;
        queue.Add((startNode, fullRoot, null), scanToken);

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
                queue.Add((node, null, batch), scanToken);
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
                var allocationReader = trackAllocation
                    ? new DirectoryAllocationReader(allocationIndex, allocationUnitBytes, workers, prioritize)
                    : null;
                var subfolders = new List<string>();
                foreach (var (node, directory, streams) in queue.GetConsumingEnumerable(scanToken))
                {
                    using var hint = streams?.Hint;
                    if (!prioritize)
                    {
                        ScanGate.Wait(scanToken);
                    }

                    if (streams is not null)
                    {
                        Interlocked.Decrement(ref queuedStreamBatches);
                        var incompleteStreams = false;
                        var streamBytes = allocationReader!.ReadFileStreams(streams.Hint, streams.FileIds, scanToken, ref incompleteStreams);
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

                    var path = directory!;
                    long bytes = 0;
                    long files = 0;
                    long allocatedBytes = 0;
                    var incomplete = false;
                    var incompleteAllocation = false;
                    subfolders.Clear();
                    if (allocationReader is null ||
                        !allocationReader.TryRead(path, node, subfolders, scheduleStreams, scanToken,
                            out bytes, out allocatedBytes, out files, out incompleteAllocation))
                    {
                        // Retry the logical listing if the filesystem cannot return allocation data.
                        incompleteAllocation = allocationReader is not null;
                        bytes = 0;
                        files = 0;
                        subfolders.Clear();
                        try
                        {
                            // A queued directory can be replaced by a reparse point after its parent was read.
                            var attributes = File.GetAttributes(path);
                            var isReparsePoint = (attributes & System.IO.FileAttributes.ReparsePoint) != 0;
                            // A name string is only needed for folders; files just add their length.
                            IEnumerable<(string? Name, bool IsDirectory, long Length, System.IO.FileAttributes Attributes)> entries = isReparsePoint ? [] : new FileSystemEnumerable<(string? Name, bool IsDirectory, long Length, System.IO.FileAttributes Attributes)>(
                                path,
                                (ref FileSystemEntry entry) => (entry.IsDirectory ? entry.FileName.ToString() : null, entry.IsDirectory, entry.Length, entry.Attributes),
                                options);
                            foreach (var entry in entries)
                            {
                                scanToken.ThrowIfCancellationRequested();
                                if (entry.IsDirectory)
                                {
                                    if ((entry.Attributes & System.IO.FileAttributes.ReparsePoint) == 0)
                                    {
                                        subfolders.Add(entry.Name!);
                                    }
                                }
                                else
                                {
                                    bytes += entry.Length;
                                    files++;
                                }
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            incomplete = true;
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

                    Interlocked.Add(ref pending, subfolders.Count);
                    for (var i = 0; i < subfolders.Count; i++)
                    {
                        queue.Add((firstChild + i, Path.Combine(path, subfolders[i]), null), scanToken);
                    }

                    Interlocked.Add(ref filesSeen, files);
                    CompleteWork();
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

    private sealed record StreamBatch(SafeFileHandle Hint, ulong[] FileIds);
}
