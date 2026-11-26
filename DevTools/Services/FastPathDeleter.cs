using System.Collections.Concurrent;
using System.Diagnostics;
using DevTools.Services.Native;
using FileAttributes = System.IO.FileAttributes;

namespace DevTools.Services;

/// <summary>
/// High-throughput file and folder deleter used for worktrees, project build outputs, and Clear Folder.
/// Uses the native Rust parallel POSIX-semantics deleter when available, and falls back to a parallel
/// managed deleter that safely unlinks directory junctions without recursing into their targets.
/// </summary>
public static class FastPathDeleter
{
    public static Task<FastDeleteOutcome> DeleteAsync(
        string path,
        bool keepRoot = false,
        int workerCount = 0,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Delete(path, keepRoot, workerCount, cancellationToken), cancellationToken);
    }

    public static FastDeleteOutcome Delete(
        string path,
        bool keepRoot = false,
        int workerCount = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new FastDeleteOutcome(0, 0, 0, 0, false, 0, false, TimeSpan.Zero);
        }

        var full = Path.GetFullPath(path);
        if (NativeFastEngine.TryDeleteNative(full, keepRoot, workerCount, cancellationToken) is { } nativeOutcome)
        {
            return nativeOutcome;
        }

        return DeleteManagedParallel(full, keepRoot, workerCount, cancellationToken);
    }

    internal static FastDeleteOutcome DeleteManagedParallel(
        string fullPath,
        bool keepRoot,
        int workerCount,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            clock.Stop();
            return new FastDeleteOutcome(0, 0, 0, 0, !keepRoot, 0, false, clock.Elapsed);
        }

        FileAttributes rootAttrs;
        try
        {
            rootAttrs = File.GetAttributes(fullPath);
        }
        catch (Exception)
        {
            clock.Stop();
            return new FastDeleteOutcome(0, 0, 0, 1, false, 5, false, clock.Elapsed);
        }

        if ((rootAttrs & FileAttributes.Directory) == 0)
        {
            if (keepRoot)
            {
                clock.Stop();
                return new FastDeleteOutcome(0, 0, 0, 0, false, 0, false, clock.Elapsed);
            }

            try
            {
                var length = new FileInfo(fullPath).Length;
                if ((rootAttrs & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                {
                    File.SetAttributes(fullPath, FileAttributes.Normal);
                }

                File.Delete(fullPath);
                clock.Stop();
                return new FastDeleteOutcome(1, 0, length, 0, true, 0, false, clock.Elapsed);
            }
            catch
            {
                clock.Stop();
                return new FastDeleteOutcome(0, 0, 0, 1, false, 5, false, clock.Elapsed);
            }
        }

        // Reparse point (junction/symlink): unlink ONLY the link itself.
        if ((rootAttrs & FileAttributes.ReparsePoint) != 0)
        {
            if (keepRoot)
            {
                clock.Stop();
                return new FastDeleteOutcome(0, 0, 0, 0, false, 0, false, clock.Elapsed);
            }

            try
            {
                Directory.Delete(fullPath, recursive: false);
                clock.Stop();
                return new FastDeleteOutcome(0, 1, 0, 0, true, 0, false, clock.Elapsed);
            }
            catch
            {
                clock.Stop();
                return new FastDeleteOutcome(0, 0, 0, 1, false, 5, false, clock.Elapsed);
            }
        }

        var workers = workerCount > 0
            ? Math.Clamp(workerCount, 1, 32)
            : Math.Clamp(Environment.ProcessorCount, 4, 16);

        var discoveredDirs = new ConcurrentBag<(int Depth, string Path)>();
        var queue = new ConcurrentQueue<(int Depth, string Path)>();
        queue.Enqueue((0, fullPath));
        var active = 1;
        long filesDeleted = 0;
        long dirsDeleted = 0;
        long bytesFreed = 0;
        long skipped = 0;

        using var workSignal = new ManualResetEventSlim(true);

        void Worker()
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                ReturnSpecialDirectories = false,
                AttributesToSkip = 0
            };

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!queue.TryDequeue(out var item))
                {
                    if (Volatile.Read(ref active) == 0)
                    {
                        break;
                    }

                    workSignal.Wait(10, cancellationToken);
                    continue;
                }

                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(item.Path, "*", options))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            break;
                        }

                        try
                        {
                            var attrs = File.GetAttributes(entry);
                            if ((attrs & FileAttributes.Directory) != 0)
                            {
                                if ((attrs & FileAttributes.ReparsePoint) != 0)
                                {
                                    Directory.Delete(entry, recursive: false);
                                    Interlocked.Increment(ref dirsDeleted);
                                }
                                else
                                {
                                    var nextDepth = item.Depth + 1;
                                    discoveredDirs.Add((nextDepth, entry));
                                    Interlocked.Increment(ref active);
                                    queue.Enqueue((nextDepth, entry));
                                    workSignal.Set();
                                }
                            }
                            else
                            {
                                var len = new FileInfo(entry).Length;
                                if ((attrs & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Hidden)) != 0)
                                {
                                    File.SetAttributes(entry, FileAttributes.Normal);
                                }

                                File.Delete(entry);
                                Interlocked.Increment(ref filesDeleted);
                                Interlocked.Add(ref bytesFreed, len);
                            }
                        }
                        catch
                        {
                            Interlocked.Increment(ref skipped);
                        }
                    }
                }
                catch
                {
                    Interlocked.Increment(ref skipped);
                }
                finally
                {
                    if (Interlocked.Decrement(ref active) == 0)
                    {
                        workSignal.Set();
                    }
                }
            }
        }

        var tasks = new Task[workers];
        for (var i = 0; i < workers; i++)
        {
            tasks[i] = Task.Run(Worker, cancellationToken);
        }

        Task.WaitAll(tasks);

        if (!cancellationToken.IsCancellationRequested)
        {
            foreach (var (_, dir) in discoveredDirs.OrderByDescending(d => d.Depth))
            {
                try
                {
                    var attrs = File.GetAttributes(dir);
                    if ((attrs & (FileAttributes.ReadOnly | FileAttributes.System)) != 0)
                    {
                        File.SetAttributes(dir, FileAttributes.Normal);
                    }

                    Directory.Delete(dir, recursive: false);
                    dirsDeleted++;
                }
                catch
                {
                    skipped++;
                }
            }
        }

        var rootRemoved = false;
        if (!keepRoot && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if ((rootAttrs & (FileAttributes.ReadOnly | FileAttributes.System)) != 0)
                {
                    File.SetAttributes(fullPath, FileAttributes.Normal);
                }

                Directory.Delete(fullPath, recursive: false);
                dirsDeleted++;
                rootRemoved = true;
            }
            catch
            {
                skipped++;
            }
        }

        clock.Stop();
        return new FastDeleteOutcome(filesDeleted, dirsDeleted, bytesFreed, skipped, rootRemoved, 0, false, clock.Elapsed);
    }
}
