using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DevTools.Services.BiggestFiles.Ntfs;

/// <summary>Geometry of an NTFS volume needed to locate and read the MFT.</summary>
internal readonly record struct NtfsVolumeData(
    uint BytesPerCluster,
    uint BytesPerFileRecordSegment,
    long MftValidDataLength,
    long MftStartLcn,
    long TotalClusters);

internal static class NativeMethods
{
    private const uint GenericRead = 0x80000000;
    private const uint ReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenDirectory = 0x02000000;
    private const uint OpenExisting = 3;
    private const uint FsctlGetNtfsVolumeData = 0x00090064;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        byte[] lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    /// <summary>Opens a raw read handle to a volume, e.g. <c>\\.\C:</c>. Requires elevation.</summary>
    internal static SafeFileHandle OpenVolume(string path)
    {
        var handle = CreateFile(
            path,
            GenericRead,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open volume {path}.");
        }

        return handle;
    }

    internal static int GetDirectoryRecordCapacity(string rootPath)
    {
        // An ordinary directory handle can query volume geometry without raw volume access.
        using var root = CreateFile(rootPath, ReadAttributes, FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero, OpenExisting, OpenDirectory, IntPtr.Zero);
        if (root.IsInvalid)
        {
            return 0;
        }

        try
        {
            var geometry = GetVolumeData(root);
            return MftTableLoader.GetRecordCount(geometry);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or NotSupportedException)
        {
            return 0;
        }
    }

    internal static NtfsVolumeData GetVolumeData(SafeFileHandle volume)
    {
        var buffer = new byte[128];
        if (!DeviceIoControl(volume, FsctlGetNtfsVolumeData, IntPtr.Zero, 0, buffer, (uint)buffer.Length, out var returned, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "FSCTL_GET_NTFS_VOLUME_DATA failed.");
        }

        if (returned < 0x48 || returned > buffer.Length)
        {
            throw new IOException("FSCTL_GET_NTFS_VOLUME_DATA returned an incomplete geometry.");
        }

        var span = buffer.AsSpan(0, (int)returned);
        var totalClusters = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(0x10, 8));
        var bytesPerCluster = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x2C, 4));
        var bytesPerFileRecordSegment = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(0x30, 4));
        var mftValidDataLength = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(0x38, 8));
        var mftStartLcn = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(0x40, 8));

        return new NtfsVolumeData(bytesPerCluster, bytesPerFileRecordSegment, mftValidDataLength, mftStartLcn, totalClusters);
    }
}
