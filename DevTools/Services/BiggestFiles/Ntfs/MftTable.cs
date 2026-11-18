using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using DevTools.Services.FolderSizes;

namespace DevTools.Services.BiggestFiles.Ntfs;

internal readonly record struct MftRankedFileDetails(string Name, long AllocatedBytes, long LastWriteFileTime);

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
    public const byte FlagIncompleteAllocation = 0x20;

    public const uint RootRecordNumber = 5;
    public const uint InvalidParentRecord = uint.MaxValue;
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
        MftRecordAllocations? recordAllocation = null,
        NtfsBitmapAllocation? bitmapAllocation = null,
        Func<MftRecordTable, int[], CancellationToken, Dictionary<int, MftRankedFileDetails>>? loadFileDetails = null)
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
        BitmapAllocation = bitmapAllocation;
        _loadFileDetails = loadFileDetails;
    }

    public string VolumeRoot { get; }

    public int RecordCount { get; }

    public uint[] Parent { get; }

    public byte[] Flags { get; }

    public long[] RealSize { get; }

    public long[] AllocSize { get; }

    public long[] LastWrite { get; }

    public string?[] Names { get; }
    public MftRecordAllocations? RecordAllocation { get; }
    public NtfsBitmapAllocation? BitmapAllocation { get; }
    private readonly Func<MftRecordTable, int[], CancellationToken, Dictionary<int, MftRankedFileDetails>>? _loadFileDetails;

    public Dictionary<int, MftRankedFileDetails>? LoadRankedFileDetails(IEnumerable<int> records, CancellationToken cancellationToken) =>
        _loadFileDetails?.Invoke(this, records.Distinct().Order().ToArray(), cancellationToken);

    /// <summary>True when the record is a base (non-extension) directory that carries a name.</summary>
    public bool IsNamedDirectory(int index)
    {
        const byte namedDirectory = FlagInUse | FlagHasName | FlagDirectory;
        return (Flags[index] & (namedDirectory | FlagExtension)) == namedDirectory;
    }

    /// <summary>True when the record is a base (non-extension) file with a name.</summary>
    public bool IsNamedFile(int index)
    {
        const byte namedFile = FlagInUse | FlagHasName;
        return (Flags[index] & (namedFile | FlagDirectory | FlagExtension)) == namedFile &&
               Parent[index] != InvalidParentRecord;
    }

    /// <summary>Resolves an absolute directory path to its MFT record number, or null when not found.</summary>
    public uint? ResolveDirectoryRecord(string path)
    {
        var start = FolderTree.GetRelativeStart(VolumeRoot, path);
        if (start < 0)
        {
            return null;
        }

        var components = path[start..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
        {
            return RootRecordNumber;
        }

        // A single path needs only the records whose names occur in one of its components.
        var componentNames = new HashSet<string>(components, StringComparer.OrdinalIgnoreCase);
        var childrenByParent = new Dictionary<uint, List<int>>();
        for (var index = 0; index < Flags.Length; index++)
        {
            if (!IsNamedDirectory(index) || Names[index] is not { } name || !componentNames.Contains(name))
            {
                continue;
            }

            var parent = Parent[index];
            if (!childrenByParent.TryGetValue(parent, out var children))
            {
                children = [];
                childrenByParent.Add(parent, children);
            }

            children.Add(index);
        }

        var current = RootRecordNumber;
        foreach (var component in components)
        {
            if (!childrenByParent.TryGetValue(current, out var children))
            {
                return null;
            }

            var next = FolderTree.FindChildByName(CollectionsMarshal.AsSpan(children), Names, component);
            if (next < 0)
            {
                return null;
            }

            current = (uint)next;
        }

        return current;
    }

    /// <summary>True when <paramref name="index"/> sits anywhere under <paramref name="target"/>.</summary>
    public bool IsUnder(int index, uint target, byte[] state)
    {
        // state: 0 unknown, 1 inside, 2 outside, 3 visiting
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

            if (current == RootRecordNumber)
            {
                result = 2;
                break;
            }

            current = (int)Parent[current];
        }

        // Follow the marked links again to cache the answer without allocating a path per file.
        current = index;
        while ((uint)current < (uint)Parent.Length && state[current] == 3)
        {
            state[current] = result;
            current = (int)Parent[current];
        }

        return result == 1;
    }

    public string BuildPath(int index, Dictionary<uint, string> directoryCache, string? fileName = null)
    {
        fileName ??= Names[index] ?? "<unknown>";
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


}

internal sealed class MftRecordAllocations
{
    private readonly uint _bytesPerCluster;
    private readonly uint[]? _compact;
    private readonly long[]? _wide;
    private Dictionary<int, long>? _overflow;

    public MftRecordAllocations(int recordCount, uint bytesPerCluster, long volumeClusters)
    {
        _bytesPerCluster = bytesPerCluster;
        if (volumeClusters <= uint.MaxValue)
        {
            _compact = new uint[recordCount];
        }
        else
        {
            _wide = new long[recordCount];
        }
    }

    public long this[int record] => ToBytes(GetClusters(record));

    internal long ToBytes(long clusters) => checked(clusters * _bytesPerCluster);

    // A parsed record cannot reference more clusters than the whole volume.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetParsedClusters(int record, long clusters)
    {
        if (_compact is not null)
        {
            _compact[record] = (uint)clusters;
        }
        else
        {
            _wide![record] = clusters;
        }
    }

    internal void Merge(int source, int target)
    {
        var sourceClusters = GetClusters(source);
        if (sourceClusters == 0)
        {
            return;
        }

        var clusters = checked(GetClusters(target) + sourceClusters);
        _ = ToBytes(clusters);

        if (_compact is not null)
        {
            // A base record and its extensions may reference overlapping clusters.
            if (clusters > uint.MaxValue)
            {
                _overflow ??= new();
                _overflow[target] = clusters;
                _compact[target] = uint.MaxValue;
            }
            else
            {
                _compact[target] = (uint)clusters;
            }

            _compact[source] = 0;
            _overflow?.Remove(source);
        }
        else
        {
            _wide![target] = clusters;
            _wide[source] = 0;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal long GetClusters(int record)
    {
        if (_compact is null)
        {
            return _wide![record];
        }

        var value = _compact[record];
        return value == uint.MaxValue ? GetOverflowClusters(record) : value;
    }

    private long GetOverflowClusters(int record) =>
        _overflow?.TryGetValue(record, out var larger) == true ? larger : uint.MaxValue;
}

/// <summary>
/// Reads the Master File Table from a raw NTFS volume handle and materializes an
/// <see cref="MftRecordTable"/>. Requires elevation; throws before allocating when the volume is
/// not a supported NTFS drive-letter volume.
/// </summary>
internal static class MftTableLoader
{
    private const int MaximumNameBatchBytes = 64 * 1024;
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

    public static Task<MftRecordTable> LoadAsync(
        string scanRoot,
        Action<long, long, TimeSpan>? reportProgress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken,
        bool keepFileNames = true) =>
        UseVolumeAsync(scanRoot, reportProgress, stopwatch, cancellationToken, keepFileNames,
            requestedLargestFiles: 0, static table => table);

    internal static async Task<TResult> UseVolumeAsync<TResult>(
        string scanRoot,
        Action<long, long, TimeSpan>? reportProgress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken,
        bool keepFileNames,
        int requestedLargestFiles,
        Func<MftRecordTable, TResult> consume)
    {
        var root = Path.GetFullPath(scanRoot);
        var pathRoot = Path.GetPathRoot(root);
        var devicePath = WindowsVolumePaths.GetDevicePath(pathRoot);
        if (devicePath is null)
        {
            throw new NotSupportedException($"MFT scan requires a local volume path, got '{root}'.");
        }

        var volumeRoot = FolderTree.NormalizeRoot(root);
        await ScanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var volume = NativeMethods.OpenVolume(devicePath);
        var geometry = NativeMethods.GetVolumeData(volume);
        var deferFileNames = keepFileNames && requestedLargestFiles > 0 &&
            ScanResources.CanDeferLargestFileNames(GetRecordCount(geometry), requestedLargestFiles) &&
            ScanResources.ReadSeekPenalty(volumeRoot) is false;
        var table = await LoadVolumeAsync(volume, volumeRoot, geometry, reportProgress, stopwatch, cancellationToken, keepFileNames,
            keepFileNames ? null : (start, output) => NativeMethods.ReadVolumeBitmap(volume, start, output), deferFileNames)
            .ConfigureAwait(false);
        return consume(table);
    }

    internal static async Task<MftRecordTable> LoadVolumeAsync(
        Microsoft.Win32.SafeHandles.SafeFileHandle volume,
        string volumeRoot,
        NtfsVolumeData geometry,
        Action<long, long, TimeSpan>? reportProgress,
        Stopwatch stopwatch,
        CancellationToken cancellationToken,
        bool keepFileNames,
        NtfsVolumeBitmap.Query? bitmapQuery = null,
        bool deferFileNames = false)
    {
        var recordCount = GetRecordCount(geometry);
        var recordSize = (int)geometry.BytesPerFileRecordSegment;
        var clusterBytes = (int)geometry.BytesPerCluster;
        var resources = ScanResources.Capture();
        var (chunkBytes, parserCount, queueCapacity, carryBytes) = resources.MftPlan(
            recordCount, geometry.MftValidDataLength, clusterBytes, recordSize, keepFileNames, deferFileNames);

        // Check the full MFT mapping before allocating its in-memory columns.
        await ScanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var extents = MftExtentMapReader.Read(geometry, (offset, buffer) =>
        {
            ScanGate.Wait(cancellationToken);
            ReadExact(volume, buffer, offset);
        }, cancellationToken);
        ValidateMftExtents(geometry, extents);

        parserCount = LimitParserCount(geometry, extents, chunkBytes / clusterBytes, parserCount);

        // Structure-of-arrays table, indexed by MFT record number.
        var parent = new uint[recordCount];
        var flags = new byte[recordCount];
        var realSize = new long[recordCount];
        var eagerFileDetails = keepFileNames && !deferFileNames;
        var allocSize = eagerFileDetails ? new long[recordCount] : [];
        var lastWrite = eagerFileDetails ? new long[recordCount] : [];
        var extensionFileAllocations = keepFileNames && deferFileNames ? new Dictionary<int, long>() : null;
        var recordAllocation = keepFileNames ? null : new MftRecordAllocations(recordCount, geometry.BytesPerCluster, geometry.TotalClusters);
        var observedBitmap = bitmapQuery is not null && recordAllocation is not null
            ? NtfsVolumeBitmap.TryCreate(geometry.TotalClusters, resources.MemoryHeadroom, bitmapQuery, cancellationToken)
            : null;
        // The record sequence and its parent reference sequence share one temporary column.
        var referenceSequences = new uint[recordCount];
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

        var referencedClusters = await ParseAllRecordsAsync(
            volume, geometry, extents, recordSize, recordCount, chunkBytes, parserCount, queueCapacity, carryBytes,
            parent, flags, realSize, allocSize, lastWrite, names, keepFileNames, deferFileNames, extensionFileAllocations,
            recordAllocation, referenceSequences,
            observedBitmap, OnParsed,
            cancellationToken).ConfigureAwait(false);

        var bitmapAllocation = bitmapQuery is not null
            ? observedBitmap?.Reconcile(geometry.BytesPerCluster, bitmapQuery, cancellationToken)
            : null;
        if (bitmapAllocation is { } bitmap)
        {
            var referencedBytes = referencedClusters * geometry.BytesPerCluster;
            var uniqueBytes = (UInt128)bitmap.MatchedBytes + (UInt128)bitmap.RecordedFreeBytes;
            bitmapAllocation = referencedBytes >= uniqueBytes
                ? bitmap with { RepeatedReferenceBytes = referencedBytes - uniqueBytes }
                : null;
        }

        reportProgress?.Invoke(recordCount, recordCount, stopwatch.Elapsed);

        Func<MftRecordTable, int[], CancellationToken, Dictionary<int, MftRankedFileDetails>>? loadFileDetails =
            extensionFileAllocations is not null
                ? (table, records, token) => LoadDeferredFileDetails(table, records, extents, recordSize, clusterBytes,
                    referenceSequences, extensionFileAllocations, (offset, destination) => ReadExact(volume, destination, offset), token)
                : null;
        return new MftRecordTable(volumeRoot, recordCount, parent, flags, realSize, allocSize, lastWrite, names,
            recordAllocation, bitmapAllocation, loadFileDetails);
    }

    internal static Dictionary<int, MftRankedFileDetails> LoadDeferredFileDetails(MftRecordTable table, int[] records,
        List<(long Lcn, long ClusterCount)> extents, int recordSize, int clusterBytes, uint[] referenceSequences,
        IReadOnlyDictionary<int, long> extensionFileAllocations, MftExtentMapReader.ReadAt readAt, CancellationToken cancellationToken)
    {
        var details = new Dictionary<int, MftRankedFileDetails>(records.Length);
        if (records.Length == 0)
        {
            return details;
        }

        long ClusterStart(long offset) => offset / clusterBytes * clusterBytes;
        long ClusterEnd(long offset) => ((offset - 1) / clusterBytes + 1) * clusterBytes;

        var scratch = ArrayPool<byte>.Shared.Rent(clusterBytes);
        byte[]? buffer = null;
        try
        {
            for (var first = 0; first < records.Length;)
            {
                ScanGate.Wait(cancellationToken);
                var firstRecordOffset = (long)records[first] * recordSize;
                var batchStart = ClusterStart(firstRecordOffset);
                var batchEnd = ClusterEnd(firstRecordOffset + recordSize);
                var next = first + 1;
                // Include adjacent clusters, with no read between clusters that contain no selected record.
                while (next < records.Length)
                {
                    var recordOffset = (long)records[next] * recordSize;
                    var recordEnd = ClusterEnd(recordOffset + recordSize);
                    if (ClusterStart(recordOffset) > batchEnd ||
                        (recordEnd > batchEnd && recordEnd - batchStart > MaximumNameBatchBytes))
                    {
                        break;
                    }

                    batchEnd = recordEnd;
                    next++;
                }

                var length = checked((int)(batchEnd - batchStart));
                if (buffer is null || buffer.Length < length)
                {
                    var expanded = ArrayPool<byte>.Shared.Rent(length);
                    if (buffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    buffer = expanded;
                }

                var batch = buffer.AsSpan(0, length);
                MftExtentMapReader.ReadMapped(extents, batchStart, batch, scratch.AsSpan(0, clusterBytes), readAt, cancellationToken);
                for (; first < next; first++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var index = records[first];
                    var record = batch.Slice((int)((long)index * recordSize - batchStart), recordSize);
                    var info = new MftRecordInfo();
                    var extensionName = table.Names[index];
                    var sizeFromExtension = extensionFileAllocations.TryGetValue(index, out var extensionBytes);
                    var hadBaseSize = !sizeFromExtension && (table.Flags[index] & MftRecordTable.FlagHasSize) != 0;
                    if (!MftRecordParser.TryParseRecord(record, ref info, requireInUse: true, readLastWrite: true) ||
                        info.IsDirectory || info.IsExtension ||
                        (extensionName is null && (!info.HasName || info.ParentRecordNumber != table.Parent[index])) ||
                        (((uint)info.SequenceNumber << 16) | info.ParentRecordSequence) != referenceSequences[index] ||
                        info.DataSizesValid != hadBaseSize || (hadBaseSize && info.DataRealSize != table.RealSize[index]))
                    {
                        throw new IOException($"NTFS record {index} changed while validating its file details.");
                    }

                    var name = extensionName ?? Encoding.Unicode.GetString(record.Slice(info.NameOffsetInRecord, info.NameLengthChars * 2));
                    var allocated = sizeFromExtension ? extensionBytes : info.DataAllocatedSize;
                    details.Add(index, new MftRankedFileDetails(name, allocated, info.LastWriteFileTime));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return details;
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

    internal static int LimitParserCount(NtfsVolumeData geometry, List<(long Lcn, long ClusterCount)> extents,
        int clustersPerChunk, int parserLimit)
    {
        // Count only batches that intersect initialized data. Reserved extents never reach the parsers.
        var clustersRemaining = (geometry.MftValidDataLength - 1) / geometry.BytesPerCluster + 1;
        long batches = 0;
        foreach (var (_, clusterCount) in extents)
        {
            var clusters = Math.Min(clusterCount, clustersRemaining);
            batches += (clusters - 1) / clustersPerChunk + 1;
            if (batches >= parserLimit)
            {
                return parserLimit;
            }

            clustersRemaining -= clusters;
            if (clustersRemaining == 0)
            {
                break;
            }
        }

        return (int)batches;
    }

    private static async Task<UInt128> ParseAllRecordsAsync(
        Microsoft.Win32.SafeHandles.SafeFileHandle volume,
        NtfsVolumeData geometry,
        List<(long Lcn, long ClusterCount)> extents,
        int recordSize,
        int recordCount,
        int targetChunkBytes,
        int parserCount,
        int queueCapacity,
        int carryBytes,
        uint[] parent,
        byte[] flags,
        long[] realSize,
        long[] allocSize,
        long[] lastWrite,
        string?[] names,
        bool keepFileNames,
        bool deferFileNames,
        Dictionary<int, long>? extensionFileAllocations,
        MftRecordAllocations? recordAllocation,
        uint[] referenceSequences,
        NtfsVolumeBitmap? observedBitmap,
        Action<int> reportThrottled,
        CancellationToken cancellationToken)
    {
        var clusterBytes = (int)geometry.BytesPerCluster;
        var clustersPerChunk = targetChunkBytes / clusterBytes;

        // An extension can hold part of the allocation, the first $DATA extent or a $FILE_NAME.
        var extensions = new List<ExtensionRecord>();
        var initializedClusters = (geometry.MftValidDataLength - 1) / clusterBytes + 1;
        UInt128 scannedClusterReferences;
        var (firstLcn, firstExtentClusters) = extents[0];
        if (initializedClusters <= clustersPerChunk && initializedClusters <= firstExtentClusters)
        {
            // One read cannot overlap with parsing. Skip creating a channel and worker.
            await ScanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var bytes = (int)initializedClusters * clusterBytes;
            var buffer = ArrayPool<byte>.Shared.Rent(bytes);
            try
            {
                ReadExact(volume, buffer.AsSpan(0, bytes), firstLcn * clusterBytes);
                await ScanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                var attributeRuns = observedBitmap is not null ? new List<(long Lcn, long ClusterCount)>() : null;
                scannedClusterReferences = ParseBlock(new Block(buffer, 0, recordCount), extensions, attributeRuns);
                reportThrottled(recordCount);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        else
        {
            scannedClusterReferences = await ParsePipelineAsync().ConfigureAwait(false);
        }

        ValidateParentReferences(parent, flags, referenceSequences, cancellationToken);

        // Keep extension data on its own record until its base reference is verified.
        Dictionary<int, (int Record, int Rank)>? recoveredNames = null;
        for (var index = 0; index < extensions.Count; index++)
        {
            if ((index & 4095) == 0)
            {
                await ScanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var extension = extensions[index];
            var baseNumber = extension.BaseNumber;
            if ((flags[baseNumber] & (MftRecordTable.FlagInUse | MftRecordTable.FlagExtension)) != MftRecordTable.FlagInUse ||
                extension.BaseSequence == 0 || (referenceSequences[baseNumber] >> 16) != extension.BaseSequence)
            {
                continue;
            }

            var record = extension.RecordNumber;
            recordAllocation?.Merge(record, baseNumber);
            if ((flags[record] & MftRecordTable.FlagHasName) != 0 &&
                ((flags[baseNumber] & MftRecordTable.FlagHasName) == 0 || parent[baseNumber] == MftRecordTable.InvalidParentRecord) &&
                IsCurrentParentReference(parent[record], baseNumber, (ushort)referenceSequences[record], flags, referenceSequences))
            {
                var rank = MftRecordParser.NamespaceRank(extension.NameNamespace);
                recoveredNames ??= new();
                if (!recoveredNames.TryGetValue(baseNumber, out var current) ||
                    rank > current.Rank || (rank == current.Rank && record < current.Record))
                {
                    recoveredNames[baseNumber] = (record, rank);
                }
            }

            if (extension.HasLogicalSize && (flags[baseNumber] & MftRecordTable.FlagHasSize) == 0)
            {
                realSize[baseNumber] = extension.LogicalBytes;
                if (allocSize.Length != 0)
                {
                    allocSize[baseNumber] = extension.UnnamedDataAllocation;
                }
                else if (extensionFileAllocations is not null && (flags[baseNumber] & MftRecordTable.FlagDirectory) == 0)
                {
                    extensionFileAllocations.Add(baseNumber, extension.UnnamedDataAllocation);
                }

                flags[baseNumber] |= MftRecordTable.FlagHasSize;
            }
        }

        if (recoveredNames is not null)
        {
            var index = 0;
            foreach (var (baseNumber, name) in recoveredNames)
            {
                if ((index++ & 4095) == 0)
                {
                    await ScanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                var isDirectory = (flags[baseNumber] & MftRecordTable.FlagDirectory) != 0;
                flags[baseNumber] |= MftRecordTable.FlagHasName;
                parent[baseNumber] = parent[name.Record];
                names[baseNumber] = keepFileNames || isDirectory ? names[name.Record] : null;
            }
        }

        return scannedClusterReferences;

        UInt128 ParseBlock(Block block, List<ExtensionRecord> localExtensions, List<(long Lcn, long ClusterCount)>? attributeRuns)
        {
            UInt128 referencedClusters = 0;
            var info = new MftRecordInfo();
            var span = block.Buffer.AsSpan(0, block.RecordCount * recordSize);
            for (var r = 0; r < block.RecordCount; r++)
            {
                var globalIndex = block.StartRecordIndex + r;
                var recordSpan = span.Slice(r * recordSize, recordSize);
                if (!MftRecordParser.TryParseRecord(recordSpan, ref info,
                    requireInUse: true, readLastWrite: lastWrite.Length != 0, countAllocation: recordAllocation is not null,
                    volumeClusters: geometry.TotalClusters, observedBitmap: observedBitmap, attributeRuns: attributeRuns))
                {
                    if (info.HasIncompleteAllocation)
                    {
                        flags[globalIndex] = MftRecordTable.FlagIncompleteAllocation;
                    }

                    continue;
                }

                byte flag = MftRecordTable.FlagInUse;
                if (info.HasIncompleteAllocation) flag |= MftRecordTable.FlagIncompleteAllocation;
                if (info.IsDirectory) flag |= MftRecordTable.FlagDirectory;
                if (info.HasName) flag |= MftRecordTable.FlagHasName;

                if (observedBitmap is not null)
                {
                    referencedClusters += (ulong)info.AllocatedClusters;
                }

                if (recordAllocation is not null && info.AllocatedClusters > 0)
                {
                    recordAllocation.SetParsedClusters(globalIndex, info.AllocatedClusters);
                }

                if (info.IsExtension)
                {
                    flag |= MftRecordTable.FlagExtension;
                    if (info.BaseRecordNumber < (ulong)recordCount && (info.HasName || info.DataSizesValid || info.AllocatedClusters > 0))
                    {
                        localExtensions.Add(new ExtensionRecord(
                            globalIndex, (int)info.BaseRecordNumber, info.BaseRecordSequence,
                            info.FileNameNamespace, info.DataSizesValid, info.DataRealSize, info.DataAllocatedSize));
                    }
                }
                else
                {
                    if (info.DataSizesValid)
                    {
                        flag |= MftRecordTable.FlagHasSize;
                        realSize[globalIndex] = info.DataRealSize;
                        if (allocSize.Length != 0)
                        {
                            allocSize[globalIndex] = info.DataAllocatedSize;
                        }
                    }
                }

                parent[globalIndex] = info.ParentRecordNumber < (ulong)recordCount
                    ? (uint)info.ParentRecordNumber
                    : MftRecordTable.InvalidParentRecord;
                referenceSequences[globalIndex] = ((uint)info.SequenceNumber << 16) | info.ParentRecordSequence;
                if (lastWrite.Length != 0)
                {
                    lastWrite[globalIndex] = info.LastWriteFileTime;
                }
                // The folder scan skips ordinary file names. An extension with a name
                // may belong to a directory even if its own directory flag is clear.
                if (info.HasName && ((keepFileNames && !deferFileNames) || info.IsDirectory || info.IsExtension))
                {
                    names[globalIndex] = Encoding.Unicode.GetString(recordSpan.Slice(info.NameOffsetInRecord, info.NameLengthChars * 2));
                }

                flags[globalIndex] = flag;
            }

            return referencedClusters;
        }

        async Task<UInt128> ParsePipelineAsync()
        {
            var channel = Channel.CreateBounded<Block>(new BoundedChannelOptions(queueCapacity)
            {
                SingleReader = false,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait
            });

            var extensionLock = new object();
            var parsedCounter = 0;

            var parsers = new Task[parserCount];
            var parserClusters = observedBitmap is not null ? new UInt128[parserCount] : null;
            for (var i = 0; i < parserCount; i++)
            {
                var parserIndex = i;
                parsers[i] = Task.Run(async () =>
                {
                    var localExtensions = new List<ExtensionRecord>();
                    var attributeRuns = observedBitmap is not null ? new List<(long Lcn, long ClusterCount)>() : null;
                    UInt128 referencedClusters = 0;
                    try
                    {
                        await foreach (var block in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                        {
                            try
                            {
                                await ScanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                                referencedClusters += ParseBlock(block, localExtensions, attributeRuns);

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

                    if (parserClusters is not null)
                    {
                        parserClusters[parserIndex] = referencedClusters;
                    }

                    if (localExtensions.Count > 0)
                    {
                        lock (extensionLock)
                        {
                            extensions.AddRange(localExtensions);
                        }
                    }
                }, cancellationToken);
            }

            // Reader: stream extents, emit record-aligned blocks.
            var carry = carryBytes > 0 ? new byte[carryBytes] : [];
            var carryLength = 0;
            var startRecordIndex = 0;
            var parserCompletion = Task.WhenAll(parsers);

            try
            {
                // Ignore reserved clusters beyond the initialized records, while keeping reads cluster-aligned.
                var mftClustersRemaining = initializedClusters;
                foreach (var (lcn, clusterCount) in extents)
                {
                    var clustersRemaining = Math.Min(clusterCount, mftClustersRemaining);
                    mftClustersRemaining -= clustersRemaining;
                    var currentLcn = lcn;
                    while (clustersRemaining > 0 && startRecordIndex < recordCount)
                    {
                        await ScanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
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

            UInt128 totalReferencedClusters = 0;
            if (parserClusters is not null)
            {
                foreach (var clusters in parserClusters)
                {
                    totalReferencedClusters += clusters;
                }
            }

            return totalReferencedClusters;
        }
    }

    internal static void ValidateParentReferences(uint[] parents, byte[] flags, uint[] sequences, CancellationToken cancellationToken)
    {
        const byte namedBase = MftRecordTable.FlagInUse | MftRecordTable.FlagHasName;
        for (var index = 0; index < parents.Length; index++)
        {
            if ((index & 4095) == 0)
            {
                ScanGate.Wait(cancellationToken);
            }

            if (index == MftRecordTable.RootRecordNumber ||
                (flags[index] & (namedBase | MftRecordTable.FlagExtension)) != namedBase)
            {
                continue;
            }

            if (!IsCurrentParentReference(parents[index], index, (ushort)sequences[index], flags, sequences))
            {
                parents[index] = MftRecordTable.InvalidParentRecord;
            }
        }
    }

    private static bool IsCurrentParentReference(uint parent, int record, ushort expectedSequence, byte[] flags, uint[] sequences)
    {
        const byte baseDirectory = MftRecordTable.FlagInUse | MftRecordTable.FlagDirectory;
        return parent < (uint)flags.Length && parent != record && expectedSequence != 0 &&
               (flags[parent] & (baseDirectory | MftRecordTable.FlagExtension)) == baseDirectory &&
               expectedSequence == sequences[parent] >> 16;
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

    private readonly record struct ExtensionRecord(
        int RecordNumber, int BaseNumber, ushort BaseSequence, byte NameNamespace,
        bool HasLogicalSize, long LogicalBytes, long UnnamedDataAllocation);

    private readonly record struct Block(byte[] Buffer, int StartRecordIndex, int RecordCount);
}
