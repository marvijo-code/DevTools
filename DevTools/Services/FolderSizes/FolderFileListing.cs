using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DevTools.Services.BiggestFiles;

namespace DevTools.Services.FolderSizes;

internal readonly record struct FolderFile(string Name, string Path, long Bytes);

internal sealed record FolderPreview(IReadOnlyList<string> FolderNames, bool HasMore);

internal sealed record FolderFileSnapshot(IReadOnlyList<FolderFile> Files, long TotalBytes, long FileCount)
{
    internal bool Matches(long bytes, long fileCount) => TotalBytes == bytes && FileCount == fileCount;
}

internal readonly record struct FolderFileRequest(int Node, string Path, long Bytes, long FileCount);
internal readonly record struct FolderFileResult(FolderFileRequest Folder, FolderFileSnapshot? Listing);

internal static class FolderFileListing
{

    private static readonly TimeSpan MinimumUpdateInterval = TimeSpan.FromMilliseconds(50);

    private static readonly EnumerationOptions ListingOptions = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        BufferSize = 16 * 1024
    };

    internal static async IAsyncEnumerable<IReadOnlyList<FolderFileResult>> ReadBatchesAsync(
        IReadOnlyList<FolderFileRequest> folders, Func<string, CancellationToken, FolderFileSnapshot?> read,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (folders.Count == 0)
        {
            yield break;
        }

        if (folders.Count == 1)
        {
            var folder = folders[0];
            var listing = await Task.Run(() => read(folder.Path, cancellationToken), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield return [new FolderFileResult(folder, listing)];
            yield break;
        }

        using var stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // The request list is finite. Let its reads finish even when the view is between redraws.
        var channel = Channel.CreateUnbounded<FolderFileResult>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        var producer = Task.Run(() =>
        {
            try
            {
                foreach (var folder in folders)
                {
                    stopped.Token.ThrowIfCancellationRequested();
                    var listing = read(folder.Path, stopped.Token);
                    channel.Writer.TryWrite(new FolderFileResult(folder, listing));
                }

                channel.Writer.Complete();
            }
            catch (Exception error)
            {
                channel.Writer.TryComplete(error);
            }
        });

        try
        {
            long lastUpdate = 0;
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (lastUpdate != 0 && !producer.IsCompleted)
                {
                    var delay = MinimumUpdateInterval - Stopwatch.GetElapsedTime(lastUpdate);
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.WhenAny(Task.Delay(delay, cancellationToken), producer).ConfigureAwait(false);
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                var ready = new List<FolderFileResult>();
                while (channel.Reader.TryRead(out var result))
                {
                    ready.Add(result);
                }

                lastUpdate = Stopwatch.GetTimestamp();
                yield return ready;
            }
        }
        finally
        {
            stopped.Cancel();
            await producer.ConfigureAwait(false);
        }
    }

    internal static FolderPreview? ReadChildFolders(string folder, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        using var errorMode = WindowsVolumePaths.SuppressCriticalErrors();
        var names = new List<string>(limit);
        var hasMore = false;
        try
        {
            if ((File.GetAttributes(folder) & System.IO.FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            var entries = new FileSystemEnumerable<string>(
                folder, static (ref FileSystemEntry entry) => entry.FileName.ToString(), ListingOptions)
            {
                ShouldIncludePredicate = (ref FileSystemEntry entry) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return entry.IsDirectory && (entry.Attributes & System.IO.FileAttributes.ReparsePoint) == 0;
                }
            };
            foreach (var name in entries)
            {
                if (names.Count == limit)
                {
                    hasMore = true;
                    break;
                }

                names.Add(name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return new FolderPreview(names, hasMore);
    }

    public static FolderFileSnapshot? ReadLargest(string folder, int limit, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var largest = new TopNAccumulator<string>(limit);
        string? directory = null;
        long totalBytes = 0;
        long fileCount = 0;
        try
        {
            if ((File.GetAttributes(folder) & System.IO.FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            var entries = new FileSystemEnumerable<(string Name, long Bytes)>(
                folder,
                (ref FileSystemEntry entry) =>
                {
                    // Capture the enumerator's full directory once. Only the winners need full file paths.
                    directory ??= entry.Directory.ToString();
                    return (entry.FileName.ToString(), Math.Max(0, entry.Length));
                },
                ListingOptions);
            entries.ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                {
                    return false;
                }

                var bytes = Math.Max(0, entry.Length);
                totalBytes = checked(totalBytes + bytes);
                fileCount++;
                return bytes > largest.Threshold;
            };

            foreach (var file in entries)
            {
                largest.TryAdd(file.Bytes, file.Name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        {
            return null;
        }

        var files = directory is null
            ? []
            : largest.Snapshot().Select(entry =>
                new FolderFile(entry.Item, Path.Join(directory, entry.Item), entry.SizeBytes)).ToArray();
        return new FolderFileSnapshot(files, totalBytes, fileCount);
    }
}
