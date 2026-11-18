using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DevTools.Services.BiggestFiles.Ntfs;
using Microsoft.Win32.SafeHandles;

namespace DevTools.Services.FolderSizes;

/// <summary>Reads allocation for NTFS files, directory indexes and named data streams.</summary>
internal sealed class DirectoryAllocationReader(DirectoryAllocationIndex identities, uint allocationUnitBytes)
{
    // FILE_ID_EXTD_DIR_INFO. The variable-length UTF-16 name follows the fixed header.
    private const int EntryHeaderBytes = 88;
    private const int StreamBatchSize = 128;
    private const int FileIdExtdDirectoryInfo = 19;
    private const int FileStandardInfo = 1;
    private const int StandardInfoBytes = 24;
    private const int FileStreamInfo = 7;
    private const int StreamHeaderBytes = 24;
    private const int MaximumStreamBufferBytes = 1024 * 1024;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const uint Directory = 0x10;
    private const uint ReparsePoint = 0x400;
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
        out long files)
    {
        logicalBytes = 0;
        allocatedBytes = 0;
        files = 0;

        var nativePath = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;
        // List directory, share with other processes, open existing, and handle reparse points themselves.
        using var handle = OpenDirectory(nativePath, 1, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return false;
        }

        // The parent listing reports zero for directories. The directory handle can report
        // the allocation of its default index stream separately from its named data streams.
        if (GetFileInformationByHandleEx(handle, FileStandardInfo, _buffer, StandardInfoBytes))
        {
            // FILE_STANDARD_INFO.Directory is the byte after DeletePending.
            if (_buffer[21] == 0)
            {
                return false;
            }

            allocatedBytes = PhysicalAllocation(BinaryPrimitives.ReadInt64LittleEndian(_buffer));
        }

        allocatedBytes += ReadNamedStreamAllocation(handle);
        while (true)
        {
            ScanGate.Wait(cancellationToken);
            if (!GetFileInformationByHandleEx(handle, FileIdExtdDirectoryInfo, _buffer, (uint)_buffer.Length))
            {
                return Marshal.GetLastWin32Error() == ErrorNoMoreFiles;
            }

            var remaining = _buffer.AsSpan();
            var streamFileCount = 0;
            while (remaining.Length >= EntryHeaderBytes)
            {
                var nextOffset = BinaryPrimitives.ReadInt32LittleEndian(remaining);
                var nameBytes = BinaryPrimitives.ReadInt32LittleEndian(remaining[60..]);
                var entryLength = nextOffset == 0 ? remaining.Length : nextOffset;
                if (entryLength < EntryHeaderBytes || entryLength > remaining.Length ||
                    nameBytes <= 0 || (nameBytes & 1) != 0 || nameBytes > entryLength - EntryHeaderBytes ||
                    (nextOffset != 0 && remaining.Length - entryLength < EntryHeaderBytes))
                {
                    allocatedBytes += ReadFileStreams(handle, _fileIds.AsSpan(0, streamFileCount), cancellationToken);
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
                }
                else
                {
                    logicalBytes += Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(remaining[40..]));
                    var low = BinaryPrimitives.ReadUInt64LittleEndian(remaining[72..]);
                    var high = BinaryPrimitives.ReadUInt64LittleEndian(remaining[80..]);
                    if (identities.TryClaim(new UInt128(high, low)))
                    {
                        allocatedBytes += PhysicalAllocation(BinaryPrimitives.ReadInt64LittleEndian(remaining[48..]));
                        if (high == 0)
                        {
                            _fileIds[streamFileCount++] = low;
                            if (streamFileCount == _fileIds.Length)
                            {
                                if (scheduleStreams?.Invoke(node, handle, _fileIds) != true)
                                {
                                    allocatedBytes += ReadFileStreams(handle, _fileIds, cancellationToken);
                                }

                                streamFileCount = 0;
                            }
                        }
                    }

                    files++;
                }

                if (nextOffset == 0)
                {
                    break;
                }

                remaining = remaining[nextOffset..];
            }

            allocatedBytes += ReadFileStreams(handle, _fileIds.AsSpan(0, streamFileCount), cancellationToken);
        }
    }

    internal long ReadFileStreams(SafeFileHandle hint, ReadOnlySpan<ulong> fileIds, CancellationToken cancellationToken)
    {
        long allocatedBytes = 0;
        foreach (var fileId in fileIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = new FileIdDescriptor(fileId);
            // Metadata access only. Prevent reparse resolution and remote file recall.
            using var file = OpenFileById(hint, in id, 0, 7, IntPtr.Zero, 0x00300000);
            if (!file.IsInvalid)
            {
                allocatedBytes += ReadNamedStreamAllocation(file);
            }
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

    private long ReadNamedStreamAllocation(SafeFileHandle file)
    {
        while (!GetFileInformationByHandleEx(file, FileStreamInfo, _streamBuffer, (uint)_streamBuffer.Length))
        {
            if (Marshal.GetLastWin32Error() is not (ErrorInsufficientBuffer or ErrorMoreData) ||
                _streamBuffer.Length >= MaximumStreamBufferBytes)
            {
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
                return 0;
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

        return 0;
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
    private readonly HashSet<UInt128>[] _shards = new HashSet<UInt128>[ShardCount];
    private readonly int _entriesPerShard;

    internal DirectoryAllocationIndex(int entryLimit, int recordCapacity = 0)
    {
        _recordSequences = recordCapacity == 0 ? [] : new int[recordCapacity];
        _entriesPerShard = entryLimit / ShardCount;
        for (var i = 0; i < _shards.Length; i++)
        {
            _shards[i] = [];
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

    public bool TryClaim(UInt128 id)
    {
        if (id == 0)
        {
            return false;
        }

        var low = (ulong)id;
        var record = low & RecordNumberMask;
        if (record < (ulong)_recordSequences.Length && id >> 64 == 0)
        {
            // Store the full high 16 bits of the 64-bit ID. Zero marks a slot nobody has claimed.
            var sequence = (int)(low >> 48) + 1;
            var previous = Interlocked.CompareExchange(ref _recordSequences[(int)record], sequence, 0);
            if (previous == 0)
            {
                return true;
            }

            if (previous == sequence)
            {
                return false;
            }
        }

        // Reused record slots, larger IDs and records added after the geometry query still deduplicate by full ID.
        var shard = _shards[id.GetHashCode() & (ShardCount - 1)];
        lock (shard)
        {
            return shard.Count < _entriesPerShard && shard.Add(id);
        }
    }
}
