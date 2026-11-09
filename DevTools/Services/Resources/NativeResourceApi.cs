using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace DevTools.Services.Resources;

/// <summary>Machine-wide counters captured in a single pass.</summary>
internal readonly record struct SystemCounters(
    long IdleTicks,
    long KernelTicks,
    long UserTicks,
    long PhysicalTotalBytes,
    long PhysicalAvailableBytes,
    long CommitTotalBytes,
    long CommitLimitBytes,
    int ProcessCount,
    int ThreadCount,
    int HandleCount);

/// <summary>Raw per-process counters as reported by the kernel in one snapshot.</summary>
internal readonly record struct RawProcessCounters(
    int ProcessId,
    int ParentProcessId,
    string ImageName,
    DateTimeOffset StartTime,
    long CpuTicks,
    long WorkingSetBytes,
    long CommitBytes,
    long ReadBytes,
    long WriteBytes,
    long OtherBytes,
    int ThreadCount,
    int HandleCount,
    long HardFaultCount);

/// <summary>
/// Thin P/Invoke layer for the resource monitor. All of it is deliberately allocation-light and
/// syscall-light: the monitor samples once a second, so it must never become the thing it is measuring.
/// Per-process data comes from a single <c>NtQuerySystemInformation</c> call rather than opening a handle
/// per process, which would cost hundreds of syscalls per tick and fail on protected processes.
/// </summary>
internal static class NativeResourceApi
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // Field offsets inside SYSTEM_PROCESS_INFORMATION (x64). Parsed by offset rather than declared as a
    // struct so a layout mistake shows up as obviously wrong data instead of memory corruption.
    private const int OffsetNextEntry = 0x00;
    private const int OffsetThreadCount = 0x04;
    private const int OffsetHardFaultCount = 0x10;
    private const int OffsetCreateTime = 0x20;
    private const int OffsetUserTime = 0x28;
    private const int OffsetKernelTime = 0x30;
    private const int OffsetImageNameLength = 0x38;
    private const int OffsetImageNameBuffer = 0x40;
    private const int OffsetProcessId = 0x50;
    private const int OffsetParentProcessId = 0x58;
    private const int OffsetHandleCount = 0x60;
    private const int OffsetWorkingSetSize = 0x90;
    private const int OffsetPagefileUsage = 0xB8;
    private const int OffsetReadTransferCount = 0xE8;
    private const int OffsetWriteTransferCount = 0xF0;
    private const int OffsetOtherTransferCount = 0xF8;
    private const int MinimumEntrySize = 0x100;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation pPerformanceInformation, int cb);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct PerformanceInformation
    {
        public int cb;
        public IntPtr CommitTotal;
        public IntPtr CommitLimit;
        public IntPtr CommitPeak;
        public IntPtr PhysicalTotal;
        public IntPtr PhysicalAvailable;
        public IntPtr SystemCache;
        public IntPtr KernelTotal;
        public IntPtr KernelPaged;
        public IntPtr KernelNonpaged;
        public IntPtr PageSize;
        public int HandleCount;
        public int ProcessCount;
        public int ThreadCount;
    }

    /// <summary>Reads machine-wide CPU time, physical memory and commit charge.</summary>
    internal static SystemCounters ReadSystemCounters()
    {
        if (!GetSystemTimes(out var idleTicks, out var kernelTicks, out var userTicks))
        {
            idleTicks = 0;
            kernelTicks = 0;
            userTicks = 0;
        }

        var info = default(PerformanceInformation);
        info.cb = Marshal.SizeOf<PerformanceInformation>();

        if (!GetPerformanceInfo(out info, info.cb))
        {
            return new SystemCounters(idleTicks, kernelTicks, userTicks, 0, 0, 0, 0, 0, 0, 0);
        }

        var pageSize = (long)info.PageSize;

        return new SystemCounters(
            idleTicks,
            kernelTicks,
            userTicks,
            (long)info.PhysicalTotal * pageSize,
            (long)info.PhysicalAvailable * pageSize,
            (long)info.CommitTotal * pageSize,
            (long)info.CommitLimit * pageSize,
            info.ProcessCount,
            info.ThreadCount,
            info.HandleCount);
    }

    /// <summary>
    /// Snapshots every process in one kernel call. Returns an empty list if the query fails, which the
    /// caller treats as "skip this tick" rather than as zeroed usage.
    /// </summary>
    internal static IReadOnlyList<RawProcessCounters> ReadProcessCounters()
    {
        var bufferSize = 1024 * 1024;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(bufferSize);

            try
            {
                var status = NtQuerySystemInformation(
                    SystemProcessInformation,
                    buffer,
                    bufferSize,
                    out var returnLength);

                if (status == StatusInfoLengthMismatch)
                {
                    // The process list grew between sizing and reading; retry with headroom.
                    bufferSize = Math.Max(returnLength, bufferSize * 2) + (256 * 1024);
                    continue;
                }

                if (status < 0)
                {
                    return [];
                }

                return ParseProcessCounters(buffer, Math.Max(returnLength, 0), bufferSize);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return [];
    }

    private static List<RawProcessCounters> ParseProcessCounters(IntPtr buffer, int returnLength, int bufferSize)
    {
        var length = returnLength > 0 ? Math.Min(returnLength, bufferSize) : bufferSize;
        var results = new List<RawProcessCounters>(512);
        var offset = 0;

        // Copy out of unmanaged memory so the parse runs over a plain span without needing unsafe blocks.
        var scratch = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            Marshal.Copy(buffer, scratch, 0, length);
            var span = new ReadOnlySpan<byte>(scratch, 0, length);

            while (offset >= 0 && offset + MinimumEntrySize <= span.Length)
            {
                var entry = span.Slice(offset);
                var nextEntryOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(OffsetNextEntry, 4));

                var processId = (int)BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(OffsetProcessId, 8));
                var parentProcessId = (int)BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(OffsetParentProcessId, 8));
                var userTime = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(OffsetUserTime, 8));
                var kernelTime = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(OffsetKernelTime, 8));
                var workingSet = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(OffsetWorkingSetSize, 8));
                var commit = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(OffsetPagefileUsage, 8));
                var readBytes = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(OffsetReadTransferCount, 8));
                var writeBytes = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(OffsetWriteTransferCount, 8));
                var otherBytes = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(OffsetOtherTransferCount, 8));
                var threadCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(OffsetThreadCount, 4));
                var handleCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(OffsetHandleCount, 4));
                // Hard faults (not the total page-fault count) are the ones that hit the disk, which is
                // the signal that matters when commit charge has outgrown physical RAM.
                var hardFaults = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(OffsetHardFaultCount, 4));

                var imageName = ReadImageName(entry);
                var createTime = BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(OffsetCreateTime, 8));

                results.Add(new RawProcessCounters(
                    processId,
                    parentProcessId,
                    imageName,
                    ToStartTime(createTime),
                    kernelTime + userTime,
                    workingSet,
                    commit,
                    readBytes,
                    writeBytes,
                    otherBytes,
                    threadCount,
                    handleCount,
                    hardFaults));

                if (nextEntryOffset <= 0)
                {
                    break;
                }

                offset += nextEntryOffset;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }

        return results;
    }

    /// <summary>
    /// Converts a FILETIME create time to local time. The System and Idle processes report zero, and a
    /// value outside the representable range would throw, so both fall back to <see cref="DateTimeOffset.MinValue"/>.
    /// </summary>
    private static DateTimeOffset ToStartTime(long fileTime)
    {
        if (fileTime <= 0)
        {
            return DateTimeOffset.MinValue;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTime).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static string ReadImageName(ReadOnlySpan<byte> entry)
    {
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(OffsetImageNameLength, 2));
        var namePointer = (IntPtr)BinaryPrimitives.ReadInt64LittleEndian(entry.Slice(OffsetImageNameBuffer, 8));

        if (namePointer == IntPtr.Zero || nameLength == 0 || nameLength > 1024)
        {
            // The idle process and a few protected processes report no image name.
            return string.Empty;
        }

        try
        {
            return Marshal.PtrToStringUni(namePointer, nameLength / 2) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
