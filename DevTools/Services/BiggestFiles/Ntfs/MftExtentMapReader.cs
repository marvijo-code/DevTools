using System.Buffers;
using System.Buffers.Binary;
using DevTools.Services.FolderSizes;

namespace DevTools.Services.BiggestFiles.Ntfs;

internal static class MftExtentMapReader
{
    private const ulong RecordNumberMask = 0x0000FFFFFFFFFFFF;
    private const int AttributeListEntryHeader = 0x1A;

    internal delegate void ReadAt(long offset, Span<byte> destination);

    internal static List<(long Lcn, long ClusterCount)> Read(NtfsVolumeData geometry, ReadAt readAt, CancellationToken cancellationToken)
    {
        var recordSize = (int)geometry.BytesPerFileRecordSegment;
        var clusterSize = (int)geometry.BytesPerCluster;
        var readSize = ((recordSize - 1) / clusterSize + 1) * clusterSize;
        var buffer = ArrayPool<byte>.Shared.Rent(readSize);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            readAt(geometry.MftStartLcn * clusterSize, buffer.AsSpan(0, readSize));
            if (!MftRecordParser.TryPrepareRecord(buffer.AsSpan(0, recordSize), out var record) ||
                !IsInUse(record) || BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]) != 0)
            {
                throw new IOException("The first NTFS master file table record is invalid.");
            }

            var extents = new List<(long Lcn, long ClusterCount)>();
            var data = MftRecordParser.FindUnnamedAttribute(record, MftRecordParser.AttrData, 0);
            if (!MftRecordParser.TryReadMftRuns(data, 0, geometry.TotalClusters, extents, out var covered) ||
                extents[0].Lcn != geometry.MftStartLcn || extents[0].ClusterCount < readSize / clusterSize)
            {
                throw new IOException("The first NTFS master file table extent map is invalid.");
            }

            var required = (geometry.MftValidDataLength - 1) / clusterSize + 1;
            if (covered >= required)
            {
                return extents;
            }

            var baseSequence = BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..]);
            var baseInstance = BinaryPrimitives.ReadUInt16LittleEndian(data[0x0E..]);
            var list = MftRecordParser.FindUnnamedAttribute(record, MftRecordParser.AttrAttributeList, 0);
            var scratch = ArrayPool<byte>.Shared.Rent(clusterSize);
            try
            {
                AppendContinuations(list, baseSequence, baseInstance, geometry, extents, covered, required,
                    buffer.AsSpan(0, recordSize), scratch.AsSpan(0, clusterSize), readAt, cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(scratch);
            }

            return extents;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void AppendContinuations(ReadOnlySpan<byte> attribute, ushort baseSequence, ushort baseInstance,
        NtfsVolumeData geometry, List<(long Lcn, long ClusterCount)> extents, long covered, long required,
        Span<byte> recordBuffer, Span<byte> scratch, ReadAt readAt, CancellationToken cancellationToken)
    {
        if (attribute.IsEmpty || baseSequence == 0)
        {
            throw new IOException("The fragmented NTFS master file table has no valid attribute list.");
        }

        var baseReference = (ulong)baseSequence << 48;
        var references = ReadAttributeList(attribute, geometry, scratch, readAt, cancellationToken);
        if (references.Count == 0 || references[0] != new AttributeReference(0, baseReference, baseInstance))
        {
            throw new IOException("The NTFS attribute list does not identify the base master file table record.");
        }

        var runs = new List<(long Lcn, long ClusterCount)>();
        for (var index = 1; index < references.Count && covered < required; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = references[index];
            var recordNumber = reference.RecordReference & RecordNumberMask;
            var sequence = (ushort)(reference.RecordReference >> 48);
            if (reference.LowestVcn != covered || recordNumber == 0 || sequence == 0 ||
                recordNumber >= (ulong)(geometry.MftValidDataLength / recordBuffer.Length))
            {
                throw new IOException("An NTFS master file table continuation has an invalid reference.");
            }

            var recordOffset = (long)recordNumber * recordBuffer.Length;
            if (recordOffset > covered * geometry.BytesPerCluster - recordBuffer.Length)
            {
                throw new IOException("An NTFS master file table continuation lies outside the mapped extents.");
            }

            new MappedReader(extents, (int)geometry.BytesPerCluster).Read(recordOffset, recordBuffer, scratch, readAt, cancellationToken);
            if (!MftRecordParser.TryPrepareRecord(recordBuffer, out var record) || !IsInUse(record) ||
                BinaryPrimitives.ReadUInt16LittleEndian(record[0x10..]) != sequence ||
                BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]) != baseReference)
            {
                throw new IOException("The NTFS master file table continuation no longer matches its attribute-list reference.");
            }

            var data = MftRecordParser.FindUnnamedAttribute(record, MftRecordParser.AttrData, covered, reference.Instance);
            if (!MftRecordParser.TryReadMftRuns(data, covered, geometry.TotalClusters, runs, out var clusters))
            {
                throw new IOException("The NTFS master file table continuation has an invalid extent map.");
            }

            extents.AddRange(runs);
            covered += clusters;
        }

        if (covered < required)
        {
            throw new IOException("The NTFS extent map does not cover the valid master file table data.");
        }
    }

    private static List<AttributeReference> ReadAttributeList(ReadOnlySpan<byte> attribute, NtfsVolumeData geometry,
        Span<byte> scratch, ReadAt readAt, CancellationToken cancellationToken)
    {
        if (attribute[8] == 0 && MftRecordParser.TryGetResidentValue(attribute, out var resident, out _))
        {
            return ReadReferences(resident, cancellationToken);
        }

        var runs = new List<(long Lcn, long ClusterCount)>();
        if (!MftRecordParser.TryReadMftRuns(attribute, 0, geometry.TotalClusters, runs, out var clusters))
        {
            throw new IOException("The NTFS master file table attribute list has an invalid extent map.");
        }

        var bytes = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x30..]);
        var initialized = BinaryPrimitives.ReadInt64LittleEndian(attribute[0x38..]);
        var bufferBudget = (ScanResources.Capture().MemoryHeadroom ?? 64L * 1024 * 1024) / 16;
        if (bytes <= 0 || bytes > initialized || bytes > clusters * geometry.BytesPerCluster ||
            bytes > geometry.MftValidDataLength || bytes > bufferBudget || bytes > Array.MaxLength)
        {
            throw new IOException("The NTFS master file table attribute list has an unsupported size.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent((int)bytes);
        try
        {
            var value = buffer.AsSpan(0, (int)bytes);
            new MappedReader(runs, (int)geometry.BytesPerCluster).Read(0, value, scratch, readAt, cancellationToken);
            return ReadReferences(value, cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static List<AttributeReference> ReadReferences(ReadOnlySpan<byte> value, CancellationToken cancellationToken)
    {
        var references = new List<AttributeReference>();
        while (!value.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (value.Length < AttributeListEntryHeader)
            {
                throw new IOException("The NTFS master file table attribute list is truncated.");
            }

            var length = BinaryPrimitives.ReadUInt16LittleEndian(value[4..]);
            if (length < AttributeListEntryHeader || length > value.Length || (length & 7) != 0)
            {
                throw new IOException("The NTFS master file table attribute list entry has an invalid length.");
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(value) == MftRecordParser.AttrData && value[6] == 0)
            {
                references.Add(new AttributeReference(BinaryPrimitives.ReadInt64LittleEndian(value[8..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(value[0x10..]), BinaryPrimitives.ReadUInt16LittleEndian(value[0x18..])));
            }

            value = value[length..];
        }

        references.Sort(static (left, right) => left.LowestVcn.CompareTo(right.LowestVcn));
        return references;
    }

    internal struct MappedReader(List<(long Lcn, long ClusterCount)> extents, int clusterSize)
    {
        private int _extentIndex;
        private long _extentStart;

        internal void Read(long fileOffset, Span<byte> destination, Span<byte> scratch, ReadAt readAt,
            CancellationToken cancellationToken)
        {
            fileOffset -= _extentStart;
            if (fileOffset < 0)
            {
                fileOffset += _extentStart;
                _extentIndex = 0;
                _extentStart = 0;
            }

            for (var index = _extentIndex; index < extents.Count; index++)
            {
                var (lcn, clusterCount) = extents[index];
                var extentBytes = clusterCount * clusterSize;
                if (fileOffset >= extentBytes)
                {
                    // The next batch can overlap this read, so keep its starting extent.
                    _extentIndex = index + 1;
                    _extentStart += extentBytes;
                    fileOffset -= extentBytes;
                    continue;
                }

                var offset = lcn * clusterSize + fileOffset;
                var remaining = (int)Math.Min(destination.Length, extentBytes - fileOffset);
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var withinCluster = (int)(offset % clusterSize);
                    int count;
                    if (withinCluster == 0 && remaining >= clusterSize)
                    {
                        count = remaining / clusterSize * clusterSize;
                        readAt(offset, destination[..count]);
                    }
                    else
                    {
                        count = Math.Min(remaining, clusterSize - withinCluster);
                        readAt(offset - withinCluster, scratch[..clusterSize]);
                        scratch.Slice(withinCluster, count).CopyTo(destination);
                    }

                    destination = destination[count..];
                    offset += count;
                    remaining -= count;
                }

                if (destination.IsEmpty)
                {
                    return;
                }

                fileOffset = 0;
            }

            throw new IOException("An NTFS master file table continuation lies outside the mapped extents.");
        }
    }

    private static bool IsInUse(ReadOnlySpan<byte> record) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(record[0x16..]) & 1) != 0;

    private readonly record struct AttributeReference(long LowestVcn, ulong RecordReference, ushort Instance);
}
