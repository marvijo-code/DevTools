using System.Buffers.Binary;

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

    public bool HasName;

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
    private const uint AttrAttributeList = 0x20;
    private const uint AttrFileName = 0x30;
    private const uint AttrData = 0x80;
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
    public static bool TryParseRecord(Span<byte> record, ref MftRecordInfo info)
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

        if (!ApplyFixups(record))
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x16, 2));
        info.InUse = (flags & 0x0001) != 0;
        info.IsDirectory = (flags & 0x0002) != 0;
        info.SequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x10, 2));
        info.BaseRecordNumber = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(0x20, 8)) & 0x0000FFFFFFFFFFFFUL;

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
            if (attributeLength < 0x10 || offset + attributeLength > record.Length)
            {
                break;
            }

            var nonResident = record[offset + 8] != 0;
            var nameLength = record[offset + 9];

            switch (attributeType)
            {
                case AttrStandardInformation when !nonResident:
                    ReadStandardInformation(record, offset, ref info);
                    break;
                case AttrAttributeList:
                    info.HasAttributeList = true;
                    break;
                case AttrFileName when !nonResident:
                    ReadFileName(record, offset, ref info, ref bestNamespaceRank);
                    break;
                case AttrData when nameLength == 0:
                    ReadUnnamedData(record, offset, nonResident, ref info);
                    break;
            }

            offset += attributeLength;
        }

        return true;
    }

    private static void ReadStandardInformation(ReadOnlySpan<byte> record, int attributeOffset, ref MftRecordInfo info)
    {
        var contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(attributeOffset + 0x14, 2));
        var start = attributeOffset + contentOffset;
        if (start + 0x18 > record.Length)
        {
            return;
        }

        // Offset 0x08 within $STANDARD_INFORMATION is the last data-modification time (FILETIME).
        info.LastWriteFileTime = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(start + 0x08, 8));
    }

    private static void ReadFileName(ReadOnlySpan<byte> record, int attributeOffset, ref MftRecordInfo info, ref int bestNamespaceRank)
    {
        var contentOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(attributeOffset + 0x14, 2));
        var start = attributeOffset + contentOffset;
        if (start + 0x42 > record.Length)
        {
            return;
        }

        var nameLengthChars = record[start + 0x40];
        var nameNamespace = record[start + 0x41];
        var nameStart = start + 0x42;
        if (nameStart + nameLengthChars * 2 > record.Length)
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
        info.ParentRecordNumber = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(start, 8)) & 0x0000FFFFFFFFFFFFUL;
        info.NameOffsetInRecord = nameStart;
        info.NameLengthChars = nameLengthChars;
        info.FileNameNamespace = nameNamespace;
    }

    private static void ReadUnnamedData(ReadOnlySpan<byte> record, int attributeOffset, bool nonResident, ref MftRecordInfo info)
    {
        if (!nonResident)
        {
            var contentLength = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(attributeOffset + 0x10, 4));
            info.HasUnnamedData = true;
            info.DataSizesValid = true;
            info.DataRealSize = contentLength;
            info.DataAllocatedSize = contentLength;
            return;
        }

        var lowestVcn = BinaryPrimitives.ReadUInt64LittleEndian(record.Slice(attributeOffset + 0x10, 8));
        info.HasUnnamedData = true;

        // Only the first fragment (LowestVCN == 0) carries valid allocated/real sizes.
        if (lowestVcn != 0 || info.DataSizesValid)
        {
            return;
        }

        info.DataSizesValid = true;
        info.DataAllocatedSize = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(attributeOffset + 0x28, 8));
        info.DataRealSize = BinaryPrimitives.ReadInt64LittleEndian(record.Slice(attributeOffset + 0x30, 8));
    }

    private static int NamespaceRank(byte fileNameNamespace) => fileNameNamespace switch
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
        var usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4, 2));
        var usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(6, 2));
        if (usaCount < 1)
        {
            return false;
        }

        var sectorCount = usaCount - 1;
        if (usaOffset + usaCount * 2 > record.Length ||
            sectorCount * SectorSize > record.Length)
        {
            return false;
        }

        var updateSequenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(usaOffset, 2));

        for (var sector = 0; sector < sectorCount; sector++)
        {
            var tail = sector * SectorSize + (SectorSize - 2);
            var current = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(tail, 2));
            if (current != updateSequenceNumber)
            {
                return false;
            }

            var replacement = record.Slice(usaOffset + 2 + sector * 2, 2);
            record[tail] = replacement[0];
            record[tail + 1] = replacement[1];
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

        if (record.Length < 0x30)
        {
            return false;
        }

        if (record[0] != (byte)'F' || record[1] != (byte)'I' || record[2] != (byte)'L' || record[3] != (byte)'E')
        {
            return false;
        }

        if (!ApplyFixups(record))
        {
            return false;
        }

        var firstAttributeOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(0x14, 2));
        var offset = (int)firstAttributeOffset;

        while (offset >= 0x18 && offset + 0x10 <= record.Length)
        {
            var attributeType = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(offset, 4));
            if (attributeType == AttrEnd)
            {
                break;
            }

            var attributeLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(offset + 4, 4));
            if (attributeLength < 0x10 || offset + attributeLength > record.Length)
            {
                break;
            }

            var nonResident = record[offset + 8] != 0;
            var nameLength = record[offset + 9];

            if (attributeType == AttrData && nameLength == 0 && nonResident)
            {
                var runListOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(offset + 0x20, 2));
                if (runListOffset < 0x40 || offset + runListOffset > offset + attributeLength)
                {
                    return false;
                }

                var runs = record.Slice(offset + runListOffset, attributeLength - runListOffset);
                return TryDecodeRunList(runs, extents);
            }

            offset += attributeLength;
        }

        return false;
    }

    /// <summary>
    /// Decodes an NTFS data-run list into absolute (LCN, clusterCount) extents. Used to locate the
    /// <c>$MFT</c>'s own clusters from record 0. Sparse runs (offset size 0) are skipped.
    /// </summary>
    public static bool TryDecodeRunList(ReadOnlySpan<byte> runList, List<(long Lcn, long ClusterCount)> extents)
    {
        extents.Clear();
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
            var offsetSize = (header >> 4) & 0x0F;
            if (lengthSize == 0 || index + lengthSize + offsetSize > runList.Length)
            {
                return false;
            }

            var clusterCount = ReadUnsignedLittleEndian(runList.Slice(index, lengthSize));
            index += lengthSize;

            if (offsetSize == 0)
            {
                // Sparse run: no clusters on disk. Skip without moving the LCN cursor.
                continue;
            }

            var deltaLcn = ReadSignedLittleEndian(runList.Slice(index, offsetSize));
            index += offsetSize;
            currentLcn += deltaLcn;

            extents.Add((currentLcn, clusterCount));
        }

        return true;
    }

    private static long ReadUnsignedLittleEndian(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            value |= (long)bytes[i] << (8 * i);
        }

        return value;
    }

    private static long ReadSignedLittleEndian(ReadOnlySpan<byte> bytes)
    {
        long value = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            value |= (long)bytes[i] << (8 * i);
        }

        // Sign-extend from the top bit of the most significant byte.
        var signBit = 1L << (8 * bytes.Length - 1);
        if ((value & signBit) != 0)
        {
            value -= 1L << (8 * bytes.Length);
        }

        return value;
    }
}
