using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevTools.Services.FolderSizes;

internal readonly record struct DiskRange(long Start, long Length)
{
    internal long End => Start + Length;

    internal bool FitsInside(long capacity) => Start >= 0 && Start <= capacity && Length > 0 && Length <= capacity - Start;
}

internal readonly record struct VolumeDiskExtent(int DiskNumber, DiskRange Range, long? FileSystemBytes);

internal readonly record struct DiskPartitionLayout(HashSet<DiskRange> Ranges, DiskRange? GptUsableRange, DiskRange? MbrSector);

internal readonly record struct DiskCapacity(
    int DiskNumber, long? TotalBytes, long? PartitionedBytes, long VolumeExtentBytes,
    long MatchedExtentBytes, long FileSystemBytes, long VolumeExtentBytesInPartitions,
    long? UnassignedGptUsableBytes = null, long? UnassignedMbrSectorBytes = null);

/// <summary>Reconciles disk geometry, partition ranges and filesystem capacities without opening disk data.</summary>
internal static class DiskCapacityInventory
{
    private const string PhysicalDrivePrefix = "PhysicalDrive";
    private const int RemovableMedia = 11;
    private const int FixedMedia = 12;
    private const uint MbrPartitionStyle = 0;
    private const uint GptPartitionStyle = 1;
    private const uint RawPartitionStyle = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const uint GetDriveGeometry = 0x000700A0;
    private const uint GetDriveLayout = 0x00070050;
    private const uint GetDiskLength = 0x0007405C;
    private const uint ReadDataAccess = 0x00000001;
    private const uint GetVolumeExtents = 0x00560000;
    private const int MaximumBufferSize = 1024 * 1024;

    internal static IReadOnlyList<VolumeDiskExtent> ReadVolumeTopology(
        IReadOnlyList<DriveDescriptor> volumes, CancellationToken cancellationToken, out int[][] backingDisks)
    {
        backingDisks = new int[volumes.Count][];
        Array.Fill(backingDisks, Array.Empty<int>());
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        using var errorMode = WindowsVolumePaths.SuppressCriticalErrors();
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

        return extents;
    }

    internal static IReadOnlyList<DiskCapacity> ReadPhysicalDisks(
        IReadOnlyList<VolumeDiskExtent> extents, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        using var errorMode = WindowsVolumePaths.SuppressCriticalErrors();
        var buffer = new byte[4096];
        var disks = new List<DiskCapacity>();
        var diskNumbers = ReadDiskNumbers().Union(extents.Select(extent => extent.DiskNumber)).Order();
        foreach (var disk in diskNumbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = PhysicalDiskPath(disk);
            using var handle = OpenMetadata(path);
            var geometryBytes = Query(handle, GetDriveGeometry, ref buffer);
            var geometry = ReadDiskGeometry(buffer.AsSpan(0, geometryBytes));
            var total = geometry?.TotalBytes ?? QueryDiskLength(path, ref buffer);

            DiskPartitionLayout? layout = null;
            if (total is long capacity)
            {
                var bytes = Query(handle, GetDriveLayout, ref buffer);
                layout = ReadPartitionLayout(buffer.AsSpan(0, bytes), capacity, geometry?.SectorBytes ?? 0);
            }

            disks.Add(Reconcile(disk, total, layout?.Ranges, extents, layout?.GptUsableRange, layout?.MbrSector));
        }

        return disks;
    }

    private static long? QueryDiskLength(string path, ref byte[] buffer)
    {
        // The disk length control requires read access. The handle only issues the length query.
        using var handle = OpenMetadata(path, ReadDataAccess);
        var bytes = Query(handle, GetDiskLength, ref buffer);
        return ReadDiskLength(buffer.AsSpan(0, bytes));
    }

    internal static long? ReadDiskLength(ReadOnlySpan<byte> reply)
    {
        var bytes = reply.Length >= sizeof(long) ? BinaryPrimitives.ReadInt64LittleEndian(reply) : 0;
        return bytes > 0 ? bytes : null;
    }

    internal static (long TotalBytes, uint SectorBytes)? ReadDiskGeometry(ReadOnlySpan<byte> geometry)
    {
        if (geometry.Length < 32 ||
            BinaryPrimitives.ReadInt32LittleEndian(geometry[8..]) is not (FixedMedia or RemovableMedia))
        {
            return null;
        }

        var total = BinaryPrimitives.ReadInt64LittleEndian(geometry[24..]);
        return total > 0 ? (total, BinaryPrimitives.ReadUInt32LittleEndian(geometry[20..])) : null;
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

    internal static DiskPartitionLayout? ReadPartitionLayout(ReadOnlySpan<byte> layout, long total, uint sectorBytes)
    {
        // DRIVE_LAYOUT_INFORMATION_EX has a 48-byte header and 144-byte PARTITION_INFORMATION_EX entries.
        const int headerBytes = 48;
        const int entryBytes = 144;
        const int mbrPartitionTypeOffset = 32;
        if (layout.Length < headerBytes)
        {
            return null;
        }

        var style = BinaryPrimitives.ReadUInt32LittleEndian(layout);
        if (style > RawPartitionStyle)
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
            if (style == MbrPartitionStyle && entry[mbrPartitionTypeOffset] == 0)
            {
                continue;
            }

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

        DiskRange? gptUsableRange = null;
        if (style == GptPartitionStyle)
        {
            // The GPT drive-layout union starts at byte 8, with a 16-byte disk ID ahead of the usable range.
            var usable = new DiskRange(BinaryPrimitives.ReadInt64LittleEndian(layout[24..]),
                BinaryPrimitives.ReadInt64LittleEndian(layout[32..]));
            if (usable.FitsInside(total) && usable.Start > 0 && usable.End < total &&
                ranges.All(range => range.Start >= usable.Start && range.End <= usable.End))
            {
                gptUsableRange = usable;
            }
        }

        DiskRange? mbrSector = style == MbrPartitionStyle && sectorBytes >= 512 && sectorBytes <= total && BitOperations.IsPow2(sectorBytes)
            ? new DiskRange(0, sectorBytes)
            : null;
        return new DiskPartitionLayout(ranges, gptUsableRange, mbrSector);
    }

    internal static DiskCapacity Reconcile(int disk, long? total, HashSet<DiskRange>? partitions,
        IReadOnlyList<VolumeDiskExtent> extents, DiskRange? gptUsableRange = null, DiskRange? mbrSector = null)
    {
        var partitionRanges = partitions is null ? null : MergeOrderedRanges(partitions.OrderBy(range => range.Start));
        var extentLimit = total ?? long.MaxValue;
        var volumeExtents = extents.Where(extent => extent.DiskNumber == disk && extent.Range.FitsInside(extentLimit))
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

        long? unassignedGptUsableBytes = null;
        long? unassignedMbrSectorBytes = null;
        if (partitionRanges is not null && (gptUsableRange.HasValue || mbrSector.HasValue))
        {
            var occupied = MergeOrderedRanges(partitionRanges.Concat(volumeRanges).OrderBy(range => range.Start));
            if (gptUsableRange is { } usable)
            {
                unassignedGptUsableBytes = usable.Length - MeasureIntersection(occupied, [usable]);
            }

            if (mbrSector is { } sector)
            {
                unassignedMbrSectorBytes = sector.Length - MeasureIntersection(occupied, [sector]);
            }
        }

        return new DiskCapacity(disk, total, partitionRanges?.Sum(range => range.Length),
            volumeRanges.Sum(range => range.Length), matchedExtentBytes, fileSystemBytes,
            partitionRanges is null ? 0 : MeasureIntersection(partitionRanges, volumeRanges),
            unassignedGptUsableBytes, unassignedMbrSectorBytes);
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
                    .Where(number => number >= 0);
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

    // Most disk and volume metadata queries only require a handle with zero desired access.
    internal static SafeFileHandle OpenMetadata(string path) => OpenMetadata(path, 0);

    private static SafeFileHandle OpenMetadata(string path, uint access) =>
        CreateFile(path, access, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);

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
