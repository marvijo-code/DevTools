using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace DevTools.Services.BiggestFiles.Ntfs;

internal readonly record struct NtfsBitmapAllocation(long InUseBytes, long MatchedBytes, long RecordedFreeBytes);

/// <summary>Tracks the union of physical clusters reported by valid, in-use MFT records.</summary>
internal sealed class NtfsVolumeBitmap
{
    private const int HeaderBytes = 16;
    private const int RequestBytes = 256 * 1024;
    private readonly long _clusters;
    private readonly long[] _observed;

    internal delegate int Query(long startingLcn, byte[] output);

    private NtfsVolumeBitmap(long clusters, int words)
    {
        _clusters = clusters;
        _observed = new long[words];
    }

    internal static NtfsVolumeBitmap? TryCreate(long clusters, long? memoryHeadroom)
    {
        if (clusters <= 0 || memoryHeadroom is not long available)
        {
            return null;
        }

        var words = (clusters - 1) / 64 + 1;
        return words <= Array.MaxLength && words <= available / 16 / sizeof(long)
            ? new NtfsVolumeBitmap(clusters, (int)words)
            : null;
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
        for (var word = first + 1; word < last; word++)
        {
            Interlocked.Exchange(ref _observed[word], -1L);
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
                if (bytes <= HeaderBytes || bytes > buffer.Length)
                {
                    return null;
                }

                var start = BinaryPrimitives.ReadInt64LittleEndian(buffer);
                var remaining = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(8));
                if (start < 0 || start > next || (start & 7) != 0 || remaining != _clusters - start)
                {
                    return null;
                }

                var end = start + Math.Min(remaining, (bytes - HeaderBytes) * 8L);
                if (end < _clusters)
                {
                    // Leave a partial word for the next reply, which can start at an earlier LCN.
                    end &= ~63L;
                }

                if (end <= next)
                {
                    return null;
                }

                var payload = buffer.AsSpan(HeaderBytes + (int)((next - start) / 8));
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
