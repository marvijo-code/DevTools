using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

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
        bool prioritize = false)
    {
        return Task.Run(() => Scan(rootPath, progress, cancellationToken, workerCount, prioritize), cancellationToken);
    }

    /// <param name="prioritize">
    /// True for a folder the user just clicked: normal thread priority, many threads, and it never waits
    /// on <see cref="ScanGate"/>. Background scans wait while one of these runs.
    /// </param>
    private static FolderTree Scan(string rootPath, IProgress<FolderScanProgress>? progress, CancellationToken cancellationToken, int? workerCount, bool prioritize)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        var driveRoot = FolderTree.NormalizeRoot(fullRoot);
        var stopwatch = Stopwatch.StartNew();
        var resources = ScanResources.Capture();
        var fullVolume = string.Equals(fullRoot, driveRoot, StringComparison.OrdinalIgnoreCase);
        var allocationIndex = prioritize ? null : DirectoryAllocationIndex.Create(driveRoot, resources, fullVolume);
        var allocationUnitBytes = allocationIndex is null ? 0 : WindowsVolumePaths.GetAllocationUnitBytes(driveRoot);
        var builder = new FolderTreeBuilder(driveRoot, trackAllocation: allocationIndex is not null);
        var builderLock = new object();
        var startNode = builder.AddFolder(Path.TrimEndingDirectorySeparator(fullRoot));

        using var queue = new BlockingCollection<(int Node, string Path)>(new ConcurrentQueue<(int, string)>());
        var pending = 1;
        long filesSeen = 0;
        queue.Add((startNode, fullRoot), cancellationToken);

        var workers = workerCount is int requested
            ? Math.Clamp(requested, 1, 16)
            : resources.WalkWorkerCount(ScanResources.ReadSeekPenalty(driveRoot), prioritize);
        var threads = new List<Thread>(workers);
        Exception? failure = null;
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        for (var w = 0; w < workers; w++)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    var allocationReader = allocationIndex is null ? null : new DirectoryAllocationReader(allocationIndex, allocationUnitBytes);
                    var subfolders = new List<string>();
                    foreach (var (node, path) in queue.GetConsumingEnumerable(cancellationToken))
                    {
                        if (!prioritize)
                        {
                            ScanGate.Wait(cancellationToken);
                        }

                        long bytes = 0;
                        long files = 0;
                        long allocatedBytes = 0;
                        subfolders.Clear();
                        if (allocationReader is null ||
                            !allocationReader.TryRead(path, subfolders, cancellationToken, out bytes, out allocatedBytes, out files))
                        {
                            // Retry the logical listing if the filesystem cannot return allocation data.
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
                            }
                        }

                        int firstChild;
                        lock (builderLock)
                        {
                            builder.AddFilesToFolder(node, bytes, files, allocatedBytes);
                            firstChild = builder.AppendEnumeratedChildren(node, subfolders);
                        }

                        Interlocked.Add(ref pending, subfolders.Count);
                        for (var i = 0; i < subfolders.Count; i++)
                        {
                            queue.Add((firstChild + i, Path.Combine(path, subfolders[i])), cancellationToken);
                        }

                        Interlocked.Add(ref filesSeen, files);
                        if (Interlocked.Decrement(ref pending) == 0)
                        {
                            queue.CompleteAdding();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    failure ??= ex;
                    queue.CompleteAdding();
                }
            })
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

        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            throw new InvalidOperationException($"Folder walk of {fullRoot} failed: {failure.Message}", failure);
        }

        stopwatch.Stop();
        return builder.Build(EngineName, stopwatch.Elapsed);
    }
}
