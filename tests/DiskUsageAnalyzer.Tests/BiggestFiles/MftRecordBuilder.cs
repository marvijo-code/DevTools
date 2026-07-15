using System.Buffers.Binary;
using System.Text;

namespace DiskUsageAnalyzer.Tests.BiggestFiles;

/// <summary>
/// Builds synthetic 1024-byte NTFS FILE records in their on-disk (fixed-up) form so tests can
/// exercise <c>MftRecordParser</c> without touching a real volume. Spec-driven and independent of
/// the parser under test.
/// </summary>
internal sealed class MftRecordBuilder
{
    public const int RecordSize = 1024;
    private const int SectorSize = 512;
    private const int UsaOffset = 0x30;
    private const int UsaCount = 3; // 1 USN + 2 sector fixups
    private const int FirstAttributeOffset = 0x38;
    private const ushort UpdateSequenceNumber = 0x0001;

    private ushort _sequence = 1;
    private bool _inUse = true;
    private bool _isDirectory;
    private ulong _baseRecordNumber;
    private readonly List<FileNameAttr> _fileNames = [];
    private long? _lastWriteFileTime;
    private DataAttr? _data;
    private bool _addAttributeList;
    private bool _addNamedData;

    public MftRecordBuilder Sequence(ushort value) { _sequence = value; return this; }
    public MftRecordBuilder InUse(bool value) { _inUse = value; return this; }
    public MftRecordBuilder Directory(bool value) { _isDirectory = value; return this; }
    public MftRecordBuilder BaseRecord(ulong value) { _baseRecordNumber = value; return this; }
    public MftRecordBuilder AttributeList() { _addAttributeList = true; return this; }
    public MftRecordBuilder NamedData() { _addNamedData = true; return this; }
    public MftRecordBuilder LastWrite(long fileTime) { _lastWriteFileTime = fileTime; return this; }

    public MftRecordBuilder FileName(string name, ulong parent, byte nameNamespace)
    {
        _fileNames.Add(new FileNameAttr(name, parent, nameNamespace));
        return this;
    }

    public MftRecordBuilder ResidentData(long size)
    {
        _data = new DataAttr(false, size, size, 0);
        return this;
    }

    public MftRecordBuilder NonResidentData(long realSize, long allocatedSize, long lowestVcn = 0)
    {
        _data = new DataAttr(true, realSize, allocatedSize, lowestVcn);
        return this;
    }

    public byte[] Build()
    {
        var buffer = new byte[RecordSize];
        Encoding.ASCII.GetBytes("FILE").CopyTo(buffer, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x04), UsaOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x06), UsaCount);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x10), _sequence);

        ushort flags = 0;
        if (_inUse) flags |= 0x0001;
        if (_isDirectory) flags |= 0x0002;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x16), flags);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(0x14), FirstAttributeOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(0x20), _baseRecordNumber);

        var offset = FirstAttributeOffset;

        if (_lastWriteFileTime is not null)
        {
            offset = WriteStandardInformation(buffer, offset, _lastWriteFileTime.Value);
        }

        if (_addAttributeList)
        {
            offset = WriteAttributeListStub(buffer, offset);
        }

        foreach (var fileName in _fileNames)
        {
            offset = WriteFileName(buffer, offset, fileName);
        }

        if (_addNamedData)
        {
            offset = WriteNamedData(buffer, offset);
        }

        if (_data is not null)
        {
            offset = WriteData(buffer, offset, _data.Value);
        }

        // End marker.
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x18), (uint)(offset + 8)); // used size
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0x1C), RecordSize);          // allocated size

        ApplyOnDiskFixups(buffer);
        return buffer;
    }

    private static int WriteStandardInformation(byte[] buffer, int offset, long lastWrite)
    {
        const int contentLength = 0x30;
        WriteResidentHeader(buffer, offset, attributeType: 0x10, contentLength, out var contentOffset, out var attributeLength);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset + contentOffset + 0x08), lastWrite);
        return offset + attributeLength;
    }

    private static int WriteAttributeListStub(byte[] buffer, int offset)
    {
        // Minimal resident $ATTRIBUTE_LIST; the parser only needs to see its presence.
        WriteResidentHeader(buffer, offset, attributeType: 0x20, contentLength: 0x20, out _, out var attributeLength);
        return offset + attributeLength;
    }

    private static int WriteFileName(byte[] buffer, int offset, FileNameAttr fileName)
    {
        var nameBytes = Encoding.Unicode.GetBytes(fileName.Name);
        var contentLength = 0x42 + nameBytes.Length;
        WriteResidentHeader(buffer, offset, attributeType: 0x30, contentLength, out var contentOffset, out var attributeLength);

        var content = offset + contentOffset;
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(content), fileName.Parent);
        // Stale sizes NTFS keeps in $FILE_NAME — deliberately wrong so tests catch misuse.
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(content + 0x28), 999_999);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(content + 0x30), 999_999);
        buffer[content + 0x40] = (byte)fileName.Name.Length;
        buffer[content + 0x41] = fileName.Namespace;
        nameBytes.CopyTo(buffer, content + 0x42);
        return offset + attributeLength;
    }

    private static int WriteNamedData(byte[] buffer, int offset)
    {
        // Non-resident named $DATA (an alternate data stream) that the parser must ignore.
        var nameChars = "ads";
        var nameBytes = Encoding.Unicode.GetBytes(nameChars);
        var nameOffset = 0x40;
        var runListOffset = nameOffset + nameBytes.Length;
        runListOffset = Align8(runListOffset);
        var attributeLength = Align8(runListOffset + 1);

        WriteNonResidentHeader(buffer, offset, 0x80, (byte)nameChars.Length, (byte)nameOffset,
            realSize: 12345, allocatedSize: 16384, lowestVcn: 0, runListOffset: (ushort)runListOffset, attributeLength);
        nameBytes.CopyTo(buffer, offset + nameOffset);
        return offset + attributeLength;
    }

    private static int WriteData(byte[] buffer, int offset, DataAttr data)
    {
        if (!data.NonResident)
        {
            // Resident $DATA: the parser reports the content-length field as the file size.
            WriteResidentHeader(buffer, offset, attributeType: 0x80, (int)data.RealSize, out _, out var residentLength);
            return offset + residentLength;
        }

        const ushort runListOffset = 0x40;
        var attributeLength = Align8(runListOffset + 1);
        WriteNonResidentHeader(buffer, offset, 0x80, nameLength: 0, nameOffset: 0,
            data.RealSize, data.AllocatedSize, data.LowestVcn, runListOffset, attributeLength);
        return offset + attributeLength;
    }

    private static void WriteResidentHeader(byte[] buffer, int offset, uint attributeType, int contentLength, out int contentOffset, out int attributeLength)
    {
        contentOffset = 0x18;
        attributeLength = Align8(contentOffset + Math.Max(contentLength, 0));
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), attributeType);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 0x04), (uint)attributeLength);
        buffer[offset + 0x08] = 0; // resident
        buffer[offset + 0x09] = 0; // name length
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 0x0A), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 0x10), (uint)contentLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 0x14), (ushort)contentOffset);
    }

    private static void WriteNonResidentHeader(byte[] buffer, int offset, uint attributeType, byte nameLength, byte nameOffset,
        long realSize, long allocatedSize, long lowestVcn, ushort runListOffset, int attributeLength)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), attributeType);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 0x04), (uint)attributeLength);
        buffer[offset + 0x08] = 1; // non-resident
        buffer[offset + 0x09] = nameLength;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 0x0A), nameOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset + 0x10), (ulong)lowestVcn);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset + 0x18), 0); // highest VCN (unused by parser)
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 0x20), runListOffset);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset + 0x28), allocatedSize);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset + 0x30), realSize);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset + 0x38), realSize);
        // Minimal run list terminator.
        buffer[offset + runListOffset] = 0x00;
    }

    private static void ApplyOnDiskFixups(byte[] buffer)
    {
        // Save each sector's last two bytes into the USA, then stamp the USN into the sector tails.
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(UsaOffset), UpdateSequenceNumber);
        for (var sector = 0; sector < UsaCount - 1; sector++)
        {
            var tail = sector * SectorSize + (SectorSize - 2);
            var usaEntry = UsaOffset + 2 + sector * 2;
            buffer[usaEntry] = buffer[tail];
            buffer[usaEntry + 1] = buffer[tail + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(tail), UpdateSequenceNumber);
        }
    }

    /// <summary>Corrupts a sector tail so the fixup check must reject the record as torn.</summary>
    public static void CorruptSectorTail(byte[] buffer, int sector)
    {
        var tail = sector * SectorSize + (SectorSize - 2);
        buffer[tail] = 0xAB;
        buffer[tail + 1] = 0xCD;
    }

    private static int Align8(int value) => (value + 7) & ~7;

    private readonly record struct FileNameAttr(string Name, ulong Parent, byte Namespace);
    private readonly record struct DataAttr(bool NonResident, long RealSize, long AllocatedSize, long LowestVcn);
}
