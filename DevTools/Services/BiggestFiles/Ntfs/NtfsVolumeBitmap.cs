using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace DevTools.Services.BiggestFiles.Ntfs;

internal readonly record struct NtfsBitmapAllocation(
    long InUseBytes, long MatchedBytes, long RecordedFreeBytes, UInt128 RepeatedReferenceBytes = default);

/// <summary>Tracks the union of physical clusters reported by valid, in-use MFT records.</summary>
internal sealed class NtfsVolumeBitmap
{
    private const int HeaderBytes = 16;
    private const int RequestBytes = 256 * 1024;
    private const int ProbeBytes = HeaderBytes + 4096;
    private readonly long _clusters;
    private readonly long[] _observed;

    internal delegate int Query(long startingLcn, byte[] output);

    private NtfsVolumeBitmap(long clusters, int words)
    {
        _clusters = clusters;
        _observed = new long[words];
    }

    internal static NtfsVolumeBitmap? TryCreate(long clusters, long? memoryHeadroom, Query? query = null,
        CancellationToken cancellationToken = default)
    {
        if (clusters <= 0 || memoryHeadroom is not long available)
        {
            return null;
        }

        var words = (clusters - 1) / 64 + 1;
        if (words > Array.MaxLength || words > available / 16 / sizeof(long))
        {
            return null;
        }

        if (query is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = ArrayPool<byte>.Shared.Rent(ProbeBytes);
            try
            {
                if (!TryDescribeReply(probe, query(0, probe), clusters, 0, out _, out _))
                {
                    return null;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(probe);
            }
        }

        return new NtfsVolumeBitmap(clusters, (int)words);
    }

    internal void Mark(long lcn, long count)
    {
        // The run-list parser has already checked both ends against this volume.
        var lastCluster = lcn + count - 1;
        var first = (int)(lcn >> 6);
        var last = (int)(lastCluster >> 6);
        var firstMask = -1L << (int)(lcn & 63);
        var lastMask = (long)(ulong.MaxValue >> (63 - (int)(lastCluster & 63)));
        if (first == last)
        {
            Interlocked.Or(ref _observed[first], firstMask & lastMask);
            return;
        }

        Interlocked.Or(ref _observed[first], firstMask);
        // A full word sets every bit, including any bits set by concurrent partial-word updates.
        for (var word = first + 1; word < last; word++)
        {
            Volatile.Write(ref _observed[word], -1L);
        }

        Interlocked.Or(ref _observed[last], lastMask);
    }

    internal NtfsBitmapAllocation? Reconcile(uint bytesPerCluster, Query query, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(RequestBytes);
        try
        {
            long next = 0;
            long inUse = 0;
            long matched = 0;
            long recordedFree = 0;
            while (next < _clusters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = query(next, buffer);
                if (!TryDescribeReply(buffer, bytes, _clusters, next, out var offset, out var end))
                {
                    return null;
                }

                var payload = buffer.AsSpan(offset, bytes - offset);
                while (next < end)
                {
                    var bits = (int)Math.Min(64, end - next);
                    var allocated = ReadBits(payload, bits);
                    var observed = (ulong)_observed[(int)(next >> 6)];
                    inUse += BitOperations.PopCount(allocated);
                    matched += BitOperations.PopCount(allocated & observed);
                    recordedFree += BitOperations.PopCount(observed & ~allocated);
                    payload = payload[((bits + 7) / 8)..];
                    next += bits;
                }
            }

            return new NtfsBitmapAllocation(
                checked(inUse * bytesPerCluster), checked(matched * bytesPerCluster), checked(recordedFree * bytesPerCluster));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryDescribeReply(byte[] buffer, int bytes, long clusters, long next, out int offset, out long end)
    {
        offset = 0;
        end = 0;
        if (bytes <= HeaderBytes || bytes > buffer.Length)
        {
            return false;
        }

        var start = BinaryPrimitives.ReadInt64LittleEndian(buffer);
        var remaining = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8));
        if (start < 0 || start > next || (start & 7) != 0 || remaining != clusters - start)
        {
            return false;
        }

        end = start + Math.Min(remaining, (bytes - HeaderBytes) * 8L);
        if (end < clusters)
        {
            // Leave a partial word for the next reply, which can start at an earlier LCN.
            end &= ~63L;
        }

        if (end <= next)
        {
            return false;
        }

        offset = HeaderBytes + (int)((next - start) / 8);
        return true;
    }

    private static ulong ReadBits(ReadOnlySpan<byte> payload, int bits)
    {
        if (bits == 64)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(payload);
        }

        ulong value = 0;
        for (var index = 0; index < (bits + 7) / 8; index++)
        {
            value |= (ulong)payload[index] << (8 * index);
        }

        return value & (ulong.MaxValue >> (64 - bits));
    }
}
