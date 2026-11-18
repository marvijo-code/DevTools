using System.Runtime.InteropServices;
using System.Text;

namespace DevTools.Services.FolderSizes;

internal static class WindowsVolumePaths
{
    private const int VolumeNameCapacity = 64;
    private const int ErrorNoMoreFiles = 18;
    private const string VolumeGuidPrefix = @"\\?\Volume{";

    internal static Guid? GetVolumeGuid(string? path) =>
        path is not null &&
        path.Length == VolumeGuidPrefix.Length + 38 &&
        path.StartsWith(VolumeGuidPrefix, StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(@"}\", StringComparison.Ordinal) &&
        Guid.TryParseExact(path.AsSpan(VolumeGuidPrefix.Length, 36), "D", out var id)
            ? id
            : null;

    internal static string? GetDevicePath(string? root)
    {
        if (root is { Length: 3 } && char.IsAsciiLetter(root[0]) && root[1] == ':' && Path.EndsInDirectorySeparator(root))
        {
            return @"\\.\" + root[..2];
        }

        // CreateFile opens the raw volume when the trailing separator is omitted.
        return GetVolumeGuid(root).HasValue ? root![..^1] : null;
    }

    internal static string? GetVolumeName(string rootPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buffer = new StringBuilder(VolumeNameCapacity);
        return GetVolumeNameForVolumeMountPoint(rootPath, buffer, VolumeNameCapacity)
            ? buffer.ToString()
            : null;
    }

    internal static IEnumerable<string> GetFixedVolumeNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            yield break;
        }

        var buffer = new StringBuilder(VolumeNameCapacity);
        var search = FindFirstVolume(buffer, VolumeNameCapacity);
        if (search == new IntPtr(-1))
        {
            yield break;
        }

        try
        {
            do
            {
                var name = buffer.ToString();
                if (GetDriveType(name) == DriveType.Fixed)
                {
                    yield return name;
                }

                buffer.Clear();
            } while (FindNextVolume(search, buffer, VolumeNameCapacity));

            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNoMoreFiles)
            {
                Infrastructure.AppLog.Info($"Windows volume enumeration stopped with error {error}.");
            }
        }
        finally
        {
            FindVolumeClose(search);
        }
    }

    internal static uint GetAllocationUnitBytes(string rootPath)
    {
        if (GetDiskFreeSpace(rootPath, out var sectorsPerCluster, out var bytesPerSector, out _, out _))
        {
            var bytes = (ulong)sectorsPerCluster * bytesPerSector;
            return bytes <= uint.MaxValue ? (uint)bytes : 0;
        }

        return 0;
    }

    internal static (string Label, string FileSystem) ReadVolumeInformation(string rootPath)
    {
        const int capacity = 256;
        var label = new StringBuilder(capacity);
        var fileSystem = new StringBuilder(capacity);
        return GetVolumeInformation(rootPath, label, capacity, out _, out _, out _, fileSystem, capacity)
            ? (label.ToString(), fileSystem.ToString())
            : (string.Empty, string.Empty);
    }

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string rootPath, StringBuilder name, int capacity);

    [DllImport("kernel32.dll", EntryPoint = "FindFirstVolumeW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindFirstVolume(StringBuilder name, int capacity);

    [DllImport("kernel32.dll", EntryPoint = "FindNextVolumeW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextVolume(IntPtr search, StringBuilder name, int capacity);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindVolumeClose(IntPtr search);

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern DriveType GetDriveType(string rootPath);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpace(
        string rootPath, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string rootPath, StringBuilder label, int labelCapacity, out uint serial,
        out uint maxComponentLength, out uint flags, StringBuilder fileSystem, int fileSystemCapacity);
}
