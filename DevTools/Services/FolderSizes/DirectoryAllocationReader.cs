using System.Buffers.Binary;
using System.Runtime.InteropServices;
using DevTools.Services.BiggestFiles.Ntfs;
using Microsoft.Win32.SafeHandles;

namespace DevTools.Services.FolderSizes;

/// <summary>Reads file identities and allocation sizes in the same directory batches as logical lengths.</summary>
internal sealed class DirectoryAllocationReader(DirectoryAllocationIndex identities)
{
    // FILE_ID_EXTD_DIR_INFO. The variable-length UTF-16 name follows the fixed header.
    private const int EntryHeaderBytes = 88;
    private const int FileIdExtdDirectoryInfo = 19;
    private const int ErrorNoMoreFiles = 18;
    private const uint Directory = 0x10;
    private const uint ReparsePoint = 0x400;
    private readonly byte[] _buffer = GC.AllocateUninitializedArray<byte>(64 * 1024);

    public bool TryRead(
        string path,
        List<string> subfolders,
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

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!GetFileInformationByHandleEx(handle, FileIdExtdDirectoryInfo, _buffer, (uint)_buffer.Length))
            {
                return Marshal.GetLastWin32Error() == ErrorNoMoreFiles;
            }

            var remaining = _buffer.AsSpan();
            while (remaining.Length >= EntryHeaderBytes)
            {
                var nextOffset = BinaryPrimitives.ReadInt32LittleEndian(remaining);
                var nameBytes = BinaryPrimitives.ReadInt32LittleEndian(remaining[60..]);
                var entryLength = nextOffset == 0 ? remaining.Length : nextOffset;
                if (entryLength < EntryHeaderBytes || entryLength > remaining.Length ||
                    nameBytes <= 0 || (nameBytes & 1) != 0 || nameBytes > entryLength - EntryHeaderBytes ||
                    (nextOffset != 0 && remaining.Length - entryLength < EntryHeaderBytes))
                {
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
                    var bytes = BinaryPrimitives.ReadInt64LittleEndian(remaining[48..]);
                    if (bytes > 0)
                    {
                        var low = BinaryPrimitives.ReadUInt64LittleEndian(remaining[72..]);
                        var high = BinaryPrimitives.ReadUInt64LittleEndian(remaining[80..]);
                        allocatedBytes += identities.Claim(new UInt128(high, low), bytes);
                    }

                    files++;
                }

                if (nextOffset == 0)
                {
                    break;
                }

                remaining = remaining[nextOffset..];
            }
        }
    }

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

    public long Claim(UInt128 id, long bytes)
    {
        if (id == 0)
        {
            return 0;
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
                return bytes;
            }

            if (previous == sequence)
            {
                return 0;
            }
        }

        // Reused record slots, larger IDs and records added after the geometry query still deduplicate by full ID.
        var shard = _shards[id.GetHashCode() & (ShardCount - 1)];
        lock (shard)
        {
            return shard.Count < _entriesPerShard && shard.Add(id) ? bytes : 0;
        }
    }
}
