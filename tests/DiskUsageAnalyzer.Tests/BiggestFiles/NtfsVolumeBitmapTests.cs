using System.Buffers.Binary;
using DevTools.Services.BiggestFiles.Ntfs;
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
        var map = Assert.IsType<NtfsVolumeBitmap>(NtfsVolumeBitmap.TryCreate(64, 1024 * 1024));
        Parallel.For(0, 256, index => map.Mark(index % 64, 1));
        var payload = new byte[8];
        payload.AsSpan().Fill(byte.MaxValue);

        Assert.Equal(new NtfsBitmapAllocation(32768, 32768, 0),
            map.Reconcile(512, (_, output) => WriteReply(0, 64, payload, output), CancellationToken.None));
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
