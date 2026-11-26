using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
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
                measureAllocation: !prioritize || measureAllocation),
            cancellationToken);
    }

    internal Task<FolderTree> ScanVolumeAsync(string rootPath, IProgress<FolderScanProgress>? progress,
        CancellationToken cancellationToken, int volumeShare, bool measureAllocation = true)
    {
        return Task.Run(() => Scan(rootPath, progress, cancellationToken, workerCount: null, prioritize: false, volumeShare,
                measureAllocation),
            cancellationToken);
    }

    internal Task<FolderTree> ScanPriorityFolderAsync(string rootPath, CancellationToken cancellationToken, bool measureAllocation)
    {
        return Task.Run(() => Scan(rootPath, progress: null, cancellationToken, workerCount: null, prioritize: true,
                volumeShare: 1, measureAllocation, pauseBackgroundScans: true),
            cancellationToken);
    }

    /// <param name="prioritize">
    /// Uses normal thread priority and never waits on <see cref="ScanGate"/>.
    /// </param>
    private static FolderTree Scan(string rootPath, IProgress<FolderScanProgress>? progress,
        CancellationToken cancellationToken, int? workerCount, bool prioritize, int volumeShare, bool measureAllocation,
        bool pauseBackgroundScans = false)
    {
        using var errorMode = WindowsVolumePaths.SuppressCriticalErrors();
        var fullRoot = Path.GetFullPath(rootPath);
        var driveRoot = FolderTree.NormalizeRoot(fullRoot);
        var stopwatch = Stopwatch.StartNew();
        var resources = ScanResources.Capture();
        var fullVolume = string.Equals(fullRoot, driveRoot, StringComparison.OrdinalIgnoreCase);
        var volumeRoot = WindowsVolumePaths.GetContainingVolumeRoot(fullRoot, driveRoot);
        cancellationToken.ThrowIfCancellationRequested();
        using var pause = pauseBackgroundScans ? ScanGate.Pause(DiskCapacityInventory.ReadBackingDiskNumbers(volumeRoot)) : null;

        // Fast native Rust parallel walk for logical-size scans when no live-progress callback is needed.
        if (!measureAllocation && progress is null && Native.NativeFastEngine.IsAvailable)
        {
            var nativeWorkers = workerCount is int requestedNative
                ? Math.Clamp(requestedNative, 1, 24)
                : resources.WalkWorkerCount(ScanResources.ReadSeekPenalty(volumeRoot), prioritize, volumeShare);
            var nativeWalk = Native.NativeFastEngine.TryWalkTreeNative(fullRoot, nativeWorkers, cancellationToken);
            if (nativeWalk is not null)
            {
                return nativeWalk.Tree;
            }
        }
        var fileSystem = measureAllocation && volumeRoot is not null && OperatingSystem.IsWindows()
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
        var logicalSubfolders = !measureAllocation && progress is not null
            ? new LogicalFolderWalk(fullRoot, startNode, builder, builderLock) : null;
        // Work down each branch in the logical pass so more subtrees finish while the drive is still being read.
        using var queue = logicalSubfolders is null
            ? new BlockingCollection<WalkJob>()
            : new BlockingCollection<WalkJob>(new ConcurrentStack<WalkJob>());
        var pending = 1;
        var queuedStreamBatches = 0;
        long filesSeen = 0;
        List<CompletedFolderSize>? completedFolders = progress is null ? null : [];
        queue.Add(new WalkJob(startNode, fullRoot), scanToken);

        var workers = workerCount is int requested
            ? Math.Clamp(requested, 1, 16)
            : resources.WalkWorkerCount(ScanResources.ReadSeekPenalty(volumeRoot), prioritize, volumeShare);
        var shareStreamJobs = allocationIndex is not null && workers > 1;
        // A priority walk can reuse the scan thread when it does not report progress.
        var scanThreadWorks = prioritize && progress is null;
        var dedicatedWorkers = workers - (scanThreadWorks ? 1 : 0);
        var threads = new List<Thread>(dedicatedWorkers);
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
        bool TryQueueStreams(int node, SafeFileHandle hint, ReadOnlySpan<ulong> ids, FolderBranch? branch)
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
                if (branch is not null)
                {
                    lock (builderLock)
                    {
                        branch.Pending++;
                    }
                }

                queue.Add(new WalkJob(node, null, Streams: batch, Branch: branch), scanToken);
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

        void QueueChildJobs(int firstNode, string parentPath, List<string> names, FolderBranch? branch, bool startBranches)
        {
            if (names.Count == 0)
            {
                return;
            }

            var batchSize = startBranches ? 1 : GetDirectoryBatchSize(names.Count, workers);
            Interlocked.Add(ref pending, 1 + (names.Count - 1) / batchSize);
            if (batchSize == 1)
            {
                for (var index = 0; index < names.Count; index++)
                {
                    var childPath = Path.Combine(parentPath, names[index]);
                    var childBranch = startBranches ? new FolderBranch(childPath) : branch;
                    queue.Add(new WalkJob(firstNode + index, childPath, Branch: childBranch), scanToken);
                }

                return;
            }

            // All batches share the parent path and the names. Form full paths only as workers open them.
            var children = names.ToArray();
            for (var offset = 0; offset < children.Length; offset += batchSize)
            {
                var length = Math.Min(batchSize, children.Length - offset);
                queue.Add(new WalkJob(firstNode + offset, parentPath, children.AsMemory(offset, length), Branch: branch), scanToken);
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

        // Called while the builder is locked. A branch settles after its directories and any queued stream work settle.
        void FinishBranchWork(FolderBranch? branch, int children, long logicalBytes, long allocatedBytes, long files,
            bool incomplete, bool incompleteAllocation)
        {
            if (branch is null)
            {
                return;
            }

            branch.LogicalBytes += Math.Max(0, logicalBytes);
            branch.AllocatedBytes += allocatedBytes;
            branch.Files += files;
            branch.Incomplete |= incomplete;
            branch.IncompleteAllocation |= incompleteAllocation;
            branch.Pending += children - 1;
            if (branch.Pending == 0)
            {
                completedFolders!.Add(new CompletedFolderSize(branch.Path,
                    branch.Incomplete ? null : branch.LogicalBytes,
                    trackAllocation && !branch.Incomplete && !branch.IncompleteAllocation ? branch.AllocatedBytes : null,
                    branch.Files, measureAllocation));
            }
        }

        void ReportProgress(bool completedOnly = false)
        {
            if (progress is null)
            {
                return;
            }

            CompletedFolderSize[] ready;
            LiveFolderSize[] subfolders;
            int folders;
            lock (builderLock)
            {
                ready = completedFolders!.Count > 0 ? completedFolders.ToArray() : [];
                completedFolders.Clear();
                subfolders = logicalSubfolders?.Drain() ?? [];
                folders = builder.FolderCount;
            }

            if (!completedOnly || ready.Length > 0 || subfolders.Length > 0)
            {
                var files = Interlocked.Read(ref filesSeen);
                var phase = measureAllocation ? "Measuring allocation" : "Measuring logical sizes";
                progress.Report(new FolderScanProgress(
                    driveRoot, EngineName, $"{phase}: {files:N0} files in {folders:N0} folders so far.",
                    files, stopwatch.Elapsed)
                {
                    CompletedFolders = ready,
                    LiveSubfolders = subfolders,
                    LiveWalk = logicalSubfolders
                });
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
                FolderBranch? currentBranch = null;
                DirectoryAllocationReader.TryScheduleStreams? scheduleStreams = shareStreamJobs
                    ? (int node, SafeFileHandle hint, ReadOnlySpan<ulong> ids) => TryQueueStreams(node, hint, ids, currentBranch)
                    : null;
                foreach (var (node, directory, children, streams, branch) in queue.GetConsumingEnumerable(scanToken))
                {
                    currentBranch = branch;
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

                            FinishBranchWork(branch, 0, 0, streamBytes, 0, false, incompleteStreams);
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
                        logicalSubfolders?.FinishListing(node, firstChild, subfolders.Count, bytes, incomplete);
                        FinishBranchWork(currentBranch, subfolders.Count, bytes, allocatedBytes, files, incomplete, incompleteAllocation);
                    }

                    QueueChildJobs(firstChild, path, subfolders, currentBranch, completedFolders is not null && node == startNode);
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
            for (var w = 0; w < dedicatedWorkers; w++)
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

            if (scanThreadWorks)
            {
                ConsumeWork();
            }

            while (threads.FirstOrDefault(thread => thread.IsAlive) is { } worker)
            {
                if (worker.Join(TimeSpan.FromMilliseconds(500)))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress();
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

        ReportProgress(completedOnly: true);
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
        StreamBatch? Streams = null, FolderBranch? Branch = null);

    private sealed class FolderBranch(string path)
    {
        public string Path { get; } = path;
        public int Pending = 1;
        public long LogicalBytes;
        public long AllocatedBytes;
        public long Files;
        public bool Incomplete;
        public bool IncompleteAllocation;
    }

    internal sealed class LogicalFolderWalk
    {
        private const int BatchSize = 4;
        private readonly int _root;
        private readonly FolderTreeBuilder _builder;
        private readonly object _sync;
        private readonly List<FolderState> _folders = [];
        private readonly Queue<(int Node, long Bytes)> _ready = new(BatchSize);

        public LogicalFolderWalk(string scanRoot, int root, FolderTreeBuilder builder, object sync)
        {
            ScanRoot = scanRoot;
            _root = root;
            _builder = builder;
            _sync = sync;
            CollectionsMarshal.SetCount(_folders, root + 1);
        }

        public string ScanRoot { get; }

        // The worker holds the builder lock and registers each child before its job is queued.

        public void FinishListing(int node, int firstChild, int childCount, long bytes, bool incomplete)
        {
            if (childCount > 0)
            {
                CollectionsMarshal.SetCount(_folders, firstChild + childCount);
                CollectionsMarshal.AsSpan(_folders).Slice(firstChild, childCount).Fill(new FolderState { Parent = node });
            }

            var folders = CollectionsMarshal.AsSpan(_folders);
            folders[node].FirstChild = firstChild;
            folders[node].ChildCount = childCount;
            folders[node].RemainingChildren = childCount;
            folders[node].IsListed = true;

            // Credit only the files read in this directory. Settling a subtree never adds those bytes again.
            for (var ancestor = node; ; ancestor = folders[ancestor].Parent)
            {
                folders[ancestor].ScannedBytes += bytes;
                folders[ancestor].Incomplete |= incomplete;
                if (ancestor == _root)
                {
                    break;
                }
            }

            // The last child can finish its parent and other ancestors.
            while (node != _root && folders[node].RemainingChildren == 0)
            {
                ref var folder = ref folders[node];
                var parent = folder.Parent;
                if (parent != _root && !folder.Incomplete && folder.ScannedBytes > 0)
                {
                    if (_ready.Count == BatchSize)
                    {
                        _ready.Dequeue();
                    }

                    _ready.Enqueue((node, folder.ScannedBytes));
                }

                folders[parent].RemainingChildren--;
                node = parent;
            }
        }

        public LiveFolderSize[] Drain()
        {
            if (_ready.Count == 0)
            {
                return [];
            }

            var results = new LiveFolderSize[_ready.Count];
            for (var index = 0; index < results.Length; index++)
            {
                var (node, bytes) = _ready.Dequeue();
                results[index] = new LiveFolderSize(_builder.GetPath(node), bytes);
            }

            return results;
        }

        public LiveFolderView Capture(string path, IReadOnlyList<string> previewNames, IReadOnlySet<string> expanded,
            IReadOnlyDictionary<string, long> quickSizes, int limit)
        {
            lock (_sync)
            {
                var parent = FindNode(path);
                var rows = new List<LiveFolderRow>(previewNames.Count);
                var rootListed = parent >= 0 && _folders[parent].IsListed;
                if (rootListed)
                {
                    AddChildren(_folders[parent], path, 0);
                }
                else
                {
                    foreach (var name in previewNames.OrderByDescending(name => PreviewSize(path, name)))
                    {
                        AddRow(-1, name, path, 0);
                    }
                }

                return new LiveFolderView(parent < 0 ? 0 : _folders[parent].ScannedBytes, rows,
                    rootListed ? _folders[parent].ChildCount > limit : null);

                long PreviewSize(string parentPath, string name) =>
                    quickSizes.Count > 0 && quickSizes.TryGetValue(Path.Join(parentPath, name), out var bytes) ? bytes : -1;

                long SortSize(int child, string parentPath, int depth)
                {
                    var folder = _folders[child];
                    var settled = folder.IsListed && folder.RemainingChildren == 0;
                    var quick = !settled && depth == 0 ? PreviewSize(parentPath, _builder.GetName(child)) : -1;
                    if (quick >= 0)
                    {
                        return quick;
                    }

                    return folder.ScannedBytes > 0 || (settled && !folder.Incomplete) ? folder.ScannedBytes : -1;
                }

                void AddChildren(FolderState parentFolder, string parentPath, int depth)
                {
                    var children = Enumerable.Range(parentFolder.FirstChild, parentFolder.ChildCount);
                    foreach (var child in children.OrderByDescending(child => SortSize(child, parentPath, depth))
                                 .ThenBy(_builder.GetName, StringComparer.OrdinalIgnoreCase).Take(limit))
                    {
                        AddRow(child, _builder.GetName(child), parentPath, depth);
                    }
                }

                void AddRow(int node, string name, string parentPath, int depth)
                {
                    var fullPath = Path.Join(parentPath, name);
                    var folder = node < 0 ? default : _folders[node];
                    var canExpand = !folder.IsListed || folder.ChildCount > 0;
                    var isExpanded = canExpand && expanded.Contains(fullPath);
                    var settled = folder.IsListed && folder.RemainingChildren == 0;
                    rows.Add(new LiveFolderRow(name, fullPath, depth, folder.ScannedBytes, settled,
                        settled && !folder.Incomplete, canExpand, isExpanded));

                    if (isExpanded)
                    {
                        AddChildren(folder, fullPath, depth + 1);
                    }
                }
            }
        }

        private int FindNode(string path)
        {
            var node = _root;
            var relative = Path.GetRelativePath(ScanRoot, path);
            if (relative == ".")
            {
                return node;
            }

            foreach (var name in relative.Split(Path.DirectorySeparatorChar))
            {
                node = FindChild(node, name);
                if (node < 0)
                {
                    break;
                }
            }

            return node;
        }

        private int FindChild(int node, string name)
        {
            if (node < 0)
            {
                return -1;
            }

            var parent = _folders[node];
            var end = parent.FirstChild + parent.ChildCount;
            for (var child = parent.FirstChild; child < end; child++)
            {
                if (_builder.GetName(child).Equals(name, StringComparison.Ordinal))
                {
                    return child;
                }
            }

            return -1;
        }

        private struct FolderState
        {
            public int Parent;
            public int FirstChild;
            public int ChildCount;
            public int RemainingChildren;
            public long ScannedBytes;
            public bool IsListed;
            public bool Incomplete;
        }
    }

    private sealed record StreamBatch(SafeFileHandle Hint, ulong[] FileIds);
}
