using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DevTools.Infrastructure;
using DevTools.Services.BiggestFiles.Ntfs;
using Microsoft.Win32.SafeHandles;

namespace DevTools.Services.FolderSizes;

public readonly record struct UsnJournalCursor(ulong JournalId, long NextUsn, long LowestValidUsn);

public sealed record UsnDeltaResult(
    bool CanApplyIncrementally,
    long NewNextUsn,
    int RecordsRead,
    IReadOnlyList<string> ModifiedFileDirectories,
    bool HasDirectoryStructureChanges,
    string Reason);

/// <summary>
/// Reads the NTFS Update Sequence Number (USN) Change Journal so a cached drive index can be
/// validated or incrementally refreshed in milliseconds instead of rescanning the entire MFT.
/// </summary>
internal static class NtfsUsnJournal
{
    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint UsnReasonDataOverwrite = 0x00000001;
    private const uint UsnReasonDataExtend = 0x00000002;
    private const uint UsnReasonDataTruncation = 0x00000004;
    private const uint UsnReasonFileCreate = 0x00000100;
    private const uint UsnReasonFileDelete = 0x00000200;
    private const uint UsnReasonRenameOldName = 0x00001000;
    private const uint UsnReasonRenameNewName = 0x00002000;
    private const uint UsnReasonClose = 0x80000000;
    private const uint FileAttributeDirectory = 0x10;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint OpenNoRecall = 0x00100000;

    private const uint TrackedReasonMask =
        UsnReasonDataOverwrite |
        UsnReasonDataExtend |
        UsnReasonDataTruncation |
        UsnReasonFileCreate |
        UsnReasonFileDelete |
        UsnReasonRenameOldName |
        UsnReasonRenameNewName |
        UsnReasonClose;

    private const int MaxRecordsPerIncrementalRefresh = 25_000;
    private const int MaxAffectedDirectories = 200;
    private static readonly TimeSpan MaxReadDuration = TimeSpan.FromMilliseconds(1500);

    public static UsnJournalCursor? TryQueryCursor(string driveRoot)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var devicePath = WindowsVolumePaths.GetDevicePath(driveRoot);
        if (devicePath is null)
        {
            return null;
        }

        try
        {
            using var volume = NativeMethods.OpenVolume(devicePath);
            return TryQueryCursor(volume);
        }
        catch (Exception ex)
        {
            AppLog.Info($"USN journal query unavailable for {driveRoot}: {ex.Message}");
            return null;
        }
    }

    internal static UsnJournalCursor? TryQueryCursor(SafeFileHandle volume)
    {
        var output = new byte[80];
        if (!DeviceIoControl(volume, FsctlQueryUsnJournal, IntPtr.Zero, 0, output, (uint)output.Length, out var returned, IntPtr.Zero) ||
            returned < 56)
        {
            return null;
        }

        var journalId = BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0, 8));
        var firstUsn = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8, 8));
        var nextUsn = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16, 8));
        var lowestValidUsn = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(24, 8));
        var effectiveLowest = Math.Max(firstUsn, lowestValidUsn);
        return new UsnJournalCursor(journalId, nextUsn, effectiveLowest);
    }

    /// <summary>
    /// Reads USN records between <paramref name="previous"/> and the current volume cursor.
    /// Returns <see cref="UsnDeltaResult.CanApplyIncrementally"/> = true when zero changes occurred
    /// or only a bounded set of directories had file-size changes (no directory tree restructuring).
    /// </summary>
    public static UsnDeltaResult TryReadDelta(
        string driveRoot,
        UsnJournalCursor previous,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || previous.JournalId == 0)
        {
            return new UsnDeltaResult(false, 0, 0, [], false, "No previous USN cursor.");
        }

        var devicePath = WindowsVolumePaths.GetDevicePath(driveRoot);
        if (devicePath is null)
        {
            return new UsnDeltaResult(false, 0, 0, [], false, "Volume device path unavailable.");
        }

        try
        {
            using var volume = NativeMethods.OpenVolume(devicePath);
            var current = TryQueryCursor(volume);
            if (current is not { } live)
            {
                return new UsnDeltaResult(false, 0, 0, [], false, "USN journal is not active on this volume.");
            }

            if (live.JournalId != previous.JournalId)
            {
                return new UsnDeltaResult(false, live.NextUsn, 0, [], true, "USN journal ID changed.");
            }

            if (previous.NextUsn < live.LowestValidUsn)
            {
                return new UsnDeltaResult(false, live.NextUsn, 0, [], true, "USN journal wrapped past cached cursor.");
            }

            if (live.NextUsn <= previous.NextUsn)
            {
                return new UsnDeltaResult(true, live.NextUsn, 0, [], false, "No volume changes since last index.");
            }

            var clock = Stopwatch.StartNew();
            var startUsn = previous.NextUsn;
            var buffer = new byte[64 * 1024];
            var affectedParentFrns = new HashSet<ulong>();
            var recordsRead = 0;
            var hasDirectoryStructureChange = false;

            while (startUsn < live.NextUsn)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clock.Elapsed > MaxReadDuration)
                {
                    return new UsnDeltaResult(false, live.NextUsn, recordsRead, [], hasDirectoryStructureChange,
                        "USN delta exceeded time budget; running full scan.");
                }

                var input = new ReadUsnJournalDataV0
                {
                    StartUsn = startUsn,
                    ReasonMask = TrackedReasonMask,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalID = previous.JournalId
                };

                if (!DeviceIoControlReadUsn(
                        volume,
                        FsctlReadUsnJournal,
                        in input,
                        (uint)Marshal.SizeOf<ReadUsnJournalDataV0>(),
                        buffer,
                        (uint)buffer.Length,
                        out var bytesReturned,
                        IntPtr.Zero))
                {
                    return new UsnDeltaResult(false, live.NextUsn, recordsRead, [], true,
                        $"FSCTL_READ_USN_JOURNAL failed (Win32 error {Marshal.GetLastWin32Error()}).");
                }

                if (bytesReturned <= 8)
                {
                    break;
                }

                var nextUsnFromBatch = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(0, 8));
                var offset = 8;
                while (offset + 60 <= bytesReturned)
                {
                    var recordLen = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4));
                    if (recordLen < 60 || offset + recordLen > bytesReturned)
                    {
                        break;
                    }

                    var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 4, 2));
                    if (majorVersion == 2)
                    {
                        var parentFrn = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset + 16, 8));
                        var reason = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 40, 4));
                        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 52, 4));

                        recordsRead++;
                        if (recordsRead > MaxRecordsPerIncrementalRefresh)
                        {
                            return new UsnDeltaResult(false, live.NextUsn, recordsRead, [], hasDirectoryStructureChange,
                                $"More than {MaxRecordsPerIncrementalRefresh:N0} USN records changed; running full scan.");
                        }

                        var isDir = (attributes & FileAttributeDirectory) != 0;
                        var isStructural = (reason & (UsnReasonFileCreate | UsnReasonFileDelete | UsnReasonRenameOldName | UsnReasonRenameNewName)) != 0;
                        if (isDir && isStructural)
                        {
                            hasDirectoryStructureChange = true;
                            return new UsnDeltaResult(false, live.NextUsn, recordsRead, [], true,
                                "Directory structure changed; running full scan.");
                        }

                        var affectsSizeOrCount = (reason & (UsnReasonDataOverwrite | UsnReasonDataExtend | UsnReasonDataTruncation |
                                                            UsnReasonFileCreate | UsnReasonFileDelete |
                                                            UsnReasonRenameOldName | UsnReasonRenameNewName)) != 0;
                        if (affectsSizeOrCount && !isDir)
                        {
                            affectedParentFrns.Add(parentFrn);
                            if (affectedParentFrns.Count > MaxAffectedDirectories)
                            {
                                return new UsnDeltaResult(false, live.NextUsn, recordsRead, [], false,
                                    $"More than {MaxAffectedDirectories} folders changed; running full scan.");
                            }
                        }
                    }

                    offset += recordLen;
                }

                if (nextUsnFromBatch <= startUsn)
                {
                    break;
                }

                startUsn = nextUsnFromBatch;
            }

            if (affectedParentFrns.Count == 0)
            {
                return new UsnDeltaResult(true, live.NextUsn, recordsRead, [], false,
                    $"Verified {recordsRead:N0} USN event(s); no size-changing folder updates.");
            }

            // Resolve parent directory FRNs to full paths via OpenFileById.
            using var rootHint = CreateFileW(
                driveRoot,
                0,
                7,
                IntPtr.Zero,
                3,
                BackupSemantics | OpenReparsePoint | OpenNoRecall,
                IntPtr.Zero);
            if (rootHint.IsInvalid)
            {
                return new UsnDeltaResult(false, live.NextUsn, recordsRead, [], false,
                    "Could not open volume root handle to resolve changed directories.");
            }

            var resolvedPaths = new List<string>(affectedParentFrns.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var frn in affectedParentFrns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = TryResolveDirectoryPath(rootHint, frn);
                if (path is null)
                {
                    // Parent directory itself may have been deleted or inaccessible; do full scan to stay exact.
                    return new UsnDeltaResult(false, live.NextUsn, recordsRead, [], true,
                        "A modified parent folder could not be resolved; running full scan.");
                }

                if (seen.Add(path))
                {
                    resolvedPaths.Add(path);
                }
            }

            return new UsnDeltaResult(true, live.NextUsn, recordsRead, resolvedPaths, false,
                $"USN journal reported {recordsRead:N0} event(s) across {resolvedPaths.Count} folder(s).");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Info($"USN delta read failed for {driveRoot}: {ex.Message}");
            return new UsnDeltaResult(false, 0, 0, [], false, ex.Message);
        }
    }

    private static string? TryResolveDirectoryPath(SafeFileHandle volumeHint, ulong fileId)
    {
        var descriptor = new FileIdDescriptor(fileId);
        using var handle = OpenFileById(volumeHint, in descriptor, 0, 7, IntPtr.Zero, BackupSemantics | OpenReparsePoint | OpenNoRecall);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[512];
        var len = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (len == 0)
        {
            return null;
        }

        if (len > buffer.Length)
        {
            buffer = new char[len + 1];
            len = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (len == 0 || len > buffer.Length)
            {
                return null;
            }
        }

        var raw = new string(buffer, 0, (int)len);
        if (raw.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            raw = raw[4..];
        }

        return raw;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalDataV0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileIdDescriptor(ulong fileId)
    {
        private readonly int _size = 24;
        private readonly int _type = 0;
        private readonly ulong _fileId = fileId;
        private readonly ulong _reserved = 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        [Out] byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlReadUsn(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        in ReadUsnJournalDataV0 lpInBuffer,
        uint nInBufferSize,
        [Out] byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(
        SafeFileHandle volumeHint,
        in FileIdDescriptor id,
        uint access,
        uint share,
        IntPtr security,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);
}
