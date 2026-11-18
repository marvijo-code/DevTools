using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
            long unmatchedAllocated = 0;
            long recordedFree = 0;
            while (next < _clusters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = query(next, buffer);
                if (!TryDescribeReply(buffer, bytes, _clusters, next, out var offset, out var end))
                {
                    return null;
                }

                var fullWords = (int)((end - next) >> 6);
                var fullBytes = fullWords * sizeof(ulong);
                var firstWord = (int)(next >> 6);
                var allocatedWords = MemoryMarshal.Cast<byte, ulong>(buffer.AsSpan(offset, fullBytes));
                var observedWords = _observed.AsSpan(firstWord, fullWords);
                var counts = CountWords(allocatedWords, observedWords);
                inUse += counts.InUse;
                unmatchedAllocated += counts.UnmatchedAllocated;
                recordedFree += counts.RecordedFree;

                var tailBits = (int)(end & 63);
                if (tailBits > 0)
                {
                    var allocated = ReadTailBits(buffer.AsSpan(offset + fullBytes, (tailBits + 7) / 8), tailBits);
                    var observed = (ulong)_observed[firstWord + fullWords];
                    inUse += BitOperations.PopCount(allocated);
                    unmatchedAllocated += BitOperations.PopCount(allocated & ~observed);
                    recordedFree += BitOperations.PopCount(observed & ~allocated);
                }

                next = end;
            }

            return new NtfsBitmapAllocation(
                checked(inUse * bytesPerCluster),
                checked((inUse - unmatchedAllocated) * bytesPerCluster),
                checked(recordedFree * bytesPerCluster));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static (long InUse, long UnmatchedAllocated, long RecordedFree) CountWords(
        ReadOnlySpan<ulong> allocatedWords, ReadOnlySpan<long> observedWords)
    {
        long inUse = 0;
        long unmatchedAllocated = 0;
        long recordedFree = 0;
        if (LooksMostlyMatching(allocatedWords, observedWords))
        {
            for (var index = 0; index < allocatedWords.Length; index++)
            {
                var allocated = LittleEndianWord(allocatedWords[index]);
                var observed = (ulong)observedWords[index];
                inUse += BitOperations.PopCount(allocated);
                if (allocated != observed)
                {
                    unmatchedAllocated += BitOperations.PopCount(allocated & ~observed);
                    recordedFree += BitOperations.PopCount(observed & ~allocated);
                }
            }

            return (inUse, unmatchedAllocated, recordedFree);
        }

        var vectorWords = Avx2.IsSupported ? allocatedWords.Length / Vector256<ulong>.Count * Vector256<ulong>.Count : 0;
        if (vectorWords > 0)
        {
            (inUse, unmatchedAllocated, recordedFree) = CountVectorWords(allocatedWords[..vectorWords], observedWords[..vectorWords]);
        }

        for (var index = vectorWords; index < allocatedWords.Length; index++)
        {
            var allocated = LittleEndianWord(allocatedWords[index]);
            var observed = (ulong)observedWords[index];
            inUse += BitOperations.PopCount(allocated);
            unmatchedAllocated += BitOperations.PopCount(allocated & ~observed);
            recordedFree += BitOperations.PopCount(observed & ~allocated);
        }

        return (inUse, unmatchedAllocated, recordedFree);
    }

    private static bool LooksMostlyMatching(ReadOnlySpan<ulong> allocatedWords, ReadOnlySpan<long> observedWords)
    {
        if (!BitConverter.IsLittleEndian || allocatedWords.Length < 64)
        {
            return true;
        }

        // Sample several short runs of words to choose a counting loop. Every bit is still counted.
        const int samples = 8;
        const int wordsPerSample = 4;
        var stride = (allocatedWords.Length - wordsPerSample) / (samples - 1);
        var differingSamples = 0;
        for (var sample = 0; sample < samples; sample++)
        {
            var index = sample * stride;
            var allocated = MemoryMarshal.AsBytes(allocatedWords.Slice(index, wordsPerSample));
            var observed = MemoryMarshal.AsBytes(observedWords.Slice(index, wordsPerSample));
            if (!allocated.SequenceEqual(observed) && ++differingSamples == 2)
            {
                return false;
            }
        }

        return true;
    }

    private static (long InUse, long UnmatchedAllocated, long RecordedFree) CountVectorWords(
        ReadOnlySpan<ulong> allocatedWords, ReadOnlySpan<long> observedWords)
    {
        var inUse = Vector256<ulong>.Zero;
        var unmatchedAllocated = Vector256<ulong>.Zero;
        var recordedFree = Vector256<ulong>.Zero;
        ref var allocatedStart = ref MemoryMarshal.GetReference(allocatedWords);
        ref var observedStart = ref MemoryMarshal.GetReference(observedWords);
        const int vectorsPerBatch = 16;

        for (var index = 0; index < allocatedWords.Length;)
        {
            var end = Math.Min(index + vectorsPerBatch * Vector256<ulong>.Count, allocatedWords.Length);
            var allocatedCounts = Vector256<byte>.Zero;
            var unmatchedCounts = Vector256<byte>.Zero;
            var freeCounts = Vector256<byte>.Zero;
            for (; index < end; index += Vector256<ulong>.Count)
            {
                var allocated = Vector256.LoadUnsafe(ref allocatedStart, (nuint)index).AsByte();
                var observed = Vector256.LoadUnsafe(ref observedStart, (nuint)index).AsByte();
                allocatedCounts += CountBitsPerByte(allocated);
                unmatchedCounts += CountBitsPerByte(Avx2.AndNot(observed, allocated));
                freeCounts += CountBitsPerByte(Avx2.AndNot(allocated, observed));
            }

            // A vector adds at most eight bits to a byte lane. Widen before reaching 256.
            inUse += Avx2.SumAbsoluteDifferences(allocatedCounts, Vector256<byte>.Zero).AsUInt64();
            unmatchedAllocated += Avx2.SumAbsoluteDifferences(unmatchedCounts, Vector256<byte>.Zero).AsUInt64();
            recordedFree += Avx2.SumAbsoluteDifferences(freeCounts, Vector256<byte>.Zero).AsUInt64();
        }

        return ((long)Vector256.Sum(inUse), (long)Vector256.Sum(unmatchedAllocated), (long)Vector256.Sum(recordedFree));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> CountBitsPerByte(Vector256<byte> value)
    {
        var counts = Vector256.Create(Vector128.Create((byte)0, 1, 1, 2, 1, 2, 2, 3, 1, 2, 2, 3, 2, 3, 3, 4));
        var mask = Vector256.Create((byte)15);
        var low = Avx2.Shuffle(counts, value & mask);
        var high = Avx2.Shuffle(counts, Avx2.ShiftRightLogical(value.AsUInt16(), 4).AsByte() & mask);
        return low + high;
    }

    private static ulong LittleEndianWord(ulong value) =>
        BitConverter.IsLittleEndian ? value : BinaryPrimitives.ReverseEndianness(value);

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

    private static ulong ReadTailBits(ReadOnlySpan<byte> payload, int bits)
    {
        ulong value = 0;
        for (var index = 0; index < (bits + 7) / 8; index++)
        {
            value |= (ulong)payload[index] << (8 * index);
        }

        return value & (ulong.MaxValue >> (64 - bits));
    }
}
