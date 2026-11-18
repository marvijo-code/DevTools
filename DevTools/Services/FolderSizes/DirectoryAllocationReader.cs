using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DevTools.Services.BiggestFiles.Ntfs;
using Microsoft.Win32.SafeHandles;

namespace DevTools.Services.FolderSizes;

/// <summary>Reads allocation for NTFS files, directory indexes and named data streams.</summary>
internal sealed class DirectoryAllocationReader(DirectoryAllocationIndex identities, uint allocationUnitBytes, int streamWorkers = 1)
{
    // FILE_ID_EXTD_DIR_INFO. The variable-length UTF-16 name follows the fixed header.
    private const int EntryHeaderBytes = 88;
    private const int StreamBatchSize = 128;
    private const int MinimumSharedStreamBatchSize = 16;
    private const int FileIdExtdDirectoryInfo = 19;
    private const int FileStandardInfo = 1;
    private const int StandardInfoBytes = 24;
    private const int FileStreamInfo = 7;
    private const int StreamHeaderBytes = 24;
    private const int MaximumStreamBufferBytes = 1024 * 1024;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorHandleEof = 38;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const uint Directory = 0x10;
    private const uint ReparsePoint = 0x400;
    private const uint OpenNoRecall = 0x00100000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private readonly byte[] _buffer = GC.AllocateUninitializedArray<byte>(64 * 1024);
    private readonly ulong[] _fileIds = GC.AllocateUninitializedArray<ulong>(StreamBatchSize);
    private byte[] _streamBuffer = GC.AllocateUninitializedArray<byte>(1024);

    internal delegate bool TryScheduleStreams(int node, SafeFileHandle hint, ReadOnlySpan<ulong> fileIds);

    public bool TryRead(
        string path,
        int node,
        List<string> subfolders,
        TryScheduleStreams? scheduleStreams,
        CancellationToken cancellationToken,
        out long logicalBytes,
        out long allocatedBytes,
        out long files,
        out bool incompleteAllocation)
    {
        logicalBytes = 0;
        allocatedBytes = 0;
        files = 0;
        incompleteAllocation = allocationUnitBytes == 0;

        var nativePath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;
        // List directory, share with other processes, open existing, and handle reparse points themselves.
        using var handle = OpenDirectory(nativePath, 1, 7, IntPtr.Zero, 3, BackupSemantics | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            if (Marshal.GetLastPInvokeError() == ErrorAccessDenied)
            {
                ScanGate.Wait(cancellationToken);
                // Windows can allow metadata access to the directory itself when it denies listing its children.
                using var metadata = OpenDirectory(nativePath, 0, 7, IntPtr.Zero, 3,
                    BackupSemantics | OpenReparsePoint | OpenNoRecall, IntPtr.Zero);
                if (!metadata.IsInvalid && TryReadDirectoryIndex(metadata, out var indexBytes, out var isMetadataDirectory) &&
                    isMetadataDirectory)
                {
                    allocatedBytes = indexBytes + ReadNamedStreamAllocation(metadata, ref incompleteAllocation);
                }
            }

            incompleteAllocation = true;
            return false;
        }

        if (TryReadDirectoryIndex(handle, out allocatedBytes, out var isDirectory))
        {
            if (!isDirectory)
            {
                return false;
            }
        }
        else
        {
            incompleteAllocation = true;
        }

        allocatedBytes += ReadNamedStreamAllocation(handle, ref incompleteAllocation);
        while (true)
        {
            ScanGate.Wait(cancellationToken);
            if (!GetFileInformationByHandleEx(handle, FileIdExtdDirectoryInfo, _buffer, (uint)_buffer.Length))
            {
                return Marshal.GetLastWin32Error() == ErrorNoMoreFiles;
            }

            var remaining = _buffer.AsSpan();
            var streamFileCount = 0;
            var sharedFullBatch = false;
            while (remaining.Length >= EntryHeaderBytes)
            {
                var nextOffset = BinaryPrimitives.ReadInt32LittleEndian(remaining);
                var nameBytes = BinaryPrimitives.ReadInt32LittleEndian(remaining[60..]);
                var entryLength = nextOffset == 0 ? remaining.Length : nextOffset;
                if (entryLength < EntryHeaderBytes || entryLength > remaining.Length ||
                    nameBytes <= 0 || (nameBytes & 1) != 0 || nameBytes > entryLength - EntryHeaderBytes ||
                    (nextOffset != 0 && remaining.Length - entryLength < EntryHeaderBytes))
                {
                    allocatedBytes += ReadFileStreams(handle, _fileIds.AsSpan(0, streamFileCount), cancellationToken, ref incompleteAllocation);
                    return false;
                }

                var attributes = BinaryPrimitives.ReadUInt32LittleEndian(remaining[56..]);
                if ((attributes & Directory) != 0)
                {
                    if ((attributes & ReparsePoint) == 0)
                    {
                        var name = MemoryMarshal.Cast<byte, char>(remaining.Slice(EntryHeaderBytes, nameBytes));
                        if (!name.SequenceEqual(".") && !name.SequenceEqual(".."))
                        {
                            subfolders.Add(new string(name));
                        }
                    }
                    else
                    {
                        ScanGate.Wait(cancellationToken);
                        // Attribute the link's own storage to the containing folder. Its target stays outside this walk.
                        var low = BinaryPrimitives.ReadUInt64LittleEndian(remaining[72..]);
                        var high = BinaryPrimitives.ReadUInt64LittleEndian(remaining[80..]);
                        allocatedBytes += ReadReparseDirectoryAllocation(handle, low, high, ref incompleteAllocation);
                    }
                }
                else
                {
                    logicalBytes += Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(remaining[40..]));
                    var low = BinaryPrimitives.ReadUInt64LittleEndian(remaining[72..]);
                    var high = BinaryPrimitives.ReadUInt64LittleEndian(remaining[80..]);
                    var claim = high == 0 ? identities.Claim(low) : identities.Claim(new UInt128(high, low));
                    if (claim == DirectoryAllocationIndex.ClaimResult.New)
                    {
                        allocatedBytes += PhysicalAllocation(BinaryPrimitives.ReadInt64LittleEndian(remaining[48..]));
                        if (high == 0)
                        {
                            _fileIds[streamFileCount++] = low;
                            if (streamFileCount == _fileIds.Length)
                            {
                                if (scheduleStreams?.Invoke(node, handle, _fileIds) == true)
                                {
                                    sharedFullBatch = true;
                                }
                                else
                                {
                                    allocatedBytes += ReadFileStreams(handle, _fileIds, cancellationToken, ref incompleteAllocation);
                                }

                                streamFileCount = 0;
                            }
                        }
                        else
                        {
                            incompleteAllocation = true;
                        }
                    }
                    else if (claim == DirectoryAllocationIndex.ClaimResult.Unavailable)
                    {
                        incompleteAllocation = true;
                    }

                    files++;
                }

                if (nextOffset == 0)
                {
                    break;
                }

                remaining = remaining[nextOffset..];
            }

            var tail = _fileIds.AsSpan(0, streamFileCount);
            if (!sharedFullBatch && scheduleStreams is not null && tail.Length >= 2 * MinimumSharedStreamBatchSize)
            {
                // Leave a slice for the worker reading the directory and share the rest when the queue has room.
                var sliceSize = Math.Max(MinimumSharedStreamBatchSize, (tail.Length + streamWorkers - 1) / streamWorkers);
                while (tail.Length - sliceSize >= MinimumSharedStreamBatchSize &&
                    scheduleStreams(node, handle, tail[..sliceSize]))
                {
                    tail = tail[sliceSize..];
                }
            }

            allocatedBytes += ReadFileStreams(handle, tail, cancellationToken, ref incompleteAllocation);
        }
    }

    private long ReadReparseDirectoryAllocation(SafeFileHandle hint, ulong low, ulong high, ref bool incompleteAllocation)
    {
        if (high != 0)
        {
            incompleteAllocation = true;
            return 0;
        }

        var claim = identities.Claim(low);
        if (claim != DirectoryAllocationIndex.ClaimResult.New)
        {
            incompleteAllocation |= claim == DirectoryAllocationIndex.ClaimResult.Unavailable;
            return 0;
        }

        var id = new FileIdDescriptor(low);
        using var directory = OpenFileById(hint, in id, 0, 7, IntPtr.Zero,
            BackupSemantics | OpenReparsePoint | OpenNoRecall);
        if (directory.IsInvalid)
        {
            incompleteAllocation = true;
            return 0;
        }

        incompleteAllocation |= !TryReadDirectoryIndex(directory, out var indexBytes, out var isDirectory) || !isDirectory;
        return indexBytes + ReadNamedStreamAllocation(directory, ref incompleteAllocation);
    }

    private bool TryReadDirectoryIndex(SafeFileHandle directory, out long allocatedBytes, out bool isDirectory)
    {
        allocatedBytes = 0;
        isDirectory = false;
        // The directory handle reports the index allocation separately from named data streams.
        // Use separate scratch space so querying a junction does not overwrite the parent listing.
        if (!GetFileInformationByHandleEx(directory, FileStandardInfo, _streamBuffer, StandardInfoBytes))
        {
            return false;
        }

        // FILE_STANDARD_INFO.Directory is the byte after DeletePending.
        isDirectory = _streamBuffer[21] != 0;
        if (isDirectory)
        {
            allocatedBytes = PhysicalAllocation(BinaryPrimitives.ReadInt64LittleEndian(_streamBuffer));
        }

        return true;
    }

    internal long ReadFileStreams(SafeFileHandle hint, ReadOnlySpan<ulong> fileIds, CancellationToken cancellationToken, ref bool incompleteAllocation)
    {
        long allocatedBytes = 0;
        foreach (var fileId in fileIds)
        {
            ScanGate.Wait(cancellationToken);
            var id = new FileIdDescriptor(fileId);
            // Metadata access only. Prevent reparse resolution and remote file recall.
            using var file = OpenFileById(hint, in id, 0, 7, IntPtr.Zero, OpenReparsePoint | OpenNoRecall);
            if (file.IsInvalid)
            {
                incompleteAllocation = true;
                continue;
            }

            allocatedBytes += ReadNamedStreamAllocation(file, ref incompleteAllocation);
        }

        return allocatedBytes;
    }

    internal static SafeFileHandle? DuplicateVolumeHint(SafeFileHandle hint)
    {
        const uint duplicateSameAccess = 2;
        var process = GetCurrentProcess();
        if (DuplicateHandle(process, hint, process, out var copy, 0, false, duplicateSameAccess))
        {
            return copy;
        }

        copy?.Dispose();
        return null;
    }

    // A resident stream can report positive bytes stored inside its MFT record. Only
    // whole clusters can represent space independently allocated to the stream.
    private long PhysicalAllocation(long bytes) =>
        bytes > 0 && (allocationUnitBytes == 0 || bytes % allocationUnitBytes == 0) ? bytes : 0;

    private long ReadNamedStreamAllocation(SafeFileHandle file, ref bool incompleteAllocation)
    {
        while (!GetFileInformationByHandleEx(file, FileStreamInfo, _streamBuffer, (uint)_streamBuffer.Length))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorHandleEof)
            {
                // An ordinary directory may have no data streams.
                return 0;
            }

            if (error is not (ErrorInsufficientBuffer or ErrorMoreData) || _streamBuffer.Length >= MaximumStreamBufferBytes)
            {
                incompleteAllocation = true;
                return 0;
            }

            // A file with many streams can require more than the usual single-stream reply.
            _streamBuffer = GC.AllocateUninitializedArray<byte>(_streamBuffer.Length * 2);
        }

        long allocated = 0;
        var remaining = _streamBuffer.AsSpan();
        while (remaining.Length >= StreamHeaderBytes)
        {
            var nextOffset = BinaryPrimitives.ReadInt32LittleEndian(remaining);
            var nameBytes = BinaryPrimitives.ReadInt32LittleEndian(remaining[4..]);
            var entryLength = nextOffset == 0 ? remaining.Length : nextOffset;
            if (entryLength < StreamHeaderBytes || entryLength > remaining.Length ||
                nameBytes <= 0 || (nameBytes & 1) != 0 || nameBytes > entryLength - StreamHeaderBytes)
            {
                incompleteAllocation = true;
                return allocated;
            }

            var name = MemoryMarshal.Cast<byte, char>(remaining.Slice(StreamHeaderBytes, nameBytes));
            if (!name.SequenceEqual("::$DATA"))
            {
                allocated += PhysicalAllocation(BinaryPrimitives.ReadInt64LittleEndian(remaining[16..]));
            }

            if (nextOffset == 0)
            {
                return allocated;
            }

            remaining = remaining[nextOffset..];
        }

        incompleteAllocation = true;
        return allocated;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileIdDescriptor(ulong fileId)
    {
        private readonly int _size = 24;
        private readonly int _type = 0;
        private readonly ulong _fileId = fileId;
        private readonly ulong _reserved = 0;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess, SafeFileHandle source, IntPtr targetProcess, out SafeFileHandle target,
        uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint, in FileIdDescriptor id, uint access, uint share, IntPtr security, uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(
        string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, [Out] byte[] buffer, uint size);
}

/// <summary>Counts a file's allocation once per scan even if multiple directory entries link to it.</summary>
internal sealed class DirectoryAllocationIndex
{
    private const int ShardCount = 32;
    private const int MaximumRecords = 16_000_000;
    private const ulong RecordNumberMask = 0x0000FFFFFFFFFFFF;
    private readonly int[] _recordSequences;
    private readonly IdentityShard[] _shards = new IdentityShard[ShardCount];
    private readonly int _entriesPerShard;

    internal DirectoryAllocationIndex(int entryLimit, int recordCapacity = 0)
    {
        _recordSequences = recordCapacity == 0 ? [] : new int[recordCapacity];
        _entriesPerShard = entryLimit / ShardCount;
        for (var i = 0; i < _shards.Length; i++)
        {
            _shards[i] = new IdentityShard();
        }
    }

    public static DirectoryAllocationIndex? Create(string root, ScanResources resources, bool fullVolume)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var fileSystem = WindowsVolumePaths.GetVolumeGuid(root).HasValue
                ? WindowsVolumePaths.ReadVolumeInformation(root).FileSystem
                : new DriveInfo(root).DriveFormat;
            // ReFS block clones can share physical clusters across different file IDs.
            if (!string.Equals(fileSystem, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        // Keep at least seven eighths of current memory headroom available to other work.
        var budget = resources.MemoryHeadroom.GetValueOrDefault() / 8;
        if (budget / 48 < ShardCount)
        {
            return null;
        }

        var recordCapacity = fullVolume ? NativeMethods.GetDirectoryRecordCapacity(root) : 0;
        if (recordCapacity > MaximumRecords || (long)recordCapacity * sizeof(int) > budget / 2)
        {
            recordCapacity = 0;
        }

        var remainingBudget = budget - (long)recordCapacity * sizeof(int);
        var entryLimit = (int)Math.Min(MaximumRecords, remainingBudget / 48);
        return new DirectoryAllocationIndex(entryLimit, recordCapacity);
    }

    internal enum ClaimResult { New, AlreadyCounted, Unavailable }

    public ClaimResult Claim(ulong id)
    {
        if (id == 0)
        {
            return ClaimResult.Unavailable;
        }

        var record = id & RecordNumberMask;
        if (record < (ulong)_recordSequences.Length)
        {
            // Store the full high 16 bits of the 64-bit ID. Zero marks a slot nobody has claimed.
            var sequence = (int)(id >> 48) + 1;
            var previous = Interlocked.CompareExchange(ref _recordSequences[(int)record], sequence, 0);
            if (previous == 0)
            {
                return ClaimResult.New;
            }

            if (previous == sequence)
            {
                return ClaimResult.AlreadyCounted;
            }
        }

        // Mix the full ID when choosing a shard so sparse file records do not fill one shard early.
        var shard = _shards[((UInt128)id).GetHashCode() & (ShardCount - 1)];
        lock (shard)
        {
            return ClaimInShard(shard.FileIds, id, _entriesPerShard - (shard.ExtendedIds?.Count ?? 0));
        }
    }

    public ClaimResult Claim(UInt128 id)
    {
        if (id >> 64 == 0)
        {
            return Claim((ulong)id);
        }

        var shard = _shards[id.GetHashCode() & (ShardCount - 1)];
        lock (shard)
        {
            var capacity = _entriesPerShard - shard.FileIds.Count;
            if (capacity == 0)
            {
                return ClaimResult.Unavailable;
            }

            shard.ExtendedIds ??= [];
            return ClaimInShard(shard.ExtendedIds, id, capacity);
        }
    }

    private static ClaimResult ClaimInShard<T>(HashSet<T> ids, T id, int capacity)
    {
        if (ids.Count < capacity)
        {
            return ids.Add(id) ? ClaimResult.New : ClaimResult.AlreadyCounted;
        }

        return ids.Contains(id) ? ClaimResult.AlreadyCounted : ClaimResult.Unavailable;
    }

    private sealed class IdentityShard
    {
        internal readonly HashSet<ulong> FileIds = [];
        internal HashSet<UInt128>? ExtendedIds;
    }
}
