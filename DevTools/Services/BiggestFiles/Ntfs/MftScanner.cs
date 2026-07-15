using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace DevTools.Services.BiggestFiles.Ntfs;

/// <summary>
/// Fast-path scan engine that reads the NTFS Master File Table directly from a raw volume handle
/// and reconstructs file sizes and paths in memory. Requires elevation and an NTFS volume.
/// </summary>
public sealed class MftScanner
{
    private const string EngineName = "NTFS MFT";
    private const int TargetChunkBytes = 8 * 1024 * 1024;
    private const uint RootRecordNumber = 5;
    private const uint FirstUserRecordNumber = 24;
    private const int MaxAncestryDepth = 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    // Per-record flag bits.
    private const byte FlagInUse = 0x01;
    private const byte FlagDirectory = 0x02;
    private const byte FlagHasName = 0x04;
    private const byte FlagExtension = 0x08;
    private const byte FlagHasSize = 0x10;

    public async Task<BiggestFilesResult> ScanAsync(
        BiggestFilesRequest request,
        IProgress<BiggestFilesProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(request.ScanRoot);
        var pathRoot = Path.GetPathRoot(root);
        if (string.IsNullOrWhiteSpace(pathRoot) || pathRoot.Length < 2 || pathRoot[1] != ':')
        {
            throw new NotSupportedException($"MFT scan requires a drive-letter path, got '{root}'.");
        }

        var driveLetter = char.ToUpperInvariant(pathRoot[0]);
        var volumeRoot = $@"{driveLetter}:\";
        var stopwatch = Stopwatch.StartNew();

        using var volume = NativeMethods.OpenVolume(driveLetter);
        var geometry = NativeMethods.GetVolumeData(volume);
        var recordSize = (int)geometry.BytesPerFileRecordSegment;
        if (recordSize <= 0 || geometry.BytesPerCluster == 0)
        {
            throw new NotSupportedException("Invalid NTFS geometry reported by the volume.");
        }

        var recordCountLong = geometry.MftValidDataLength / recordSize;
        if (recordCountLong is <= 0 or > int.MaxValue)
        {
            throw new NotSupportedException($"Unsupported MFT record count: {recordCountLong}.");
        }

        var recordCount = (int)recordCountLong;

        // Structure-of-arrays table, indexed by MFT record number.
        var parent = new uint[recordCount];
        var flags = new byte[recordCount];
        var realSize = new long[recordCount];
        var allocSize = new long[recordCount];
        var lastWrite = new long[recordCount];
        var names = new string?[recordCount];
        var recordsParsed = 0;

        // Locate the MFT's own clusters from record 0.
        var extents = ReadMftExtents(volume, geometry);
        if (extents.Count == 0)
        {
            throw new NotSupportedException("Could not locate the MFT data runs.");
        }

        var reporter = StartReporter(progress, stopwatch, () => Volatile.Read(ref recordsParsed), recordCount, cancellationToken, out var stopReporter);

        try
        {
            await ParseAllRecordsAsync(
                volume, geometry, extents, recordSize, recordCount,
                parent, flags, realSize, allocSize, lastWrite, names,
                count => Volatile.Write(ref recordsParsed, count),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stopReporter();
            await reporter.ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Ancestry filter for subtree scans (volume-root scans include everything).
        var isSubtree = !string.Equals(TrimTrailingSeparators(root), TrimTrailingSeparators(volumeRoot), StringComparison.OrdinalIgnoreCase);
        var targetRecord = isSubtree ? ResolveDirectoryRecord(root, volumeRoot, parent, flags, names) : (uint?)null;
        if (isSubtree && targetRecord is null)
        {
            throw new DirectoryNotFoundException($"Could not resolve '{root}' in the MFT.");
        }

        var ancestry = targetRecord is null ? null : new byte[recordCount];

        var topN = new TopNAccumulator<int>(request.TopCount);
        long filesSeen = 0;
        long bytesSeen = 0;

        for (var index = 0; index < recordCount; index++)
        {
            var flag = flags[index];
            if ((flag & FlagInUse) == 0 || (flag & FlagHasName) == 0 || (flag & FlagDirectory) != 0 || (flag & FlagExtension) != 0)
            {
                continue;
            }

            if (index < FirstUserRecordNumber)
            {
                continue;
            }

            var size = realSize[index];
            filesSeen++;
            bytesSeen += size;

            if (targetRecord is not null && !IsUnder(index, targetRecord.Value, parent, flags, ancestry!))
            {
                continue;
            }

            topN.TryAdd(size, index);
        }

        var winners = topN.Snapshot();
        var pathCache = new Dictionary<uint, string>();
        var hits = new List<FileSizeHit>(winners.Count);
        foreach (var (size, index) in winners)
        {
            var fullPath = BuildPath(index, volumeRoot, parent, flags, names, pathCache);
            hits.Add(new FileSizeHit(fullPath, size, allocSize[index], ToModified(lastWrite[index])));
        }

        stopwatch.Stop();

        progress?.Report(new BiggestFilesProgress(
            "Done", filesSeen, bytesSeen, stopwatch.Elapsed,
            ComputeRate(recordCount, stopwatch.Elapsed), hits, IsCompleted: true));

        return new BiggestFilesResult(hits, filesSeen, bytesSeen, stopwatch.Elapsed, EngineName, Warning: null);
    }

    private static List<(long Lcn, long ClusterCount)> ReadMftExtents(Microsoft.Win32.SafeHandles.SafeFileHandle volume, NtfsVolumeData geometry)
    {
        var clusterBytes = (int)geometry.BytesPerCluster;
        var recordSize = (int)geometry.BytesPerFileRecordSegment;
        // Read enough whole clusters to cover record 0 (handles cluster sizes smaller than the record).
        var bytesToRead = ((recordSize + clusterBytes - 1) / clusterBytes) * clusterBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(bytesToRead);
        try
        {
            ReadExact(volume, buffer.AsSpan(0, bytesToRead), geometry.MftStartLcn * geometry.BytesPerCluster);
            var extents = new List<(long Lcn, long ClusterCount)>();
            MftRecordParser.TryReadUnnamedDataRuns(buffer.AsSpan(0, recordSize), extents);
            return extents;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ParseAllRecordsAsync(
        Microsoft.Win32.SafeHandles.SafeFileHandle volume,
        NtfsVolumeData geometry,
        List<(long Lcn, long ClusterCount)> extents,
        int recordSize,
        int recordCount,
        uint[] parent,
        byte[] flags,
        long[] realSize,
        long[] allocSize,
        long[] lastWrite,
        string?[] names,
        Action<int> reportParsed,
        CancellationToken cancellationToken)
    {
        var clusterBytes = (int)geometry.BytesPerCluster;
        var clustersPerChunk = Math.Max(1, TargetChunkBytes / clusterBytes);

        var channel = Channel.CreateBounded<Block>(new BoundedChannelOptions(4)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        // Extension records whose LowestVCN==0 $DATA carries the real size for a fragmented base file.
        var extensionSizes = new List<(uint BaseNumber, long Real, long Alloc)>();
        var extensionLock = new object();
        var parsedCounter = 0;

        var parserCount = Math.Max(1, Environment.ProcessorCount - 1);
        var parsers = new Task[parserCount];
        for (var i = 0; i < parserCount; i++)
        {
            parsers[i] = Task.Run(async () =>
            {
                var localExtensions = new List<(uint, long, long)>();
                var info = new MftRecordInfo();
                await foreach (var block in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    var span = block.Buffer.AsSpan(0, block.RecordCount * recordSize);
                    for (var r = 0; r < block.RecordCount; r++)
                    {
                        var globalIndex = block.StartRecordIndex + r;
                        var recordSpan = span.Slice(r * recordSize, recordSize);
                        if (!MftRecordParser.TryParseRecord(recordSpan, ref info))
                        {
                            continue;
                        }

                        byte flag = 0;
                        if (info.InUse) flag |= FlagInUse;
                        if (info.IsDirectory) flag |= FlagDirectory;
                        if (info.HasName) flag |= FlagHasName;

                        if (info.BaseRecordNumber != 0)
                        {
                            flag |= FlagExtension;
                            if (info.DataSizesValid)
                            {
                                localExtensions.Add(((uint)info.BaseRecordNumber, info.DataRealSize, info.DataAllocatedSize));
                            }
                        }
                        else if (info.DataSizesValid)
                        {
                            flag |= FlagHasSize;
                            realSize[globalIndex] = info.DataRealSize;
                            allocSize[globalIndex] = info.DataAllocatedSize;
                        }

                        parent[globalIndex] = (uint)info.ParentRecordNumber;
                        lastWrite[globalIndex] = info.LastWriteFileTime;
                        if (info.HasName)
                        {
                            names[globalIndex] = Encoding.Unicode.GetString(recordSpan.Slice(info.NameOffsetInRecord, info.NameLengthChars * 2));
                        }

                        flags[globalIndex] = flag;
                    }

                    Interlocked.Add(ref parsedCounter, block.RecordCount);
                    reportParsed(Volatile.Read(ref parsedCounter));
                    ArrayPool<byte>.Shared.Return(block.Buffer);
                }

                if (localExtensions.Count > 0)
                {
                    lock (extensionLock)
                    {
                        extensionSizes.AddRange(localExtensions);
                    }
                }
            }, cancellationToken);
        }

        // Reader: stream extents, emit record-aligned blocks.
        var carry = new byte[recordSize];
        var carryLength = 0;
        var startRecordIndex = 0;
        var parserCompletion = Task.WhenAll(parsers);

        try
        {
            foreach (var (lcn, clusterCount) in extents)
            {
                var clustersRemaining = clusterCount;
                var currentLcn = lcn;
                while (clustersRemaining > 0 && startRecordIndex < recordCount)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (parserCompletion.IsCompleted)
                    {
                        // Parsers died (fault/cancel): stop writing or the bounded channel deadlocks.
                        await parserCompletion.ConfigureAwait(false);
                        throw new InvalidOperationException("MFT parser workers exited early.");
                    }

                    var chunkClusters = (int)Math.Min(clustersRemaining, clustersPerChunk);
                    var chunkBytes = chunkClusters * clusterBytes;
                    var blockBuffer = ArrayPool<byte>.Shared.Rent(carryLength + chunkBytes);

                    if (carryLength > 0)
                    {
                        carry.AsSpan(0, carryLength).CopyTo(blockBuffer);
                    }

                    ReadExact(volume, blockBuffer.AsSpan(carryLength, chunkBytes), currentLcn * clusterBytes);

                    var totalBytes = carryLength + chunkBytes;
                    var fullRecords = totalBytes / recordSize;
                    if (startRecordIndex + fullRecords > recordCount)
                    {
                        fullRecords = recordCount - startRecordIndex;
                    }

                    var consumedBytes = fullRecords * recordSize;
                    carryLength = totalBytes - consumedBytes;
                    if (carryLength > 0)
                    {
                        blockBuffer.AsSpan(consumedBytes, carryLength).CopyTo(carry);
                    }

                    if (fullRecords > 0)
                    {
                        await channel.Writer.WriteAsync(new Block(blockBuffer, startRecordIndex, fullRecords), cancellationToken).ConfigureAwait(false);
                        startRecordIndex += fullRecords;
                    }
                    else
                    {
                        ArrayPool<byte>.Shared.Return(blockBuffer);
                    }

                    clustersRemaining -= chunkClusters;
                    currentLcn += chunkClusters;
                }

                if (startRecordIndex >= recordCount)
                {
                    break;
                }
            }

            channel.Writer.Complete();
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete(ex);
            throw;
        }

        await parserCompletion.ConfigureAwait(false);

        // Merge extension-record sizes into their base records.
        foreach (var (baseNumber, real, alloc) in extensionSizes)
        {
            if (baseNumber >= (uint)recordCount)
            {
                continue;
            }

            if ((flags[baseNumber] & FlagHasSize) == 0)
            {
                realSize[baseNumber] = real;
                allocSize[baseNumber] = alloc;
                flags[baseNumber] |= FlagHasSize;
            }
        }
    }

    private static Task StartReporter(
        IProgress<BiggestFilesProgress>? progress,
        Stopwatch stopwatch,
        Func<int> parsedGetter,
        int recordCount,
        CancellationToken cancellationToken,
        out Action stop)
    {
        if (progress is null)
        {
            stop = static () => { };
            return Task.CompletedTask;
        }

        var stopFlag = new TaskCompletionSource();
        stop = () => stopFlag.TrySetResult();

        return Task.Run(async () =>
        {
            while (!stopFlag.Task.IsCompleted)
            {
                await Task.WhenAny(stopFlag.Task, Task.Delay(ProgressInterval, CancellationToken.None)).ConfigureAwait(false);
                var parsed = parsedGetter();
                progress.Report(new BiggestFilesProgress(
                    "Reading MFT", parsed, 0, stopwatch.Elapsed,
                    ComputeRate(parsed, stopwatch.Elapsed), Array.Empty<FileSizeHit>(), IsCompleted: false));
            }
        }, CancellationToken.None);
    }

    private static uint? ResolveDirectoryRecord(string root, string volumeRoot, uint[] parent, byte[] flags, string?[] names)
    {
        var relative = TrimTrailingSeparators(root)[TrimTrailingSeparators(volumeRoot).Length..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Length == 0)
        {
            return RootRecordNumber;
        }

        var components = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        // Index directories by (parent, lowercase name) for the walk.
        var directoryIndex = new Dictionary<(uint Parent, string Name), uint>();
        for (var index = 0; index < flags.Length; index++)
        {
            if ((flags[index] & FlagDirectory) == 0 || (flags[index] & FlagHasName) == 0 || (flags[index] & FlagExtension) != 0)
            {
                continue;
            }

            var name = names[index];
            if (name is not null)
            {
                directoryIndex[(parent[index], name.ToLowerInvariant())] = (uint)index;
            }
        }

        var current = RootRecordNumber;
        foreach (var component in components)
        {
            if (!directoryIndex.TryGetValue((current, component.ToLowerInvariant()), out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static bool IsUnder(int index, uint target, uint[] parent, byte[] flags, byte[] state)
    {
        // state: 0 unknown, 1 inside, 2 outside, 3 visiting
        var chain = new List<int>();
        var current = index;
        var result = (byte)2;

        for (var depth = 0; depth < MaxAncestryDepth; depth++)
        {
            if (current < 0 || current >= parent.Length)
            {
                result = 2;
                break;
            }

            if (current == target)
            {
                result = 1;
                break;
            }

            var cached = state[current];
            if (cached == 1)
            {
                result = 1;
                break;
            }

            if (cached == 2)
            {
                result = 2;
                break;
            }

            if (cached == 3)
            {
                // Cycle detected.
                result = 2;
                break;
            }

            state[current] = 3;
            chain.Add(current);

            if (current == RootRecordNumber)
            {
                result = 2;
                break;
            }

            current = (int)parent[current];
        }

        foreach (var node in chain)
        {
            state[node] = result;
        }

        return result == 1;
    }

    private static string BuildPath(int index, string volumeRoot, uint[] parent, byte[] flags, string?[] names, Dictionary<uint, string> directoryCache)
    {
        var fileName = names[index] ?? "<unknown>";
        var directory = BuildDirectoryPath((int)parent[index], volumeRoot, parent, flags, names, directoryCache, 0);
        return Path.Combine(directory, fileName);
    }

    private static string BuildDirectoryPath(int index, string volumeRoot, uint[] parent, byte[] flags, string?[] names, Dictionary<uint, string> directoryCache, int depth)
    {
        if (index == RootRecordNumber)
        {
            return volumeRoot;
        }

        if (index < 0 || index >= parent.Length || depth >= MaxAncestryDepth)
        {
            return Path.Combine(volumeRoot, "<orphaned>");
        }

        if (directoryCache.TryGetValue((uint)index, out var cached))
        {
            return cached;
        }

        var name = names[index];
        if (name is null || (flags[index] & FlagInUse) == 0)
        {
            var orphan = Path.Combine(volumeRoot, "<orphaned>");
            directoryCache[(uint)index] = orphan;
            return orphan;
        }

        var parentPath = BuildDirectoryPath((int)parent[index], volumeRoot, parent, flags, names, directoryCache, depth + 1);
        var full = Path.Combine(parentPath, name);
        directoryCache[(uint)index] = full;
        return full;
    }

    private static void ReadExact(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Span<byte> buffer, long fileOffset)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = RandomAccess.Read(handle, buffer[totalRead..], fileOffset + totalRead);
            if (read == 0)
            {
                throw new IOException($"Unexpected end of volume at offset {fileOffset + totalRead}.");
            }

            totalRead += read;
        }
    }

    private static DateTimeOffset? ToModified(long fileTime)
    {
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTime);
        }
        catch
        {
            return null;
        }
    }

    private static double ComputeRate(long items, TimeSpan elapsed)
    {
        return elapsed.TotalSeconds <= 0 ? 0 : items / elapsed.TotalSeconds;
    }

    private static string TrimTrailingSeparators(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private readonly record struct Block(byte[] Buffer, int StartRecordIndex, int RecordCount);
}
