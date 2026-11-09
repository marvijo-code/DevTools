using System.Buffers;
using System.Text;
using System.Threading.Channels;

namespace DevTools.Services.BiggestFiles.Ntfs;

/// <summary>
/// In-memory snapshot of the NTFS Master File Table as parallel arrays indexed by MFT record
/// number. Built once by <see cref="MftTableLoader"/> and shared by the biggest-files and
/// folder-size scans so both read the same record data.
/// </summary>
internal sealed class MftRecordTable
{
    // Per-record flag bits.
    public const byte FlagInUse = 0x01;
    public const byte FlagDirectory = 0x02;
    public const byte FlagHasName = 0x04;
    public const byte FlagExtension = 0x08;
    public const byte FlagHasSize = 0x10;

    public const uint RootRecordNumber = 5;
    public const uint FirstUserRecordNumber = 24;
    public const int MaxAncestryDepth = 1024;

    public MftRecordTable(
        string volumeRoot,
        int recordCount,
        uint[] parent,
        byte[] flags,
        long[] realSize,
        long[] allocSize,
        long[] lastWrite,
        string?[] names)
    {
        VolumeRoot = volumeRoot;
        RecordCount = recordCount;
        Parent = parent;
        Flags = flags;
        RealSize = realSize;
        AllocSize = allocSize;
        LastWrite = lastWrite;
        Names = names;
    }

    public string VolumeRoot { get; }

    public int RecordCount { get; }

    public uint[] Parent { get; }

    public byte[] Flags { get; }

    public long[] RealSize { get; }

    public long[] AllocSize { get; }

    public long[] LastWrite { get; }

    public string?[] Names { get; }

    /// <summary>True when the record is a base (non-extension) directory that carries a name.</summary>
    public bool IsNamedDirectory(int index)
    {
        var flag = Flags[index];
        return (flag & FlagInUse) != 0 &&
               (flag & FlagHasName) != 0 &&
               (flag & FlagDirectory) != 0 &&
               (flag & FlagExtension) == 0;
    }

    /// <summary>True when the record is a base (non-extension) file with a name.</summary>
    public bool IsNamedFile(int index)
    {
        var flag = Flags[index];
        return (flag & FlagInUse) != 0 &&
               (flag & FlagHasName) != 0 &&
               (flag & FlagDirectory) == 0 &&
               (flag & FlagExtension) == 0;
    }

    /// <summary>Resolves an absolute directory path to its MFT record number, or null when not found.</summary>
    public uint? ResolveDirectoryRecord(string path)
    {
        var relative = TrimTrailingSeparators(path)[TrimTrailingSeparators(VolumeRoot).Length..]
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Length == 0)
        {
            return RootRecordNumber;
        }

        var components = relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        // Index directories by (parent, lowercase name) for the walk.
        var directoryIndex = new Dictionary<(uint Parent, string Name), uint>();
        for (var index = 0; index < Flags.Length; index++)
        {
            if (!IsNamedDirectory(index))
            {
                continue;
            }

            var name = Names[index];
            if (name is not null)
            {
                directoryIndex[(Parent[index], name.ToLowerInvariant())] = (uint)index;
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

    /// <summary>True when <paramref name="index"/> sits anywhere under <paramref name="target"/>.</summary>
    public bool IsUnder(int index, uint target, byte[] state)
    {
        // state: 0 unknown, 1 inside, 2 outside, 3 visiting
        var chain = new List<int>();
        var current = index;
        var result = (byte)2;

        for (var depth = 0; depth < MaxAncestryDepth; depth++)
        {
            if (current < 0 || current >= Parent.Length)
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

            current = (int)Parent[current];
        }

        foreach (var node in chain)
        {
            state[node] = result;
        }

        return result == 1;
    }

    public string BuildPath(int index, Dictionary<uint, string> directoryCache)
    {
        var fileName = Names[index] ?? "<unknown>";
        var directory = BuildDirectoryPath((int)Parent[index], directoryCache, 0);
        return Path.Combine(directory, fileName);
    }

    public string BuildDirectoryPath(int index, Dictionary<uint, string> directoryCache, int depth)
    {
        if (index == RootRecordNumber)
        {
            return VolumeRoot;
        }

        if (index < 0 || index >= Parent.Length || depth >= MaxAncestryDepth)
        {
            return Path.Combine(VolumeRoot, "<orphaned>");
        }

        if (directoryCache.TryGetValue((uint)index, out var cached))
        {
            return cached;
        }

        var name = Names[index];
        if (name is null || (Flags[index] & FlagInUse) == 0)
        {
            var orphan = Path.Combine(VolumeRoot, "<orphaned>");
            directoryCache[(uint)index] = orphan;
            return orphan;
        }

        var parentPath = BuildDirectoryPath((int)Parent[index], directoryCache, depth + 1);
        var full = Path.Combine(parentPath, name);
        directoryCache[(uint)index] = full;
        return full;
    }

    private static string TrimTrailingSeparators(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

/// <summary>
/// Reads the Master File Table from a raw NTFS volume handle and materializes an
/// <see cref="MftRecordTable"/>. Requires elevation; throws before allocating when the volume is
/// not a supported NTFS drive-letter volume.
/// </summary>
internal static class MftTableLoader
{
    private const int TargetChunkBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    public static async Task<MftRecordTable> LoadAsync(
        string scanRoot,
        Action<long, long, TimeSpan>? reportProgress,
        System.Diagnostics.Stopwatch stopwatch,
        CancellationToken cancellationToken,
        bool keepFileNames = true)
    {
        var root = Path.GetFullPath(scanRoot);
        var pathRoot = Path.GetPathRoot(root);
        if (string.IsNullOrWhiteSpace(pathRoot) || pathRoot.Length < 2 || pathRoot[1] != ':')
        {
            throw new NotSupportedException($"MFT scan requires a drive-letter path, got '{root}'.");
        }

        var driveLetter = char.ToUpperInvariant(pathRoot[0]);
        var volumeRoot = $@"{driveLetter}:\";

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

        var lastReportTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        void OnParsed(int parsed)
        {
            if (reportProgress is null)
            {
                return;
            }

            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - lastReportTicks < ProgressInterval.Ticks)
            {
                return;
            }

            lastReportTicks = now;
            reportProgress(parsed, recordCount, stopwatch.Elapsed);
        }

        await ParseAllRecordsAsync(
            volume, geometry, extents, recordSize, recordCount,
            parent, flags, realSize, allocSize, lastWrite, names, keepFileNames,
            count => Volatile.Write(ref recordsParsed, count),
            OnParsed,
            cancellationToken).ConfigureAwait(false);

        reportProgress?.Invoke(recordsParsed, recordCount, stopwatch.Elapsed);

        return new MftRecordTable(volumeRoot, recordCount, parent, flags, realSize, allocSize, lastWrite, names);
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
        bool keepFileNames,
        Action<int> reportParsed,
        Action<int> reportThrottled,
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

        // Half the cores at most: the read is mostly disk bound, and the laptop stays usable.
        var parserCount = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
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
                        if (info.InUse) flag |= MftRecordTable.FlagInUse;
                        if (info.IsDirectory) flag |= MftRecordTable.FlagDirectory;
                        if (info.HasName) flag |= MftRecordTable.FlagHasName;

                        if (info.BaseRecordNumber != 0)
                        {
                            flag |= MftRecordTable.FlagExtension;
                            if (info.DataSizesValid)
                            {
                                localExtensions.Add(((uint)info.BaseRecordNumber, info.DataRealSize, info.DataAllocatedSize));
                            }
                        }
                        else if (info.DataSizesValid)
                        {
                            flag |= MftRecordTable.FlagHasSize;
                            realSize[globalIndex] = info.DataRealSize;
                            allocSize[globalIndex] = info.DataAllocatedSize;
                        }

                        parent[globalIndex] = (uint)info.ParentRecordNumber;
                        lastWrite[globalIndex] = info.LastWriteFileTime;
                        // File names cost hundreds of MB on a big drive; the folder-size scan only
                        // needs folder names, so it skips them to stay light on a RAM-starved laptop.
                        if (info.HasName && (keepFileNames || info.IsDirectory))
                        {
                            names[globalIndex] = Encoding.Unicode.GetString(recordSpan.Slice(info.NameOffsetInRecord, info.NameLengthChars * 2));
                        }

                        flags[globalIndex] = flag;
                    }

                    Interlocked.Add(ref parsedCounter, block.RecordCount);
                    reportThrottled(Volatile.Read(ref parsedCounter));
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
                    DevTools.Services.FolderSizes.ScanGate.Wait(cancellationToken);
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

            if ((flags[baseNumber] & MftRecordTable.FlagHasSize) == 0)
            {
                realSize[baseNumber] = real;
                allocSize[baseNumber] = alloc;
                flags[baseNumber] |= MftRecordTable.FlagHasSize;
            }
        }
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

    private readonly record struct Block(byte[] Buffer, int StartRecordIndex, int RecordCount);
}
