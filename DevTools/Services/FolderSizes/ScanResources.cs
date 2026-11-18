using System.Runtime.InteropServices;
using DevTools.Services.Resources;
using Microsoft.Win32.SafeHandles;

namespace DevTools.Services.FolderSizes;

internal readonly record struct ScanResources(int LogicalProcessors, long? MemoryHeadroom)
{
    private const int MiB = 1024 * 1024;
    private const uint StorageQueryProperty = 0x002D1400;
    private const int DeviceSeekPenaltyProperty = 7;

    public static ScanResources Capture()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new ScanResources(Environment.ProcessorCount, null);
        }

        var counters = NativeResourceApi.ReadSystemCounters();
        long? headroom = counters.PhysicalTotalBytes > 0 && counters.CommitLimitBytes > 0
            ? Math.Min(counters.PhysicalAvailableBytes, Math.Max(0, counters.CommitLimitBytes - counters.CommitTotalBytes))
            : null;
        return new ScanResources(Environment.ProcessorCount, headroom);
    }

    public int WalkWorkerCount(bool? seekPenalty, bool prioritize)
    {
        var cpuLimit = Math.Max(1, prioritize ? LogicalProcessors - 1 : LogicalProcessors / 2);
        var memoryLimit = MemoryHeadroom is long available
            ? (int)Math.Clamp(available / (128 * MiB), 1, 12)
            : 4;
        var deviceLimit = seekPenalty switch
        {
            true => prioritize ? 2 : 1,
            false => prioritize ? 12 : 8,
            null => 4
        };

        return Math.Min(Math.Min(cpuLimit, memoryLimit), deviceLimit);
    }

    public (int ChunkBytes, int Parsers, int QueueCapacity) MftPlan(int records, bool includeFileNames)
    {
        // Estimate the indexed columns, temporary tree indexes and decoded names. Leave half of the
        // available commit and physical memory alone so a drive refresh can coexist with other apps.
        var estimatedTableBytes = (long)records * (includeFileNames ? 100 : 50);
        if (MemoryHeadroom is long available && estimatedTableBytes > available / 2)
        {
            throw new InsufficientMemoryException("Available memory is too low for an in-memory NTFS table.");
        }

        var bufferBudget = MemoryHeadroom is long headroom
            ? Math.Clamp((headroom - estimatedTableBytes) / 16, 6L * MiB, 192L * MiB)
            : 64L * MiB;
        var chunkBytes = bufferBudget >= 96L * MiB ? 8 * MiB : bufferBudget >= 24L * MiB ? 4 * MiB : MiB;

        // A carry-over fragment can round the ArrayPool rental up to twice the chunk size.
        var blocks = (int)(bufferBudget / (2 * chunkBytes));
        var parsers = Math.Clamp(LogicalProcessors / 2, 1, Math.Min(6, blocks - 2));
        var queue = Math.Clamp(blocks - parsers - 1, 1, 4);
        return (chunkBytes, parsers, queue);
    }

    internal static bool? ReadSeekPenalty(string driveRoot)
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
