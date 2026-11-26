using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DevTools.Infrastructure;
using DevTools.Services.FolderSizes;

namespace DevTools.Services.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFastDeleteResult
{
    public ulong FilesDeleted;
    public ulong DirsDeleted;
    public ulong BytesFreed;
    public ulong Skipped;
    public uint RootRemoved;
    public uint LastError;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeFastMeasureResult
{
    public ulong TotalBytes;
    public ulong TotalFiles;
    public ulong TotalDirs;
    public ulong ContentHash;
    public uint Incomplete;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeWalkTreeStruct
{
    public uint FolderCount;
    public uint IncompleteCount;
    public ulong TotalBytes;
    public ulong TotalFiles;
    public ulong TreeHash;
    public IntPtr Parents;
    public IntPtr TotalBytesNodes;
    public IntPtr FileCountNodes;
    public IntPtr DirMtimeNodes;
    public IntPtr DirHashNodes;
    public IntPtr IncompleteFlags;
    public IntPtr NameOffsets;
    public IntPtr NameBlob;
    public uint NameBlobLen;
}

public readonly record struct FastDeleteOutcome(
    long FilesDeleted,
    long DirsDeleted,
    long BytesFreed,
    long Skipped,
    bool RootRemoved,
    int LastError,
    bool UsedNative,
    TimeSpan Elapsed);

public readonly record struct FastMeasureOutcome(
    long TotalBytes,
    long TotalFiles,
    long TotalDirs,
    ulong ContentHash,
    bool Incomplete,
    bool UsedNative,
    TimeSpan Elapsed);

public sealed record NativeWalkOutcome(
    FolderTree Tree,
    ulong TreeHash,
    long[] DirMtimes,
    ulong[] DirHashes);

/// <summary>
/// Bridge to the optional Rust native library (<c>devtools_native.dll</c>) for parallel Windows
/// directory scanning and fast POSIX-semantics deletion. Falls back cleanly when the DLL is absent.
/// </summary>
public static class NativeFastEngine
{
    private const string DllName = "devtools_native.dll";
    private const uint ExpectedApiVersion = 1;
    private static readonly Lazy<bool> Available = new(ProbeNativeLibrary);

    public static bool IsAvailable => OperatingSystem.IsWindows() && Available.Value;

    private static bool ProbeNativeLibrary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, DllName);
            if (!File.Exists(candidate) && !NativeLibrary.TryLoad(DllName, out _))
            {
                return false;
            }

            var version = dt_api_version();
            if (version == ExpectedApiVersion)
            {
                AppLog.Info($"Native Rust engine loaded ({DllName}, API v{version}).");
                return true;
            }

            AppLog.Info($"Native Rust engine version mismatch: got v{version}, expected v{ExpectedApiVersion}.");
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Info($"Native Rust engine unavailable ({ex.Message}); using managed fallback.");
            return false;
        }
    }

    public static unsafe FastDeleteOutcome? TryDeleteNative(
        string path,
        bool keepRoot = false,
        int workerCount = 0,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var clock = Stopwatch.StartNew();
        var cancelFlag = new byte[1];
        var cancelHandle = GCHandle.Alloc(cancelFlag, GCHandleType.Pinned);
        using var registration = cancellationToken.Register(() => Volatile.Write(ref cancelFlag[0], 1));
        try
        {
            fixed (char* pathPtr = path)
            {
                var raw = dt_fast_delete_path(
                    (ushort*)pathPtr,
                    (uint)path.Length,
                    keepRoot ? 1u : 0u,
                    (uint)Math.Max(0, workerCount),
                    (byte*)cancelHandle.AddrOfPinnedObject());
                clock.Stop();
                return new FastDeleteOutcome(
                    (long)raw.FilesDeleted,
                    (long)raw.DirsDeleted,
                    (long)raw.BytesFreed,
                    (long)raw.Skipped,
                    raw.RootRemoved != 0,
                    (int)raw.LastError,
                    UsedNative: true,
                    clock.Elapsed);
            }
        }
        finally
        {
            cancelHandle.Free();
        }
    }

    public static unsafe FastMeasureOutcome? TryMeasureDirectoryNative(
        string path,
        int workerCount = 0,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var clock = Stopwatch.StartNew();
        var cancelFlag = new byte[1];
        var cancelHandle = GCHandle.Alloc(cancelFlag, GCHandleType.Pinned);
        using var registration = cancellationToken.Register(() => Volatile.Write(ref cancelFlag[0], 1));
        try
        {
            fixed (char* pathPtr = path)
            {
                var raw = dt_fast_measure_dir(
                    (ushort*)pathPtr,
                    (uint)path.Length,
                    (uint)Math.Max(0, workerCount),
                    (byte*)cancelHandle.AddrOfPinnedObject());
                clock.Stop();
                cancellationToken.ThrowIfCancellationRequested();
                return new FastMeasureOutcome(
                    (long)raw.TotalBytes,
                    (long)raw.TotalFiles,
                    (long)raw.TotalDirs,
                    raw.ContentHash,
                    raw.Incomplete != 0,
                    UsedNative: true,
                    clock.Elapsed);
            }
        }
        finally
        {
            cancelHandle.Free();
        }
    }

    public static unsafe NativeWalkOutcome? TryWalkTreeNative(
        string rootPath,
        int workerCount = 0,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        var fullRoot = Path.GetFullPath(rootPath);
        var driveRoot = FolderTree.NormalizeRoot(fullRoot);
        var clock = Stopwatch.StartNew();

        var cancelFlag = new byte[1];
        var cancelHandle = GCHandle.Alloc(cancelFlag, GCHandleType.Pinned);
        using var registration = cancellationToken.Register(() => Volatile.Write(ref cancelFlag[0], 1));
        IntPtr treePtr = IntPtr.Zero;
        try
        {
            fixed (char* pathPtr = fullRoot)
            {
                treePtr = dt_fast_walk_tree(
                    (ushort*)pathPtr,
                    (uint)fullRoot.Length,
                    (uint)Math.Max(0, workerCount),
                    (byte*)cancelHandle.AddrOfPinnedObject());
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (treePtr == IntPtr.Zero)
            {
                return null;
            }

            var native = Marshal.PtrToStructure<NativeWalkTreeStruct>(treePtr);
            var nativeCount = (int)native.FolderCount;
            if (nativeCount <= 0)
            {
                return null;
            }

            var nativeParents = new int[nativeCount];
            var nativeTotals = new long[nativeCount];
            var nativeFiles = new long[nativeCount];
            var nativeMtimes = new long[nativeCount];
            var nativeHashes = new ulong[nativeCount];
            var nativeIncomplete = new byte[nativeCount];
            var nativeOffsets = new int[nativeCount + 1];
            var nativeBlob = native.NameBlobLen > 0 ? new byte[native.NameBlobLen] : [];

            Marshal.Copy(native.Parents, nativeParents, 0, nativeCount);
            Marshal.Copy(native.TotalBytesNodes, nativeTotals, 0, nativeCount);
            Marshal.Copy(native.FileCountNodes, nativeFiles, 0, nativeCount);
            Marshal.Copy(native.DirMtimeNodes, nativeMtimes, 0, nativeCount);
            if (native.DirHashNodes != IntPtr.Zero)
            {
                var tmp = new long[nativeCount];
                Marshal.Copy(native.DirHashNodes, tmp, 0, nativeCount);
                Buffer.BlockCopy(tmp, 0, nativeHashes, 0, nativeCount * sizeof(ulong));
            }
            Marshal.Copy(native.IncompleteFlags, nativeIncomplete, 0, nativeCount);
            Marshal.Copy(native.NameOffsets, nativeOffsets, 0, nativeCount + 1);
            if (nativeBlob.Length > 0)
            {
                Marshal.Copy(native.NameBlob, nativeBlob, 0, nativeBlob.Length);
            }

            // Build ancestor prefix chain if fullRoot is a subfolder under driveRoot.
            var trimmedRoot = Path.TrimEndingDirectorySeparator(fullRoot);
            var trimmedDrive = Path.TrimEndingDirectorySeparator(driveRoot);
            string[] prefixParts = [];
            if (!string.Equals(trimmedRoot, trimmedDrive, StringComparison.OrdinalIgnoreCase) &&
                trimmedRoot.Length > trimmedDrive.Length)
            {
                var relative = trimmedRoot[trimmedDrive.Length..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                prefixParts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            }

            var prefixLen = prefixParts.Length;
            var totalCount = prefixLen + nativeCount;
            var parents = new int[totalCount];
            var names = new string[totalCount];
            var totals = new long[totalCount];
            var files = new long[totalCount];
            var mtimes = new long[totalCount];
            var hashes = new ulong[totalCount];
            BitArray? incompleteBits = native.IncompleteCount > 0 ? new BitArray(totalCount) : null;

            names[0] = string.Empty;
            parents[0] = 0;

            if (prefixLen > 0)
            {
                var subtreeBytes = nativeTotals[0];
                var subtreeFiles = nativeFiles[0];
                totals[0] = subtreeBytes;
                files[0] = subtreeFiles;
                if (incompleteBits is not null && nativeIncomplete[0] != 0)
                {
                    incompleteBits[0] = true;
                }

                for (var p = 0; p < prefixLen; p++)
                {
                    var nodeIdx = p + 1;
                    parents[nodeIdx] = p;
                    names[nodeIdx] = prefixParts[p];
                    totals[nodeIdx] = subtreeBytes;
                    files[nodeIdx] = subtreeFiles;
                    if (incompleteBits is not null && nativeIncomplete[0] != 0)
                    {
                        incompleteBits[nodeIdx] = true;
                    }
                }

                // Node `prefixLen` IS native node 0 (the scanned folder itself).
                mtimes[prefixLen] = nativeMtimes[0];
                hashes[prefixLen] = nativeHashes[0];

                for (var i = 1; i < nativeCount; i++)
                {
                    var dst = prefixLen + i;
                    parents[dst] = prefixLen + nativeParents[i];
                    names[dst] = DecodeName(nativeBlob, nativeOffsets[i], nativeOffsets[i + 1]);
                    totals[dst] = nativeTotals[i];
                    files[dst] = nativeFiles[i];
                    mtimes[dst] = nativeMtimes[i];
                    hashes[dst] = nativeHashes[i];
                    if (incompleteBits is not null && nativeIncomplete[i] != 0)
                    {
                        incompleteBits[dst] = true;
                    }
                }
            }
            else
            {
                Array.Copy(nativeParents, parents, nativeCount);
                Array.Copy(nativeTotals, totals, nativeCount);
                Array.Copy(nativeFiles, files, nativeCount);
                Array.Copy(nativeMtimes, mtimes, nativeCount);
                Array.Copy(nativeHashes, hashes, nativeCount);
                for (var i = 1; i < nativeCount; i++)
                {
                    names[i] = DecodeName(nativeBlob, nativeOffsets[i], nativeOffsets[i + 1]);
                }

                if (incompleteBits is not null)
                {
                    for (var i = 0; i < nativeCount; i++)
                    {
                        if (nativeIncomplete[i] != 0)
                        {
                            incompleteBits[i] = true;
                        }
                    }
                }
            }

            clock.Stop();
            var tree = new FolderTree(
                driveRoot,
                parents,
                names,
                totals,
                files,
                "Rust fast walk",
                clock.Elapsed,
                incompleteSubtrees: incompleteBits,
                incompleteDirectoryCount: (int)native.IncompleteCount);

            return new NativeWalkOutcome(tree, native.TreeHash, mtimes, hashes);
        }
        finally
        {
            if (treePtr != IntPtr.Zero)
            {
                dt_free_walk_tree(treePtr);
            }
            cancelHandle.Free();
        }
    }

    private static string DecodeName(byte[] blob, int start, int end)
    {
        if (end <= start || start < 0 || end > blob.Length)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(blob, start, end - start);
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint dt_api_version();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe NativeFastDeleteResult dt_fast_delete_path(
        ushort* pathPtr,
        uint pathLen,
        uint keepRoot,
        uint workerCount,
        byte* cancelPtr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe IntPtr dt_fast_walk_tree(
        ushort* pathPtr,
        uint pathLen,
        uint workerCount,
        byte* cancelPtr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void dt_free_walk_tree(IntPtr treePtr);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe NativeFastMeasureResult dt_fast_measure_dir(
        ushort* pathPtr,
        uint pathLen,
        uint workerCount,
        byte* cancelPtr);
}
