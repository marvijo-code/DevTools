using System.Buffers.Binary;
using DevTools.Services.BiggestFiles.Ntfs;
using DevTools.Services.FolderSizes;
using Xunit;

namespace DiskUsageAnalyzer.Tests.BiggestFiles;

public sealed class NtfsVolumeBitmapTests
{
    [Fact]
    public void Reconcile_CountsUniqueMatchesAcrossRoundedRepliesAndIgnoresPadding()
    {
        const int clusters = 133;
        const uint clusterBytes = 512;
        var map = Assert.IsType<NtfsVolumeBitmap>(NtfsVolumeBitmap.TryCreate(clusters, 1024 * 1024));
        map.Mark(10, 76);
        map.Mark(70, 20);
        map.Mark(124, 9);

        var allocated = new byte[17];
        SetBits(allocated, 2, 3);
        SetBits(allocated, 10, 58);
        SetBits(allocated, 80, 20);
        SetBits(allocated, 128, 8);

        var requests = new List<long>();
        var result = map.Reconcile(clusterBytes, (start, output) =>
        {
            requests.Add(start);
            return start == 0
                ? WriteReply(0, clusters, allocated.AsSpan(0, 9), output)
                : WriteReply(56, clusters - 56, allocated.AsSpan(7), output);
        }, CancellationToken.None);

        Assert.Equal([0L, 64L], requests);
        Assert.Equal(new NtfsBitmapAllocation(86 * clusterBytes, 73 * clusterBytes, 16 * clusterBytes), result);
    }

    [Fact]
    public void Reconcile_PausesAfterTheCurrentReplyAndCancelsBeforeTheNextRead()
    {
        const int clusters = 128;
        var map = Assert.IsType<NtfsVolumeBitmap>(NtfsVolumeBitmap.TryCreate(clusters, 1024 * 1024));
        var firstWord = new byte[8];
        using var cancellation = new CancellationTokenSource();
        IDisposable? priorityScan = null;
        try
        {
            Assert.ThrowsAny<OperationCanceledException>(() => map.Reconcile(512, (start, output) =>
            {
                Assert.Equal(0, start);
                priorityScan = ScanGate.Pause();
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
                return WriteReply(0, clusters, firstWord, output);
            }, cancellation.Token));
            Assert.NotNull(priorityScan);
        }
        finally
        {
            priorityScan?.Dispose();
        }
    }

    [Theory]
    [InlineData(8, 125, 17)]
    [InlineData(0, 134, 17)]
    [InlineData(0, 133, 1)]
    public void Reconcile_RejectsMissingClustersMismatchedGeometryOrNoProgress(long start, long clusters, int bytes)
    {
        var map = Assert.IsType<NtfsVolumeBitmap>(NtfsVolumeBitmap.TryCreate(133, 1024 * 1024));
        var payload = new byte[bytes];

        Assert.Null(map.Reconcile(512, (_, output) => WriteReply(start, clusters, payload, output), CancellationToken.None));
        Assert.Null(map.Reconcile(512, (_, _) => 0, CancellationToken.None));
    }

    [Fact]
    public void Reconcile_PreservesBitsMarkedByConcurrentParsers()
    {
        const int clusters = 256;
        const uint clusterBytes = 512;
        var map = Assert.IsType<NtfsVolumeBitmap>(NtfsVolumeBitmap.TryCreate(clusters, 1024 * 1024));
        Parallel.For(0, 128, index =>
        {
            map.Mark(index % 32, 1);
            map.Mark(224 + index % 32, 1);
            map.Mark(96 + index % 32, 1);
            if (index % 8 == 0)
            {
                map.Mark(32, 192);
            }
        });

        var payload = new byte[clusters / 8];
        payload.AsSpan().Fill(byte.MaxValue);

        Assert.Equal(new NtfsBitmapAllocation(clusters * clusterBytes, clusters * clusterBytes, 0),
            map.Reconcile(clusterBytes, (_, output) => WriteReply(0, clusters, payload, output), CancellationToken.None));
    }

    [Theory]
    [InlineData(32)]
    [InlineData(1032)]
    public void Reconcile_CountsDiscrepanciesInMixedWordsAndTheFinalPartialWord(int replyBytes)
    {
        const int clusters = 26315;
        var map = Assert.IsType<NtfsVolumeBitmap>(NtfsVolumeBitmap.TryCreate(clusters, 1024 * 1024));
        var payload = new byte[(clusters + 7) / 8];
        var inUse = 0;
        var matched = 0;
        var recordedFree = 0;
        for (var cluster = 0; cluster < clusters; cluster++)
        {
            var observed = cluster % 13 < 5;
            var allocated = cluster < 8192 ? observed : cluster % 17 < 7;
            if (observed)
            {
                map.Mark(cluster, 1);
            }

            if (allocated)
            {
                SetBits(payload, cluster, 1);
                inUse++;
                matched += observed ? 1 : 0;
            }
            else if (observed)
            {
                recordedFree++;
            }
        }

        payload[^1] |= 0b1111_1000;
        var result = map.Reconcile(1, (start, output) =>
        {
            var offset = (int)(start / 8);
            return WriteReply(start, clusters - start, payload.AsSpan(offset, Math.Min(replyBytes, payload.Length - offset)), output);
        }, CancellationToken.None);

        Assert.Equal(new NtfsBitmapAllocation(inUse, matched, recordedFree), result);
    }

    [Fact]
    public void TryCreate_RespectsMemoryBudget()
    {
        Assert.Null(NtfsVolumeBitmap.TryCreate(4096, null));
        Assert.Null(NtfsVolumeBitmap.TryCreate(4096, 4096));
        Assert.NotNull(NtfsVolumeBitmap.TryCreate(4096, 8192));
    }

    [Fact]
    public void TryCreate_ProbesBeforeTrackingAndRejectsAnInvalidReply()
    {
        var calls = 0;
        Assert.Null(NtfsVolumeBitmap.TryCreate(133, 1 << 20, (start, output) =>
        {
            Assert.Equal(0, start);
            calls++;
            return WriteReply(0, 134, new byte[17], output);
        }));

        Assert.Equal(1, calls);
        Assert.Null(NtfsVolumeBitmap.TryCreate(4096, 4096, (_, _) => throw new InvalidOperationException()));
    }

    internal static void SetBits(byte[] bytes, int first, int count)
    {
        for (var bit = first; bit < first + count; bit++)
        {
            bytes[bit >> 3] |= (byte)(1 << (bit & 7));
        }
    }

    internal static int WriteReply(long first, long clusters, ReadOnlySpan<byte> payload, byte[] output)
    {
        BinaryPrimitives.WriteInt64LittleEndian(output, first);
        BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(8), clusters);
        payload.CopyTo(output.AsSpan(16));
        return 16 + payload.Length;
    }
}
