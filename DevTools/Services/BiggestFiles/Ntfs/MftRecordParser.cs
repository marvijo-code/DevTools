using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DevTools.Services.BiggestFiles.Ntfs;

/// <summary>Parsed fields from a single NTFS MFT FILE record that a size scan needs.</summary>
public struct MftRecordInfo
{
    public ushort SequenceNumber;
    public bool InUse;
    public bool IsDirectory;
    public bool HasAttributeList;

    /// <summary>Base record number (low 48 bits) when this is an extension record; 0 for a base record.</summary>
    public ulong BaseRecordNumber;
    public ushort BaseRecordSequence;
    public bool IsExtension;
    public long AllocatedClusters;

    public bool HasName;
    public ushort ParentRecordSequence;

    /// <summary>Parent directory record number (low 48 bits of the parent file reference).</summary>
    public ulong ParentRecordNumber;

    /// <summary>Byte offset of the UTF-16 file name within the (fixed-up) record buffer.</summary>
    public int NameOffsetInRecord;
    public int NameLengthChars;
    public byte FileNameNamespace;

    public bool HasUnnamedData;

    /// <summary>True when the unnamed $DATA seen is the first (LowestVCN == 0) chunk carrying valid sizes.</summary>
    public bool DataSizesValid;
    public long DataRealSize;
    public long DataAllocatedSize;

    /// <summary>Raw FILETIME of the last data modification, or 0 if unavailable.</summary>
    public long LastWriteFileTime;
}

/// <summary>
/// Pure, allocation-light parser for NTFS FILE records. Applies update-sequence fixups in place
/// and reads names/parents/sizes. Sizes come only from the unnamed <c>$DATA</c> attribute — never
/// from <c>$FILE_NAME</c>, whose sizes NTFS updates only lazily and are frequently stale.
/// </summary>
public static class MftRecordParser
{
    private const int SectorSize = 512;

    // Attribute type codes.
    private const uint AttrStandardInformation = 0x10;
    internal const uint AttrAttributeList = 0x20;
    private const uint AttrFileName = 0x30;
    internal const uint AttrData = 0x80;
    private const uint AttrEnd = 0xFFFFFFFF;

    // $FILE_NAME namespaces.
    private const byte NamespacePosix = 0;
    private const byte NamespaceWin32 = 1;
    private const byte NamespaceDos = 2;
    private const byte NamespaceWin32Dos = 3;

    /// <summary>
    /// Validates, applies fixups, and parses a single FILE record in place. Returns false for a
    /// non-FILE signature or a torn record (fixup mismatch). A not-in-use record still parses;
    /// callers inspect <see cref="MftRecordInfo.InUse"/>.
    /// </summary>
    public static bool TryParseRecord(Span<byte> record, ref MftRecordInfo info) =>
        TryParseRecord(record, ref info, requireInUse: false, readLastWrite: true);

    internal static bool TryParseRecord(
        Span<byte> record,
        ref MftRecordInfo info,
        bool requireInUse,
        bool readLastWrite,
        bool countAllocation = false,
        long volumeClusters = long.MaxValue,
        NtfsVolumeBitmap? observedBitmap = null,
        List<(long Lcn, long ClusterCount)>? attributeRuns = null)
    {
        info = default;

        if (record.Length < 0x30)
        {
            return false;
        }

        // Signature "FILE".
        if (record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E')
        {
            return false;
        }

        // The flags are in the record header, ahead of any sector trailer that needs fixups.
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x16, 2));
        var inUse = (flags & 0x0001) != 0;
        if ((requireInUse && !inUse) || !ApplyFixups(record))
        {
            return false;
        }

        var usedBytes = GetUsedRecordLength(record);
        if (usedBytes == 0)
        {
            return false;
        }

        record = record[..usedBytes];
        info.InUse = inUse;
        info.IsDirectory = (flags & 0x0002) != 0;
        info.SequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x10, 2));
        var baseReference = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(0x20, 8));
        info.BaseRecordNumber = baseReference & 0x0000FFFFFFFFFFFFUL;
        info.BaseRecordSequence = (ushort)(baseReference >> 48);
        info.IsExtension = baseReference != 0;

        var firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x14, 2));
        if (firstAttributeOffset < 0x18 || firstAttributeOffset >= record.Length)
        {
            // No attributes but still a structurally valid record.
            return true;
        }

        var bestNamespaceRank = -1;
        var offset = (int)firstAttributeOffset;

        while (offset + 0x10 <= record.Length)
        {
            var attributeType = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(offset, 4));
            if (attributeType == AttrEnd)
            {
                break;
            }

            var attributeLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(offset + 4, 4));
            if (attributeLength < 0x10 || attributeLength > record.Length - offset)
            {
                break;
            }

            var attribute = record.Slice(offset, attributeLength);
            var form = attribute[8];
            var nonResident = form == 1;
            if (countAllocation && nonResident &&
                TryGetMappingPairs(attribute, out var runs) &&
                TryDecodeRunList(runs, attributeRuns, out var allocated, volumeClusters: volumeClusters) &&
                allocated <= volumeClusters - info.AllocatedClusters)
            {
                info.AllocatedClusters += allocated;
                if (observedBitmap is not null && attributeRuns is not null)
                {
                    foreach (var (lcn, count) in attributeRuns)
                    {
                        observedBitmap.Mark(lcn, count);
                    }
                }
            }

            switch (attributeType)
            {
                case AttrStandardInformation when readLastWrite && form == 0:
                    ReadStandardInformation(attribute, ref info);
                    break;
                case AttrAttributeList when form is 0 or 1:
                    info.HasAttributeList = true;
                    break;
                case AttrFileName when form == 0:
                    ReadFileName(attribute, offset, ref info, ref bestNamespaceRank);
                    break;
                case AttrData when form is 0 or 1 && attribute[9] == 0:
                    ReadUnnamedData(attribute, nonResident, ref info);
                    break;
            }

            offset += attributeLength;
        }

        return true;
    }

    private static int GetUsedRecordLength(ReadOnlySpan<byte> record)
    {
        var length = BinaryPrimitives.ReadUInt32LittleEndian(record[0x18..]);
        return length >= 0x30 && length <= (uint)record.Length ? (int)length : 0;
    }

    private static bool TryGetMappingPairs(ReadOnlySpan<byte> attribute, out ReadOnlySpan<byte> runs)
    {
        runs = default;
        if (attribute.Length < 0x40)
        {
            return false;
        }

        var offset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x20..]);
        if (offset < 0x40 || offset >= attribute.Length)
        {
            return false;
        }

        runs = attribute[offset..];
        return true;
    }

    internal static bool TryGetResidentValue(ReadOnlySpan<byte> attribute, out ReadOnlySpan<byte> value, out int offset)
    {
        value = default;
        offset = 0;
        if (attribute.Length < 0x18)
        {
            return false;
        }

        offset = BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x14..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(attribute[0x10..]);
        if (offset < 0x18 || offset > attribute.Length || length > (uint)(attribute.Length - offset))
        {
            return false;
        }

        value = attribute.Slice(offset, (int)length);
        return true;
    }

    private static void ReadStandardInformation(ReadOnlySpan<byte> attribute, ref MftRecordInfo info)
    {
        if (TryGetResidentValue(attribute, out var value, out _) && value.Length >= 0x10)
        {
            info.LastWriteFileTime = BinaryPrimitives.ReadInt64LittleEndian(value[0x08..]);
        }
    }

    private static void ReadFileName(ReadOnlySpan<byte> attribute, int attributeOffset, ref MftRecordInfo info, ref int bestNamespaceRank)
    {
        if (!TryGetResidentValue(attribute, out var value, out var contentOffset) || value.Length < 0x42)
        {
            return;
        }

        var nameLengthChars = value[0x40];
        var nameNamespace = value[0x41];
        if (nameLengthChars == 0 || nameLengthChars * 2 > value.Length - 0x42)
        {
            return;
        }

        var rank = NamespaceRank(nameNamespace);
        if (rank <= bestNamespaceRank)
        {
            return;
        }

        bestNamespaceRank = rank;
        info.HasName = true;
        var parentReference = BinaryPrimitives.ReadUInt64LittleEndian(value);
        info.ParentRecordNumber = parentReference & 0x0000FFFFFFFFFFFFUL;
        info.ParentRecordSequence = (ushort)(parentReference >> 48);
        info.NameOffsetInRecord = attributeOffset + contentOffset + 0x42;
        info.NameLengthChars = nameLengthChars;
        info.FileNameNamespace = nameNamespace;
    }

    private static void ReadUnnamedData(ReadOnlySpan<byte> attribute, bool nonResident, ref MftRecordInfo info)
    {
        if (!nonResident)
        {
            if (TryGetResidentValue(attribute, out var value, out _))
            {
                info.HasUnnamedData = true;
                info.DataSizesValid = true;
                info.DataRealSize = value.Length;
                info.DataAllocatedSize = value.Length;
            }

            return;
        }

        if (attribute.Length < 0x40)
        {
            return;
        }

        info.HasUnnamedData = true;
        var lowestVcn = BinaryPrimitives.ReadUInt64LittleEndian(attribute[0x10..]);
        if (lowestVcn != 0 || info.DataSizesValid)
        {
            return;
        }

        var allocated = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x28..]);
        var logical = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);
        if (allocated >= 0 && logical >= 0)
        {
            info.DataSizesValid = true;
            info.DataAllocatedSize = allocated;
            info.DataRealSize = logical;
        }
    }

    internal static int NamespaceRank(byte fileNameNamespace) => fileNameNamespace switch
    {
        NamespaceWin32 => 3,
        NamespaceWin32Dos => 3,
        NamespacePosix => 2,
        NamespaceDos => 1,
        _ => 0
    };

    /// <summary>Applies the update-sequence array fixups in place. Returns false on a torn record.</summary>

    private static bool ApplyFixups(Span<byte> record)
    {
        const int wordsPerSector = SectorSize / sizeof(ushort);
        const int headerBytes = 8;
        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4, 2));
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(6, 2));
        if (record.Length % SectorSize != 0 || usaCount != record.Length / SectorSize + 1 ||
            usaOffset < headerBytes || (usaOffset & 1) != 0 ||
            usaOffset + usaCount * sizeof(ushort) > SectorSize - sizeof(ushort))
        {
            return false;
        }

        // The sequence and sector tails use the same on-disk byte order.
        var words = MemoryMarshal.Cast<byte, ushort>(record);
        var fixups = words.Slice(usaOffset / sizeof(ushort), usaCount);
        var sequence = fixups[0];
        for (var sector = 1; sector < fixups.Length; sector++)
        {
            if (words[sector * wordsPerSector - 1] != sequence)
            {
                return false;
            }
        }

        for (var sector = 1; sector < fixups.Length; sector++)
        {
            words[sector * wordsPerSector - 1] = fixups[sector];
        }

        return true;
    }

    /// <summary>
    /// Validates + fixes up a record, then extracts the data-run extents of its unnamed non-resident
    /// <c>$DATA</c> attribute. Used on MFT record 0 to discover where the rest of the MFT lives.
    /// </summary>
    public static bool TryReadUnnamedDataRuns(Span<byte> record, List<(long Lcn, long ClusterCount)> extents)
    {
        extents.Clear();
        if (!TryPrepareRecord(record, out var prepared))
        {
            return false;
        }

        var attribute = FindUnnamedAttribute(prepared, AttrData, 0);
        return !attribute.IsEmpty && attribute[8] == 1 &&
            TryGetMappingPairs(attribute, out var runs) &&
            TryDecodeRunList(runs, extents, out _, allowSparse: false);
    }

    internal static bool TryPrepareRecord(Span<byte> record, out ReadOnlySpan<byte> prepared)
    {
        prepared = default;
        if (record.Length < 0x30 || !record[..4].SequenceEqual("FILE"u8))
        {
            return false;
        }

        var usedBytes = GetUsedRecordLength(record);
        if (usedBytes == 0 || !ApplyFixups(record))
        {
            return false;
        }

        prepared = record[..usedBytes];
        return true;
    }

    internal static ReadOnlySpan<byte> FindUnnamedAttribute(ReadOnlySpan<byte> record, uint type, long lowestVcn, ushort? instance = null)
    {
        var offset = (int)BinaryPrimitives.ReadUInt16LittleEndian(record[0x14..]);
        while (offset >= 0x18 && offset + 0x10 <= record.Length)
        {
            var attributeType = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (attributeType == AttrEnd)
            {
                break;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(record[(offset + 4)..]);
            if (length < 0x10 || length > (uint)(record.Length - offset))
            {
                break;
            }

            var attribute = record.Slice(offset, (int)length);
            if (attributeType == type && attribute[9] == 0 &&
                (instance is null || BinaryPrimitives.ReadUInt16LittleEndian(attribute[0x0E..]) == instance))
            {
                if ((attribute[8] == 0 && lowestVcn == 0) ||
                    (attribute[8] == 1 && attribute.Length >= 0x40 &&
                        BinaryPrimitives.ReadInt64LittleEndian(attribute[0x10..]) == lowestVcn))
                {
                    return attribute;
                }
            }

            offset += (int)length;
        }

        return [];
    }

    internal static bool TryReadMftRuns(ReadOnlySpan<byte> attribute, long lowestVcn, long volumeClusters,
        List<(long Lcn, long ClusterCount)> extents, out long clusters)
    {
        clusters = 0;
        if (attribute.Length < 0x40 || attribute[8] != 1 || !TryGetMappingPairs(attribute, out var runs))
        {
            return false;
        }

        var highestVcn = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x18..]);
        return highestVcn >= lowestVcn && highestVcn < volumeClusters &&
            TryDecodeRunList(runs, extents, out clusters, allowSparse: false, volumeClusters: volumeClusters) &&
            clusters == highestVcn - lowestVcn + 1;
    }

    /// <summary>
    /// Decodes an NTFS data-run list into absolute (LCN, clusterCount) extents. Used to locate the
    /// <c>$MFT</c>'s own clusters from record 0. Sparse runs (offset size 0) are skipped.
    /// </summary>
    public static bool TryDecodeRunList(ReadOnlySpan<byte> runList, List<(long Lcn, long ClusterCount)> extents) =>
        TryDecodeRunList(runList, extents, out _);

    private static bool TryDecodeRunList(
        ReadOnlySpan<byte> runList, List<(long Lcn, long ClusterCount)>? extents, out long allocatedClusters,
        bool allowSparse = true, long volumeClusters = long.MaxValue)
    {
        extents?.Clear();
        allocatedClusters = 0;
        var index = 0;
        long currentLcn = 0;

        while (index < runList.Length)
        {
            var header = runList[index++];
            if (header == 0)
            {
                return true;
            }

            var lengthSize = header & 0x0F;
            var offsetSize = header >> 4;
            var fieldBytes = lengthSize + offsetSize;
            if (lengthSize is < 1 or > 8 || offsetSize > 8 || fieldBytes > runList.Length - index)
            {
                return false;
            }

            var fields = runList[index..];
            index += fieldBytes;
            // A single load covers typical runs. The shifts discard any bytes from the next run.
            var usePackedRead = fieldBytes <= sizeof(ulong) && fields.Length >= sizeof(ulong);
            var packed = usePackedRead ? BinaryPrimitives.ReadUInt64LittleEndian(fields) : 0UL;
            var lengthShift = (sizeof(ulong) - lengthSize) * 8;
            var clusterCount = usePackedRead
                ? unchecked((long)((packed << lengthShift) >> lengthShift))
                : ReadUnsignedLittleEndian(fields[..lengthSize]);
            if (clusterCount <= 0)
            {
                return false;
            }

            if (offsetSize == 0)
            {
                if (!allowSparse)
                {
                    return false;
                }

                // Sparse run: no clusters on disk. Skip without moving the LCN cursor.
                continue;
            }

            if (clusterCount > volumeClusters - allocatedClusters)
            {
                return false;
            }

            var offsetShift = (sizeof(ulong) - offsetSize) * 8;
            var deltaLcn = usePackedRead
                ? unchecked((long)((packed >> (lengthSize * 8)) << offsetShift)) >> offsetShift
                : ReadSignedLittleEndian(fields.Slice(lengthSize, offsetSize));
            if (deltaLcn < -currentLcn || deltaLcn > volumeClusters - clusterCount - currentLcn)
            {
                return false;
            }

            currentLcn += deltaLcn;
            allocatedClusters += clusterCount;
            extents?.Add((currentLcn, clusterCount));
        }

        return false;
    }

    private static long ReadUnsignedLittleEndian(ReadOnlySpan<byte> bytes)
    {
        switch (bytes.Length)
        {
            case 1:
                return bytes[0];
            case 2:
                return BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            case 3:
                return (uint)bytes[2] << 16 | BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            case 4:
                return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            case 8:
                return BinaryPrimitives.ReadInt64LittleEndian(bytes);
        }

        long value = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        for (var i = 4; i < bytes.Length; i++)
        {
            value |= (long)bytes[i] << (8 * i);
        }

        return value;
    }

    private static long ReadSignedLittleEndian(ReadOnlySpan<byte> bytes)
    {
        var shift = (sizeof(long) - bytes.Length) * 8;
        return (ReadUnsignedLittleEndian(bytes) << shift) >> shift;
    }
}
