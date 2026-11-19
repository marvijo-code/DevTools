using System.Numerics;
using System.Runtime.InteropServices;
using DevTools.Services.Resources;
using Microsoft.Win32.SafeHandles;

namespace DevTools.Services.FolderSizes;

internal readonly record struct ScanResources(int LogicalProcessors, long? MemoryHeadroom)
{
    private const int MiB = 1024 * 1024;
    private const int FolderMftBytesPerRecord = 50;
    private const uint StorageQueryProperty = 0x002D1400;
    private const int DeviceSeekPenaltyProperty = 7;

    public static ScanResources Capture()
    {
        var headroom = OperatingSystem.IsWindows() ? NativeResourceApi.ReadMemoryHeadroom() : null;
        return new ScanResources(Environment.ProcessorCount, headroom);
    }

    internal static bool CanDeferLargestFileNames(int recordCount, int requestedFiles) =>
        // The extra read pass is worthwhile when most records will stay out of the ranking.
        requestedFiles is > 0 and <= 500 && recordCount >= (requestedFiles <= 100 ? 16_384 : 65_536);

    internal int VolumeWalkParallelism(int independentDisks)
    {
        const int maximumConcurrentWalks = 4;
        var byCpu = LogicalProcessors / 2;
        var byMemory = MemoryHeadroom.GetValueOrDefault() / (1024L * MiB);
        return (int)Math.Max(1, Math.Min(Math.Min(independentDisks, maximumConcurrentWalks), Math.Min(byCpu, byMemory)));
    }

    internal int LimitParallelFolderMft(int parallelism, int largestRecordCount)
    {
        if (largestRecordCount <= 0 || MemoryHeadroom is not long available)
        {
            return 1;
        }

        // A shared MFT plan needs half its share free. Keep extra margin for the walk starting first.
        var estimatedBytes = (long)largestRecordCount * FolderMftBytesPerRecord;
        return (int)Math.Clamp(available / 3 / estimatedBytes, 1, parallelism);
    }

    internal ScanResources ShareVolumeBudget(int volumeShare) =>
        new(Math.Max(1, LogicalProcessors / volumeShare), MemoryHeadroom / volumeShare);

    public int WalkWorkerCount(bool? seekPenalty, bool prioritize, int volumeShare = 1)
    {
        var cpuBudget = prioritize ? LogicalProcessors - 1 : LogicalProcessors / 2;
        var cpuLimit = Math.Max(1, cpuBudget / volumeShare);
        var memoryLimit = MemoryHeadroom is long available
            ? (int)Math.Clamp(available / (128L * MiB * volumeShare), 1, 12)
            : Math.Max(1, 4 / volumeShare);
        var deviceLimit = seekPenalty switch
        {
            true => prioritize ? 2 : 1,
            false => prioritize ? 12 : 8,
            null => 4
        };

        return Math.Min(Math.Min(cpuLimit, memoryLimit), deviceLimit);
    }

    internal int LargestFileWorkerCount(bool? seekPenalty)
    {
        // Directory reads can overlap on solid-state storage, even when some workers are waiting for I/O.
        var cpuLimit = (int)Math.Clamp(2L * LogicalProcessors, 1, 32);
        var memoryLimit = MemoryHeadroom is long available ? (int)Math.Clamp(available / (128L * MiB), 1, 32) : 4;
        var deviceLimit = seekPenalty switch
        {
            true => 2,
            false => 32,
            null => 4
        };
        return Math.Min(Math.Min(cpuLimit, memoryLimit), deviceLimit);
    }

    public (int ChunkBytes, int Parsers, int QueueCapacity, int CarryBytes) MftPlan(
        int records, long initializedBytes, int clusterBytes, int recordBytes, bool includeFileNames, bool deferFileNames = false)
    {
        // Estimate the indexed columns, temporary tree indexes and decoded names. Leave half of the
        // available commit and physical memory alone so a drive refresh can coexist with other apps.
        var bytesPerRecord = includeFileNames ? (deferFileNames ? 48 : 100) : FolderMftBytesPerRecord;
        var estimatedTableBytes = (long)records * bytesPerRecord;
        if (MemoryHeadroom is long available && estimatedTableBytes > available / 2)
        {
            throw new InsufficientMemoryException("Available memory is too low for an in-memory NTFS table.");
        }

        var bufferBudget = 64L * MiB;
        if (MemoryHeadroom is long headroom)
        {
            var remaining = headroom - estimatedTableBytes;
            bufferBudget = Math.Min(Math.Clamp(remaining / 16, 6L * MiB, 192L * MiB), remaining / 2);
        }

        var targetChunkBytes = bufferBudget >= 96L * MiB ? 8 * MiB : bufferBudget >= 24L * MiB ? 4 * MiB : MiB;
        var cpuParsers = Math.Clamp(LogicalProcessors / 2, 1, 6);

        // Give the parsers several chances to take work while the reader is still running.
        const int batchesPerParser = 8;
        while (targetChunkBytes > MiB && initializedBytes / targetChunkBytes < cpuParsers * batchesPerParser)
        {
            targetChunkBytes /= 2;
        }

        // Each read ends on a cluster boundary. Its unfinished record can only contain
        // a multiple of the common divisor, and aligned records need no carry buffer.
        var divisor = clusterBytes;
        var remainder = recordBytes;
        while (remainder != 0)
        {
            (divisor, remainder) = (remainder, divisor % remainder);
        }

        var carryBytes = recordBytes - divisor;
        var initializedClusters = (initializedBytes - 1) / clusterBytes + 1;
        while (true)
        {
            var clustersPerChunk = (int)Math.Min(initializedClusters, Math.Max(1, targetChunkBytes / clusterBytes));
            var chunkBytes = clustersPerChunk * clusterBytes;
            // Shared ArrayPool rents power-of-two buffers. Budget the carry separately.
            var bucket = (long)BitOperations.RoundUpToPowerOf2((ulong)Math.Max(16L, (long)chunkBytes + carryBytes));
            var blocks = (bufferBudget - carryBytes) / bucket;
            if (blocks >= 3)
            {
                // Keep a slot for the reader and at least one block waiting for a parser.
                var parsers = (int)Math.Min(cpuParsers, blocks - 2);
                // A pending batch per parser is enough to keep the workers fed.
                var queue = (int)Math.Min(blocks - parsers - 1, Math.Min(parsers, 4));
                return (chunkBytes, parsers, queue, carryBytes);
            }

            if (clustersPerChunk == 1)
            {
                throw new InsufficientMemoryException("Available memory is too low for NTFS read buffers.");
            }

            targetChunkBytes /= 2;
        }
    }

    internal static bool? ReadSeekPenalty(string? driveRoot)
    {
        if (!OperatingSystem.IsWindows() || WindowsVolumePaths.GetDevicePath(driveRoot) is not { } path)
        {
            return null;
        }

        using var volume = DiskCapacityInventory.OpenMetadata(path);
        return volume.IsInvalid ? null : ReadDeviceSeekPenalty(volume) ?? ReadBackingDiskSeekPenalty(volume);
    }

    private static bool? ReadBackingDiskSeekPenalty(SafeFileHandle volume)
    {
        var buffer = new byte[4096];
        var disks = DiskCapacityInventory.ReadVolumeExtents(volume, ref buffer).Select(extent => extent.DiskNumber);
        return ResolveBackingDiskSeekPenalty(disks, static number =>
        {
            using var disk = DiskCapacityInventory.OpenMetadata(DiskCapacityInventory.PhysicalDiskPath(number));
            return disk.IsInvalid ? null : ReadDeviceSeekPenalty(disk);
        });
    }

    internal static bool? ResolveBackingDiskSeekPenalty(IEnumerable<int> diskNumbers, Func<int, bool?> query)
    {
        var found = false;
        var unknown = false;
        foreach (var number in diskNumbers.Distinct())
        {
            var result = query(number);
            if (result is true)
            {
                return true;
            }

            found = true;
            unknown |= result is null;
        }

        return found && !unknown ? false : null;
    }

    private static bool? ReadDeviceSeekPenalty(SafeFileHandle volume)
    {
        var query = new StoragePropertyQuery { PropertyId = DeviceSeekPenaltyProperty };
        if (!DeviceIoControl(volume, StorageQueryProperty, in query, (uint)Marshal.SizeOf<StoragePropertyQuery>(),
                out var result, (uint)Marshal.SizeOf<SeekPenaltyDescriptor>(), out var size, IntPtr.Zero) || size < 9)
        {
            return null;
        }

        return result.IncursSeekPenalty != 0;
    }


    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle volume, uint code, in StoragePropertyQuery query, uint querySize,
        out SeekPenaltyDescriptor result, uint resultSize, out uint bytesReturned, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SeekPenaltyDescriptor
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty;
    }
}
