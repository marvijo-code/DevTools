using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DevTools.Infrastructure;

namespace DevTools.Services.FolderSizes;

public sealed record FolderIndexMetadata(
    DateTimeOffset SizedAt,
    ulong TreeHash,
    ulong UsnJournalId,
    long NextUsn,
    long LowestValidUsn);

/// <summary>
/// Persists a whole-drive <see cref="FolderTree"/> with a trailing SHA-256 integrity hash and
/// optional NTFS USN Journal cursor so relaunches and refreshes can verify or incrementally update
/// the index in milliseconds instead of rescanning the entire volume.
/// </summary>
public static class FolderIndexCache
{
    public const int FormatVersion = 2;

    private const string ApplicationFolder = "DevTools";
    private const string IndexFolder = "index";
    private const int Sha256Length = 32;

    private static ReadOnlySpan<byte> MagicBytes => "DTIX"u8;

    private static readonly object Gate = new();
    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromMinutes(5);

    private static string _cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationFolder,
        IndexFolder);

    static FolderIndexCache()
    {
        if (!BitConverter.IsLittleEndian)
        {
            throw new PlatformNotSupportedException("The folder index cache requires a little-endian machine.");
        }
    }

    internal static string CacheDirectory
    {
        get => _cacheDirectory;
        set => _cacheDirectory = value;
    }

    /// <summary>How old a cached tree may be before a cold rescan is required. Default 24 hours.</summary>
    public static TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(24);

    internal static IReadOnlyCollection<string> AcceptableEngines { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "NTFS MFT",
            ParallelFolderWalker.EngineName,
            "Rust fast walk",
            "NTFS MFT (USN incremental)"
        };

    public static bool IsAcceptableEngine(string? engineName) =>
        !string.IsNullOrWhiteSpace(engineName) && AcceptableEngines.Contains(engineName);

    public static bool TryLoad(
        string drive,
        [NotNullWhen(true)] out FolderTree? tree,
        out FolderIndexMetadata? metadata)
    {
        tree = null;
        metadata = null;

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        string path;
        uint serial;
        string root;
        try
        {
            if (!TryResolveVolumeRoot(drive, out root))
            {
                return false;
            }

            var foundSerial = TryGetVolumeSerial(root);
            if (foundSerial is null)
            {
                return false;
            }

            serial = foundSerial.Value;
            path = GetCachePath(root, serial);
        }
        catch (Exception ex)
        {
            AppLog.Info($"Folder index cache: cannot resolve cache for drive '{drive}': {ex.Message}");
            return false;
        }

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            var now = DateTime.UtcNow;
            var written = File.GetLastWriteTimeUtc(path);
            if (now - written > MaxAge)
            {
                AppLog.Info($"Folder index cache {path} is older than {MaxAge.TotalHours:F0}h; rescanning.");
                return false;
            }

            if (written - now > ClockSkewTolerance)
            {
                AppLog.Info($"Folder index cache {path} has a future timestamp; rescanning.");
                return false;
            }

            var loaded = ReadTree(path, root, serial, now, out metadata);
            if (loaded is null)
            {
                return false;
            }

            tree = loaded;
            return true;
        }
        catch (Exception ex) when (IsCorrupt(ex))
        {
            DeleteQuietly(path);
            AppLog.Info($"Folder index cache {path} failed verification ({ex.Message}); deleted it and rescanning.");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Info($"Folder index cache {path} could not be read ({ex.Message}); rescanning.");
            return false;
        }
    }

    public static bool Save(string drive, FolderTree tree, UsnJournalCursor? usnCursor = null, ulong treeHash = 0)
    {
        if (!OperatingSystem.IsWindows() || tree is null || tree.FolderCount == 0)
        {
            return false;
        }

        string path;
        string root;
        uint serial;
        try
        {
            if (!TryResolveVolumeRoot(drive, out root))
            {
                return false;
            }

            var foundSerial = TryGetVolumeSerial(root);
            if (foundSerial is null)
            {
                return false;
            }

            serial = foundSerial.Value;
            path = GetCachePath(root, serial);
        }
        catch (Exception ex)
        {
            AppLog.Info($"Folder index cache: cannot resolve cache for drive '{drive}': {ex.Message}");
            return false;
        }

        lock (Gate)
        {
            var temp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var computedHash = treeHash != 0 ? treeHash : ComputeTreeHash(tree);
                using (var fileStream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
                using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                using (var hashingStream = new HashingWriteStream(fileStream, hasher))
                {
                    WriteTree(hashingStream, tree, root, serial, DateTimeOffset.UtcNow, usnCursor, computedHash);
                    hashingStream.Flush();
                    var digest = hasher.GetHashAndReset();
                    fileStream.Write(digest, 0, digest.Length);
                }

                File.Move(temp, path, overwrite: true);
                AppLog.Info($"Folder index cache wrote {path} ({tree.FolderCount:N0} folders, {new FileInfo(path).Length / 1024d / 1024d:F1} MB, hash={computedHash:X16}).");
                return true;
            }
            catch (Exception ex)
            {
                DeleteQuietly(temp);
                AppLog.Info($"Folder index cache could not write {path}: {ex.Message}");
                return false;
            }
        }
    }

    public static void SaveInBackground(string drive, FolderTree tree, UsnJournalCursor? usnCursor = null, ulong treeHash = 0)
    {
        _ = Task.Run(() => Save(drive, tree, usnCursor, treeHash));
    }

    internal static string GetCachePath(string drive)
    {
        var root = FolderTree.NormalizeRoot(drive);
        var serial = TryGetVolumeSerial(root)
            ?? throw new InvalidOperationException($"Cannot read the volume serial of {root}.");
        return GetCachePath(root, serial);
    }

    private static bool TryResolveVolumeRoot(string drive, out string root)
    {
        root = string.Empty;
        if (string.IsNullOrWhiteSpace(drive))
        {
            return false;
        }

        var trimmed = drive.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        root = FolderTree.NormalizeRoot(trimmed);
        if (root.Length < 2 || root[1] != ':')
        {
            return false;
        }

        // Only cache actual volume roots (e.g. "C:" or "C:\"), never subdirectories such as temp test fixtures.
        var candidate = trimmed.TrimEnd(Path.DirectorySeparatorChar);
        return candidate.Length == 2 &&
               candidate[1] == ':' &&
               string.Equals(candidate + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCachePath(string root, uint serial) =>
        Path.Combine(CacheDirectory, $"drive-{char.ToLowerInvariant(root[0])}-{serial:X8}-v{FormatVersion}.bin");

    internal static ulong ComputeTreeHash(FolderTree tree)
    {
        const ulong fnvOffset = 0xCBF2_9CE4_8422_2325UL;
        const ulong fnvPrime = 0x0000_0100_0000_01B3UL;
        var hash = fnvOffset;
        var count = tree.FolderCount;
        var parents = tree.ParentNodes;
        var bytes = tree.TotalBytesNodes;
        var files = tree.FileCountNodes;
        hash = (hash ^ (ulong)count) * fnvPrime;
        var step = Math.Max(1, count / 4096);
        for (var i = 0; i < count; i += step)
        {
            hash = (hash ^ (ulong)(uint)parents[i]) * fnvPrime;
            hash = (hash ^ (ulong)bytes[i]) * fnvPrime;
            hash = (hash ^ (ulong)files[i]) * fnvPrime;
        }

        hash = (hash ^ (ulong)bytes[0]) * fnvPrime;
        hash = (hash ^ (ulong)files[0]) * fnvPrime;
        return hash;
    }

    private static void WriteTree(
        Stream stream,
        FolderTree tree,
        string root,
        uint serial,
        DateTimeOffset sizedAt,
        UsnJournalCursor? usnCursor,
        ulong treeHash)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(MagicBytes);
        writer.Write(FormatVersion);
        writer.Write(serial);
        writer.Write(sizedAt.UtcDateTime.Ticks);
        writer.Write(tree.Elapsed.Ticks);
        writer.Write(tree.FolderCount);
        writer.Write(treeHash);
        writer.Write(usnCursor?.JournalId ?? 0UL);
        writer.Write(usnCursor?.NextUsn ?? 0L);
        writer.Write(usnCursor?.LowestValidUsn ?? 0L);
        writer.Write(tree.IncompleteDirectoryCount);
        writer.Write(tree.IncompleteAllocationDirectoryCount);

        var allocated = tree.AllocatedBytesNodes;
        var ntfs = tree.NtfsAllocation;
        var flags = (byte)0;
        if (allocated is not null) flags |= 0x01;
        if (ntfs.HasValue) flags |= 0x02;
        if (tree.IncompleteSubtreesBits is not null) flags |= 0x04;
        if (tree.IncompleteAllocationSubtreesBits is not null) flags |= 0x08;
        writer.Write(flags);

        WriteString(writer, root);
        WriteString(writer, tree.EngineName ?? string.Empty);

        WriteBuffer(writer, MemoryMarshal.AsBytes(tree.ParentNodes.AsSpan()));
        WriteBuffer(writer, MemoryMarshal.AsBytes(tree.TotalBytesNodes.AsSpan()));
        WriteBuffer(writer, MemoryMarshal.AsBytes(tree.FileCountNodes.AsSpan()));

        if (allocated is not null)
        {
            WriteBuffer(writer, MemoryMarshal.AsBytes(allocated.AsSpan()));
        }

        if (ntfs is { } ntfsAlloc)
        {
            writer.Write(ntfsAlloc.ReservedBytes);
            writer.Write(ntfsAlloc.UnlinkedBytes);
            writer.Write(ntfsAlloc.IncompleteRecordCount);
        }

        if (tree.IncompleteSubtreesBits is { } incBits)
        {
            WriteBitArray(writer, incBits);
        }

        if (tree.IncompleteAllocationSubtreesBits is { } incAllocBits)
        {
            WriteBitArray(writer, incAllocBits);
        }

        var names = tree.NameNodes;
        var offsets = new int[names.Length + 1];
        using var blob = new MemoryStream(names.Length * 12);
        for (var i = 0; i < names.Length; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(names[i] ?? string.Empty);
            blob.Write(bytes, 0, bytes.Length);
            offsets[i + 1] = (int)blob.Length;
        }

        writer.Write(offsets.Length);
        WriteBuffer(writer, MemoryMarshal.AsBytes(offsets.AsSpan()));
        writer.Write((int)blob.Length);
        writer.Flush();
        blob.Position = 0;
        blob.CopyTo(stream);
    }

    private static FolderTree? ReadTree(
        string path,
        string root,
        uint expectedSerial,
        DateTime now,
        out FolderIndexMetadata? metadata)
    {
        metadata = null;

        using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        if (fileStream.Length <= Sha256Length + 32)
        {
            throw new InvalidDataException("file too small");
        }

        // Verify trailing SHA-256 checksum over the entire payload first.
        var payloadLength = fileStream.Length - Sha256Length;
        VerifySha256(fileStream, payloadLength);

        fileStream.Position = 0;
        using var bounded = new BoundedReadStream(fileStream, payloadLength);
        using var reader = new BinaryReader(bounded, Encoding.UTF8, leaveOpen: true);

        Span<byte> magic = stackalloc byte[4];
        ReadExact(reader, magic);
        if (!magic.SequenceEqual(MagicBytes))
        {
            throw new InvalidDataException("bad magic");
        }

        var version = reader.ReadInt32();
        if (version != FormatVersion)
        {
            throw new InvalidDataException($"format version {version} is not {FormatVersion}");
        }

        var serial = reader.ReadUInt32();
        if (serial != expectedSerial)
        {
            return null;
        }

        var sizedAtTicks = reader.ReadInt64();
        var elapsedTicks = reader.ReadInt64();
        var count = reader.ReadInt32();
        if (count <= 0)
        {
            throw new InvalidDataException("empty tree");
        }

        var treeHash = reader.ReadUInt64();
        var usnJournalId = reader.ReadUInt64();
        var nextUsn = reader.ReadInt64();
        var lowestValidUsn = reader.ReadInt64();
        var incompleteDirCount = reader.ReadInt32();
        var incompleteAllocDirCount = reader.ReadInt32();
        var flags = reader.ReadByte();

        var fileRoot = ReadString(reader);
        if (!string.Equals(fileRoot, root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var engineName = ReadString(reader);
        var sizedAt = new DateTimeOffset(sizedAtTicks, TimeSpan.Zero);
        if (now - sizedAt.UtcDateTime > MaxAge || sizedAt.UtcDateTime - now > ClockSkewTolerance)
        {
            return null;
        }

        if (!IsAcceptableEngine(engineName))
        {
            return null;
        }

        var parent = ReadInts(reader, count);
        var totalBytes = ReadLongs(reader, count);
        var fileCount = ReadLongs(reader, count);

        long[]? allocatedBytes = (flags & 0x01) != 0 ? ReadLongs(reader, count) : null;
        NtfsRecordAllocation? ntfsAlloc = null;
        if ((flags & 0x02) != 0)
        {
            var reserved = reader.ReadInt64();
            var unlinked = reader.ReadInt64();
            var incompleteRecords = reader.ReadInt32();
            ntfsAlloc = new NtfsRecordAllocation(reserved, unlinked, Bitmap: null, incompleteRecords);
        }

        BitArray? incompleteBits = (flags & 0x04) != 0 ? ReadBitArray(reader, count) : null;
        BitArray? incompleteAllocBits = (flags & 0x08) != 0 ? ReadBitArray(reader, count) : null;

        var offsetCount = reader.ReadInt32();
        if (offsetCount != count + 1)
        {
            throw new InvalidDataException("name offset count mismatch");
        }

        var offsets = ReadInts(reader, offsetCount);
        var blobLength = reader.ReadInt32();
        if (blobLength < 0)
        {
            throw new InvalidDataException("negative name blob");
        }

        var blob = new byte[blobLength];
        ReadExact(reader, blob);

        if (parent[0] != 0 || offsets[0] != 0 || offsets[count] != blobLength)
        {
            throw new InvalidDataException("inconsistent tree arrays");
        }

        for (var i = 1; i < count; i++)
        {
            if (parent[i] < 0 || parent[i] >= count)
            {
                throw new InvalidDataException($"parent of node {i} outside tree");
            }
        }

        for (var i = 0; i < count; i++)
        {
            if (offsets[i] < 0 || offsets[i] > offsets[i + 1] || offsets[i + 1] > blobLength)
            {
                throw new InvalidDataException($"name offsets of node {i} unordered");
            }
        }

        var names = new string[count];
        for (var i = 0; i < count; i++)
        {
            names[i] = Encoding.UTF8.GetString(blob, offsets[i], offsets[i + 1] - offsets[i]);
        }

        var elapsed = elapsedTicks >= 0 && elapsedTicks <= TimeSpan.MaxValue.Ticks
            ? TimeSpan.FromTicks(elapsedTicks)
            : TimeSpan.Zero;

        metadata = new FolderIndexMetadata(sizedAt, treeHash, usnJournalId, nextUsn, lowestValidUsn);
        return FolderTree.Restore(
            root,
            parent,
            names,
            totalBytes,
            fileCount,
            engineName,
            elapsed,
            allocatedBytes,
            ntfsAlloc,
            incompleteBits,
            incompleteDirCount,
            incompleteAllocBits,
            incompleteAllocDirCount);
    }

    private static void VerifySha256(FileStream stream, long payloadLength)
    {
        stream.Position = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        var remaining = payloadLength;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = stream.Read(buffer, 0, toRead);
            if (read <= 0)
            {
                throw new EndOfStreamException("truncated payload");
            }

            hasher.AppendData(buffer, 0, read);
            remaining -= read;
        }

        Span<byte> stored = stackalloc byte[Sha256Length];
        var total = 0;
        while (total < Sha256Length)
        {
            var chunk = stream.Read(stored[total..]);
            if (chunk <= 0)
            {
                throw new EndOfStreamException("truncated digest");
            }

            total += chunk;
        }

        var computed = hasher.GetHashAndReset();
        if (!stored.SequenceEqual(computed))
        {
            throw new InvalidDataException("SHA-256 integrity hash mismatch");
        }
    }

    private static void WriteBitArray(BinaryWriter writer, BitArray bits)
    {
        var byteLen = (bits.Length + 7) / 8;
        var bytes = new byte[byteLen];
        bits.CopyTo(bytes, 0);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static BitArray ReadBitArray(BinaryReader reader, int expectedBitCount)
    {
        var byteLen = reader.ReadInt32();
        var expectedBytes = (expectedBitCount + 7) / 8;
        if (byteLen != expectedBytes)
        {
            throw new InvalidDataException("bitarray length mismatch");
        }

        var bytes = new byte[byteLen];
        ReadExact(reader, bytes);
        return new BitArray(bytes) { Length = expectedBitCount };
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length < 0 || length > 4096)
        {
            throw new InvalidDataException("bad string length");
        }

        var bytes = new byte[length];
        ReadExact(reader, bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteBuffer(BinaryWriter writer, ReadOnlySpan<byte> bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static int[] ReadInts(BinaryReader reader, int count)
    {
        var length = reader.ReadInt32();
        if (length != count * sizeof(int))
        {
            throw new InvalidDataException("int buffer length mismatch");
        }

        var array = new int[count];
        ReadExact(reader, MemoryMarshal.AsBytes(array.AsSpan()));
        return array;
    }

    private static long[] ReadLongs(BinaryReader reader, int count)
    {
        var length = reader.ReadInt32();
        if (length != count * sizeof(long))
        {
            throw new InvalidDataException("long buffer length mismatch");
        }

        var array = new long[count];
        ReadExact(reader, MemoryMarshal.AsBytes(array.AsSpan()));
        return array;
    }

    private static void ReadExact(BinaryReader reader, Span<byte> destination)
    {
        var read = 0;
        while (read < destination.Length)
        {
            var chunk = reader.BaseStream.Read(destination[read..]);
            if (chunk <= 0)
            {
                throw new EndOfStreamException("cache ended early");
            }

            read += chunk;
        }
    }

    private static bool IsCorrupt(Exception ex) =>
        ex is InvalidDataException
            or EndOfStreamException
            or ArgumentException
            or OverflowException
            or IndexOutOfRangeException
            or FormatException
            or DecoderFallbackException;

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static uint? TryGetVolumeSerial(string root)
    {
        var name = new StringBuilder(261);
        var fileSystem = new StringBuilder(261);
        if (!GetVolumeInformationW(root, name, name.Capacity, out var serial, out _, out _, fileSystem, fileSystem.Capacity))
        {
            return null;
        }

        return serial;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder? fileSystemNameBuffer,
        int fileSystemNameSize);

    private sealed class HashingWriteStream(Stream inner, IncrementalHash hasher) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            hasher.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hasher.AppendData(buffer);
            inner.Write(buffer);
        }
    }

    private sealed class BoundedReadStream(Stream inner, long remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => remaining;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(count, remaining);
            var read = inner.Read(buffer, offset, toRead);
            remaining -= read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            if (remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = inner.Read(buffer[..toRead]);
            remaining -= read;
            return read;
        }
    }
}
