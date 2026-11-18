using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using DevTools.Services.FolderSizes;

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
        string?[] names,
        long[]? recordAllocation = null)
    {
        VolumeRoot = volumeRoot;
        RecordCount = recordCount;
        Parent = parent;
        Flags = flags;
        RealSize = realSize;
        AllocSize = allocSize;
        LastWrite = lastWrite;
        Names = names;
        RecordAllocation = recordAllocation;
    }

    public string VolumeRoot { get; }

    public int RecordCount { get; }

    public uint[] Parent { get; }

    public byte[] Flags { get; }

    public long[] RealSize { get; }

    public long[] AllocSize { get; }

    public long[] LastWrite { get; }

    public string?[] Names { get; }
    public long[]? RecordAllocation { get; }

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
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly SemaphoreSlim ActiveScan = new(1, 1);

    // Keep the reservation through aggregation as the full table remains live until the caller returns.
    internal static async ValueTask<IDisposable> EnterScanAsync(CancellationToken cancellationToken)
    {
        await ActiveScan.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ScanLease();
    }

    private sealed class ScanLease : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                ActiveScan.Release();
            }
        }
    }

    public static async Task<MftRecordTable> LoadAsync(
        string scanRoot,
        Action<long, long, TimeSpan>? reportProgress,
        System.Diagnostics.Stopwatch stopwatch,
        CancellationToken cancellationToken,
        bool keepFileNames = true)
    {
        var root = Path.GetFullPath(scanRoot);
        var pathRoot = Path.GetPathRoot(root);
        var devicePath = WindowsVolumePaths.GetDevicePath(pathRoot);
        if (devicePath is null)
        {
            throw new NotSupportedException($"MFT scan requires a local volume path, got '{root}'.");
        }

        var volumeRoot = FolderTree.NormalizeRoot(root);
        using var volume = NativeMethods.OpenVolume(devicePath);
        var geometry = NativeMethods.GetVolumeData(volume);
        var recordCount = GetRecordCount(geometry);
        var recordSize = (int)geometry.BytesPerFileRecordSegment;
        var (chunkBytes, parserCount, queueCapacity) = ScanResources.Capture().MftPlan(recordCount, keepFileNames);

        // Check the full MFT mapping before allocating its in-memory columns.
        var extents = ReadMftExtents(volume, geometry);
        ValidateMftExtents(geometry, extents);

        // Structure-of-arrays table, indexed by MFT record number.
        var parent = new uint[recordCount];
        var flags = new byte[recordCount];
        var realSize = new long[recordCount];
        var allocSize = keepFileNames ? new long[recordCount] : [];
        var lastWrite = keepFileNames ? new long[recordCount] : [];
        var recordAllocation = keepFileNames ? null : new long[recordCount];
        var sequences = recordAllocation is null ? null : new ushort[recordCount];
        var names = new string?[recordCount];


        var lastReportTicks = Stopwatch.GetTimestamp();
        void OnParsed(int parsed)
        {
            if (reportProgress is null)
            {
                return;
            }

            var previous = Volatile.Read(ref lastReportTicks);
            var now = Stopwatch.GetTimestamp();
            if (Stopwatch.GetElapsedTime(previous, now) < ProgressInterval ||
                Interlocked.CompareExchange(ref lastReportTicks, now, previous) != previous)
            {
                return;
            }

            reportProgress(parsed, recordCount, stopwatch.Elapsed);
        }

        await ParseAllRecordsAsync(
            volume, geometry, extents, recordSize, recordCount, chunkBytes, parserCount, queueCapacity,
            parent, flags, realSize, allocSize, lastWrite, names, keepFileNames, recordAllocation, sequences,
            OnParsed,
            cancellationToken).ConfigureAwait(false);

        reportProgress?.Invoke(recordCount, recordCount, stopwatch.Elapsed);

        return new MftRecordTable(volumeRoot, recordCount, parent, flags, realSize, allocSize, lastWrite, names, recordAllocation);
    }

    internal static int GetRecordCount(NtfsVolumeData geometry)
    {
        var clusterBytes = geometry.BytesPerCluster;
        var recordBytes = geometry.BytesPerFileRecordSegment;
        if (clusterBytes is 0 or > int.MaxValue || recordBytes is 0 or > int.MaxValue ||
            geometry.TotalClusters <= 0 || geometry.TotalClusters > long.MaxValue / clusterBytes ||
            geometry.MftStartLcn < 0 || geometry.MftStartLcn >= geometry.TotalClusters ||
            geometry.MftValidDataLength <= 0 ||
            geometry.MftValidDataLength > geometry.TotalClusters * clusterBytes ||
            geometry.MftValidDataLength % recordBytes != 0)
        {
            throw new NotSupportedException("Invalid NTFS geometry reported by the volume.");
        }

        var requiredClustersForFirstRecord = ((long)recordBytes - 1) / clusterBytes + 1;
        if (requiredClustersForFirstRecord > geometry.TotalClusters - geometry.MftStartLcn ||
            requiredClustersForFirstRecord * clusterBytes > int.MaxValue)
        {
            throw new NotSupportedException("Record zero extends beyond the readable volume range.");
        }

        var count = geometry.MftValidDataLength / recordBytes;
        if (count <= MftRecordTable.RootRecordNumber || count > int.MaxValue)
        {
            throw new NotSupportedException($"Unsupported MFT record count: {count}.");
        }

        return (int)count;
    }

    internal static void ValidateMftExtents(NtfsVolumeData geometry, List<(long Lcn, long ClusterCount)> extents)
    {
        if (extents.Count == 0 || extents[0].Lcn != geometry.MftStartLcn)
        {
            throw new IOException("The NTFS master file table has an invalid starting extent.");
        }

        var covered = 0L;
        foreach (var (lcn, clusterCount) in extents)
        {
            if (lcn < 0 || lcn >= geometry.TotalClusters || clusterCount <= 0 ||
                clusterCount > geometry.TotalClusters - lcn || clusterCount > geometry.TotalClusters - covered)
            {
                throw new IOException("An NTFS master file table extent lies outside the volume.");
            }

            covered += clusterCount;
        }

        var minimumClusters = (geometry.MftValidDataLength - 1) / geometry.BytesPerCluster + 1;
        if (covered < minimumClusters)
        {
            throw new IOException("The NTFS extent map does not cover the valid master file table data.");
        }

        // Keep the original virtual cluster order for the reader. Sort a copy to detect repeated physical clusters.
        if (extents.Count > 1)
        {
            var physicalOrder = extents.ToArray();
            Array.Sort(physicalOrder, static (left, right) => left.Lcn.CompareTo(right.Lcn));
            for (var i = 1; i < physicalOrder.Length; i++)
            {
                var previous = physicalOrder[i - 1];
                if (physicalOrder[i].Lcn < previous.Lcn + previous.ClusterCount)
                {
                    throw new IOException("The NTFS master file table extents overlap.");
                }
            }
        }
    }

    private static List<(long Lcn, long ClusterCount)> ReadMftExtents(Microsoft.Win32.SafeHandles.SafeFileHandle volume, NtfsVolumeData geometry)
    {
        var clusterBytes = (int)geometry.BytesPerCluster;
        var recordSize = (int)geometry.BytesPerFileRecordSegment;
        // Read enough whole clusters to cover record 0 (handles cluster sizes smaller than the record).
        var bytesToRead = ((recordSize - 1) / clusterBytes + 1) * clusterBytes;
        var buffer = ArrayPool<byte>.Shared.Rent(bytesToRead);
        try
        {
            ReadExact(volume, buffer.AsSpan(0, bytesToRead), geometry.MftStartLcn * geometry.BytesPerCluster);
            var extents = new List<(long Lcn, long ClusterCount)>();
            if (!MftRecordParser.TryReadUnnamedDataRuns(buffer.AsSpan(0, recordSize), extents))
            {
                throw new IOException("The NTFS master file table has an invalid extent map.");
            }

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
        int targetChunkBytes,
        int parserCount,
        int queueCapacity,
        uint[] parent,
        byte[] flags,
        long[] realSize,
        long[] allocSize,
        long[] lastWrite,
        string?[] names,
        bool keepFileNames,
        long[]? recordAllocation,
        ushort[]? sequences,
        Action<int> reportThrottled,
        CancellationToken cancellationToken)
    {
        var clusterBytes = (int)geometry.BytesPerCluster;
        var clustersPerChunk = Math.Max(1, targetChunkBytes / clusterBytes);

        var channel = Channel.CreateBounded<Block>(new BoundedChannelOptions(queueCapacity)
        {
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        // Extension records whose LowestVCN==0 $DATA carries the real size for a fragmented base file.
        var extensionSizes = new List<ExtensionAllocation>();
        var extensionLock = new object();
        var parsedCounter = 0;


        var parsers = new Task[parserCount];
        for (var i = 0; i < parserCount; i++)
        {
            parsers[i] = Task.Run(async () =>
            {
                var localExtensions = new List<ExtensionAllocation>();
                var info = new MftRecordInfo();
                try
                {
                    await foreach (var block in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        try
                        {
                            var span = block.Buffer.AsSpan(0, block.RecordCount * recordSize);
                            for (var r = 0; r < block.RecordCount; r++)
                            {
                                var globalIndex = block.StartRecordIndex + r;
                                var recordSpan = span.Slice(r * recordSize, recordSize);
                                if (!MftRecordParser.TryParseRecord(recordSpan, ref info,
                                    requireInUse: true, readLastWrite: keepFileNames, countAllocation: recordAllocation is not null,
                                    volumeClusters: geometry.TotalClusters))
                                {
                                    continue;
                                }

                                byte flag = MftRecordTable.FlagInUse;
                                if (info.IsDirectory) flag |= MftRecordTable.FlagDirectory;
                                if (info.HasName) flag |= MftRecordTable.FlagHasName;

                                var allocated = recordAllocation is null
                                    ? 0
                                    : checked(info.AllocatedClusters * geometry.BytesPerCluster);
                                if (info.IsExtension)
                                {
                                    flag |= MftRecordTable.FlagExtension;
                                    if (info.BaseRecordNumber < (ulong)recordCount && (info.DataSizesValid || allocated > 0))
                                    {
                                        localExtensions.Add(new ExtensionAllocation(
                                            (int)info.BaseRecordNumber, info.BaseRecordSequence,
                                            info.DataSizesValid, info.DataRealSize, info.DataAllocatedSize, allocated));
                                    }
                                }
                                else
                                {
                                    if (recordAllocation is not null && sequences is not null)
                                    {
                                        recordAllocation[globalIndex] = allocated;
                                        sequences[globalIndex] = info.SequenceNumber;
                                    }

                                    if (info.DataSizesValid)
                                    {
                                        flag |= MftRecordTable.FlagHasSize;
                                        realSize[globalIndex] = info.DataRealSize;
                                        if (keepFileNames)
                                        {
                                            allocSize[globalIndex] = info.DataAllocatedSize;
                                        }
                                    }
                                }

                                parent[globalIndex] = (uint)info.ParentRecordNumber;
                                if (keepFileNames)
                                {
                                    lastWrite[globalIndex] = info.LastWriteFileTime;
                                }
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
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(block.Buffer);
                        }
                    }
                }
                catch (Exception ex)
                {
                    channel.Writer.TryComplete(ex);
                    throw;
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
                    var queued = false;
                    try
                    {
                        if (carryLength > 0)
                        {
                            carry.AsSpan(0, carryLength).CopyTo(blockBuffer);
                        }

                        ReadExact(volume, blockBuffer.AsSpan(carryLength, chunkBytes), currentLcn * clusterBytes);

                        var totalBytes = carryLength + chunkBytes;
                        var recordsRemaining = recordCount - startRecordIndex;
                        var fullRecords = Math.Min(totalBytes / recordSize, recordsRemaining);
                        var consumedBytes = fullRecords * recordSize;
                        // The last extent can include allocated space beyond the valid data length.
                        carryLength = fullRecords == recordsRemaining ? 0 : totalBytes - consumedBytes;
                        if (carryLength > 0)
                        {
                            blockBuffer.AsSpan(consumedBytes, carryLength).CopyTo(carry);
                        }

                        if (fullRecords > 0)
                        {
                            await channel.Writer.WriteAsync(new Block(blockBuffer, startRecordIndex, fullRecords), cancellationToken).ConfigureAwait(false);
                            queued = true;
                            startRecordIndex += fullRecords;
                        }
                    }
                    finally
                    {
                        if (!queued)
                        {
                            ArrayPool<byte>.Shared.Return(blockBuffer);
                        }
                    }

                    clustersRemaining -= chunkClusters;
                    currentLcn += chunkClusters;
                }

                if (startRecordIndex >= recordCount)
                {
                    break;
                }
            }

            if (startRecordIndex != recordCount)
            {
                throw new IOException($"MFT extents covered {startRecordIndex:N0} of {recordCount:N0} records.");
            }

            channel.Writer.TryComplete();
            await parserCompletion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            channel.Writer.TryComplete(ex);
            try
            {
                await parserCompletion.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Preserve the read or parse error that stopped the pipeline.
            }

            throw;
        }
        finally
        {
            while (channel.Reader.TryRead(out var pending))
            {
                ArrayPool<byte>.Shared.Return(pending.Buffer);
            }
        }

        // Merge extension-record sizes into their base records.
        foreach (var extension in extensionSizes)
        {
            var baseNumber = extension.BaseNumber;
            if ((flags[baseNumber] & (MftRecordTable.FlagInUse | MftRecordTable.FlagExtension)) != MftRecordTable.FlagInUse ||
                (sequences is not null && sequences[baseNumber] != extension.BaseSequence))
            {
                continue;
            }

            if (recordAllocation is not null)
            {
                recordAllocation[baseNumber] = checked(recordAllocation[baseNumber] + extension.RecordBytes);
            }

            if (extension.HasLogicalSize && (flags[baseNumber] & MftRecordTable.FlagHasSize) == 0)
            {
                realSize[baseNumber] = extension.LogicalBytes;
                if (keepFileNames)
                {
                    allocSize[baseNumber] = extension.UnnamedDataAllocation;
                }

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

    private readonly record struct ExtensionAllocation(
        int BaseNumber, ushort BaseSequence, bool HasLogicalSize, long LogicalBytes, long UnnamedDataAllocation, long RecordBytes);

    private readonly record struct Block(byte[] Buffer, int StartRecordIndex, int RecordCount);
}
