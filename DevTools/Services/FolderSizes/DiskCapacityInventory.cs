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

internal readonly record struct VolumeDiskExtent(int DiskNumber, DiskRange Range, long? FileSystemBytes);

internal readonly record struct DiskCapacity(
    int DiskNumber, long TotalBytes, long? PartitionedBytes, long VolumeExtentBytes,
    long MatchedExtentBytes, long FileSystemBytes, long VolumeExtentBytesInPartitions);

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

    internal static IReadOnlyList<DiskCapacity> Read(
        IReadOnlyList<DriveDescriptor> volumes, CancellationToken cancellationToken, out int[][] backingDisks)
    {
        backingDisks = new int[volumes.Count][];
        Array.Fill(backingDisks, Array.Empty<int>());
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var buffer = new byte[4096];
        var extents = new List<VolumeDiskExtent>();
        for (var index = 0; index < volumes.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var volume = volumes[index];
            if (WindowsVolumePaths.GetDevicePath(volume.RootPath) is not { } device)
            {
                continue;
            }

            using var handle = OpenMetadata(device);
            var volumeExtents = ReadVolumeExtents(handle, ref buffer);
            backingDisks[index] = GetBackingDiskNumbers(volumeExtents);
            long? fileSystemBytes = volumeExtents.Length == 1 && volume.Space is { IsVolumeWide: true, TotalBytes: > 0 } space
                ? space.TotalBytes
                : null;
            foreach (var (disk, range) in volumeExtents)
            {
                extents.Add(new VolumeDiskExtent(disk, range, fileSystemBytes));
            }
        }

        var disks = new List<DiskCapacity>();
        foreach (var disk in ReadDiskNumbers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var handle = OpenMetadata(PhysicalDiskPath(disk));
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
            disks.Add(Reconcile(disk, total, ranges, extents));
        }

        return disks;
    }

    internal static int[] GetBackingDiskNumbers((int DiskNumber, DiskRange Range)[] extents) => extents.Length switch
    {
        0 => [],
        1 => [extents[0].DiskNumber],
        _ => extents.Select(extent => extent.DiskNumber).Distinct().ToArray()
    };

    internal static (int DiskNumber, DiskRange Range)[] ReadVolumeExtents(SafeFileHandle volume, ref byte[] buffer)
    {
        var bytes = Query(volume, GetVolumeExtents, ref buffer);
        return ParseVolumeExtents(buffer.AsSpan(0, bytes));
    }

    internal static (int DiskNumber, DiskRange Range)[] ParseVolumeExtents(ReadOnlySpan<byte> reply)
    {
        // VOLUME_DISK_EXTENTS has a four-byte count followed by an aligned array of DISK_EXTENT records.
        const int headerBytes = 8;
        const int extentBytes = 24;
        if (reply.Length < headerBytes)
        {
            return [];
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(reply);
        if (count < 1 || count > (reply.Length - headerBytes) / extentBytes)
        {
            return [];
        }

        var extents = new (int DiskNumber, DiskRange Range)[count];
        for (var i = 0; i < count; i++)
        {
            var extent = reply[(headerBytes + i * extentBytes)..];
            var disk = BinaryPrimitives.ReadInt32LittleEndian(extent);
            var range = new DiskRange(
                BinaryPrimitives.ReadInt64LittleEndian(extent[8..]),
                BinaryPrimitives.ReadInt64LittleEndian(extent[16..]));
            if (disk < 0 || !range.FitsInside(long.MaxValue))
            {
                return [];
            }

            extents[i] = (disk, range);
        }

        return extents;
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

    internal static DiskCapacity Reconcile(int disk, long total, HashSet<DiskRange>? partitions, IReadOnlyList<VolumeDiskExtent> extents)
    {
        var partitionRanges = partitions is null ? null : MergeOrderedRanges(partitions.OrderBy(range => range.Start));
        var volumeExtents = extents.Where(extent => extent.DiskNumber == disk && extent.Range.FitsInside(total))
            .GroupBy(extent => extent.Range).OrderBy(group => group.Key.Start).ToArray();
        var volumeRanges = MergeOrderedRanges(volumeExtents.Select(group => group.Key));

        long matchedExtentBytes = 0;
        long fileSystemBytes = 0;
        long lastEnd = 0;
        for (var index = 0; index < volumeExtents.Length; index++)
        {
            var group = volumeExtents[index];
            var range = group.Key;
            var overlapsAnother = range.Start < lastEnd ||
                (index + 1 < volumeExtents.Length && volumeExtents[index + 1].Key.Start < range.End);
            lastEnd = Math.Max(lastEnd, range.End);

            // Physical ranges can be counted once even when a filesystem mapping is ambiguous.
            if (!overlapsAnother && group.First().FileSystemBytes is long bytes && bytes > 0 && bytes <= range.Length &&
                group.All(extent => extent.FileSystemBytes == bytes))
            {
                matchedExtentBytes += range.Length;
                fileSystemBytes += bytes;
            }
        }

        return new DiskCapacity(disk, total, partitionRanges?.Sum(range => range.Length),
            volumeRanges.Sum(range => range.Length), matchedExtentBytes, fileSystemBytes,
            partitionRanges is null ? 0 : MeasureIntersection(partitionRanges, volumeRanges));
    }

    private static List<DiskRange> MergeOrderedRanges(IEnumerable<DiskRange> ranges)
    {
        // MBR extended partitions can cover logical partitions. Adjacent ranges also have no gap.
        var merged = new List<DiskRange>();
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                var previous = merged[^1];
                merged[^1] = new DiskRange(previous.Start, Math.Max(previous.End, range.End) - previous.Start);
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    private static long MeasureIntersection(List<DiskRange> partitions, List<DiskRange> volumes)
    {
        long bytes = 0;
        var partitionIndex = 0;
        var volumeIndex = 0;
        while (partitionIndex < partitions.Count && volumeIndex < volumes.Count)
        {
            var partition = partitions[partitionIndex];
            var volume = volumes[volumeIndex];
            bytes += Math.Max(0, Math.Min(partition.End, volume.End) - Math.Max(partition.Start, volume.Start));
            if (partition.End <= volume.End)
            {
                partitionIndex++;
            }
            else
            {
                volumeIndex++;
            }
        }

        return bytes;
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

    internal static string PhysicalDiskPath(int number) =>
        @"\\.\" + PhysicalDrivePrefix + number.ToString(CultureInfo.InvariantCulture);

    // Desired access zero permits disk and volume metadata queries.
    internal static SafeFileHandle OpenMetadata(string path) => CreateFile(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);

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
