using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevTools.Services.FolderSizes;

internal readonly record struct DiskRange(long Start, long Length)
{
    internal long End => Start + Length;

    internal bool FitsInside(long capacity) => Start >= 0 && Start <= capacity && Length > 0 && Length <= capacity - Start;
}

internal readonly record struct VolumeDiskExtent(int DiskNumber, DiskRange Range, long FileSystemBytes);

internal readonly record struct DiskCapacity(
    int DiskNumber, long TotalBytes, long? PartitionedBytes, long MatchedPartitionBytes, long FileSystemBytes);

/// <summary>Reconciles disk geometry, partition ranges and filesystem capacities without opening disk data.</summary>
internal static class DiskCapacityInventory
{
    private const string PhysicalDrivePrefix = "PhysicalDrive";
    private const int FixedMedia = 12;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const uint GetDriveGeometry = 0x000700A0;
    private const uint GetDriveLayout = 0x00070050;
    private const uint GetVolumeExtents = 0x00560000;
    private const int MaximumBufferSize = 1024 * 1024;

    internal static IReadOnlyList<DiskCapacity> Read(IReadOnlyList<DriveDescriptor> volumes, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var buffer = new byte[4096];
        var extents = new List<VolumeDiskExtent>();
        foreach (var volume in volumes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (volume.Space is not { IsVolumeWide: true, TotalBytes: > 0 } space ||
                WindowsVolumePaths.GetDevicePath(volume.RootPath) is not { } device)
            {
                continue;
            }

            using var handle = OpenMetadata(device);
            if (Query(handle, GetVolumeExtents, ref buffer) >= 32 &&
                BinaryPrimitives.ReadUInt32LittleEndian(buffer) == 1)
            {
                // VOLUME_DISK_EXTENTS contains an aligned array of DISK_EXTENT records.
                var disk = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8));
                var start = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(16));
                var length = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24));
                extents.Add(new VolumeDiskExtent(disk, new DiskRange(start, length), space.TotalBytes));
            }
        }

        var disks = new List<DiskCapacity>();
        foreach (var disk in ReadDiskNumbers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var handle = OpenMetadata(@"\\.\" + PhysicalDrivePrefix + disk.ToString(CultureInfo.InvariantCulture));
            if (Query(handle, GetDriveGeometry, ref buffer) < 32 ||
                BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8)) != FixedMedia)
            {
                continue;
            }

            var total = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(24));
            if (total <= 0)
            {
                continue;
            }

            var bytes = Query(handle, GetDriveLayout, ref buffer);
            var ranges = ReadPartitionRanges(buffer.AsSpan(0, bytes), total);
            disks.Add(ranges is null
                ? new DiskCapacity(disk, total, null, 0, 0)
                : Reconcile(disk, total, ranges, extents));
        }

        return disks;
    }

    internal static HashSet<DiskRange>? ReadPartitionRanges(ReadOnlySpan<byte> layout, long total)
    {
        // DRIVE_LAYOUT_INFORMATION_EX has a 48-byte header and 144-byte PARTITION_INFORMATION_EX entries.
        const int headerBytes = 48;
        const int entryBytes = 144;
        if (layout.Length < headerBytes || BinaryPrimitives.ReadUInt32LittleEndian(layout) > 2)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(layout[4..]);
        if (count < 0 || count > (layout.Length - headerBytes) / entryBytes)
        {
            return null;
        }

        var ranges = new HashSet<DiskRange>();
        for (var i = 0; i < count; i++)
        {
            var entry = layout[(headerBytes + i * entryBytes)..];
            var range = new DiskRange(
                BinaryPrimitives.ReadInt64LittleEndian(entry[8..]),
                BinaryPrimitives.ReadInt64LittleEndian(entry[16..]));
            if (range.Length == 0)
            {
                continue;
            }

            if (!range.FitsInside(total))
            {
                return null;
            }

            ranges.Add(range);
        }

        return ranges;
    }

    internal static DiskCapacity Reconcile(int disk, long total, HashSet<DiskRange> partitions, IReadOnlyList<VolumeDiskExtent> extents)
    {
        // MBR extended partitions can cover logical partitions. Count the union of their ranges.
        long partitioned = 0;
        long lastEnd = 0;
        foreach (var range in partitions.OrderBy(range => range.Start))
        {
            partitioned += Math.Max(0, range.End - Math.Max(lastEnd, range.Start));
            lastEnd = Math.Max(lastEnd, range.End);
        }

        long mappedBytes = 0;
        long fileSystemBytes = 0;
        lastEnd = 0;
        var matched = extents
            .Where(volume => volume.DiskNumber == disk &&
                volume.FileSystemBytes > 0 && volume.FileSystemBytes <= volume.Range.Length &&
                partitions.Contains(volume.Range))
            .GroupBy(volume => volume.Range)
            .OrderBy(group => group.Key.Start);
        foreach (var group in matched)
        {
            var range = group.Key;
            var first = group.First();
            if (range.Start < lastEnd || group.Any(volume => volume.FileSystemBytes != first.FileSystemBytes))
            {
                // Conflicting mappings cannot be used to explain the physical bytes.
                return new DiskCapacity(disk, total, partitioned, 0, 0);
            }

            mappedBytes += range.Length;
            fileSystemBytes += first.FileSystemBytes;
            lastEnd = range.End;
        }

        return new DiskCapacity(disk, total, partitioned, mappedBytes, fileSystemBytes);
    }

    private static IEnumerable<int> ReadDiskNumbers()
    {
        var names = new char[4096];
        while (true)
        {
            var written = QueryDosDevice(null, names, (uint)names.Length);
            if (written > 0 && written <= names.Length)
            {
                return new string(names, 0, (int)written)
                    .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    .Where(name => name.StartsWith(PhysicalDrivePrefix, StringComparison.OrdinalIgnoreCase))
                    .Select(name => int.TryParse(name.AsSpan(PhysicalDrivePrefix.Length), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var number) ? number : -1)
                    .Where(number => number >= 0)
                    .Distinct()
                    .Order();
            }

            if (Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer || names.Length >= MaximumBufferSize / sizeof(char))
            {
                return [];
            }

            names = new char[names.Length * 2];
        }
    }

    private static int Query(SafeFileHandle handle, uint control, ref byte[] buffer)
    {
        if (handle.IsInvalid)
        {
            return 0;
        }

        while (true)
        {
            if (DeviceIoControl(handle, control, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out var bytes, IntPtr.Zero))
            {
                return bytes <= buffer.Length ? (int)bytes : 0;
            }

            if (Marshal.GetLastPInvokeError() is not (ErrorInsufficientBuffer or ErrorMoreData) ||
                buffer.Length >= MaximumBufferSize)
            {
                return 0;
            }

            buffer = new byte[buffer.Length * 2];
        }
    }

    // Desired access zero permits metadata queries without opening the disk or volume for data access.
    private static SafeFileHandle OpenMetadata(string path) => CreateFile(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string? device, [Out] char[] targets, uint targetChars);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle disk, uint control, IntPtr input, uint inputSize, [Out] byte[] output, uint outputSize,
        out uint bytesReturned, IntPtr overlapped);
}
