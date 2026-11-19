using System.Runtime.InteropServices;
using System.Text;

namespace DevTools.Services.FolderSizes;

internal readonly record struct VolumeMountAlias(string AccessPath, string ScanRoot, string VolumeName);
internal readonly record struct ResolvedVolumeMount(VolumeMountAlias Mount, string Path);

internal static class WindowsVolumePaths
{
    private const int VolumeNameCapacity = 64;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorMoreData = 234;
    private const int MaximumAccessPathChars = 1024 * 1024;
    private const int MaximumWindowsPathChars = 32_768;
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

    internal static string? GetContainingVolumeRoot(string fullPath, string pathRoot)
    {
        if (!OperatingSystem.IsWindows() || fullPath.Equals(pathRoot, StringComparison.OrdinalIgnoreCase))
        {
            return pathRoot;
        }

        // The walker opens the final entry itself. Resolving its parent gets the volume that owns
        // that entry, even if the final entry is a junction or mount point on that volume.
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(fullPath));
        return parent is null ? null : ResolveVolumeRoot(parent);
    }

    // Directory enumeration opens the target of its root path, including mounted folders.
    internal static string? GetDirectoryVolumeRoot(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath);
        return !OperatingSystem.IsWindows() ||
            (fullPath.Equals(root, StringComparison.OrdinalIgnoreCase) && GetDevicePath(root) is not null)
            ? root
            : ResolveVolumeRoot(fullPath);
    }

    private static string? ResolveVolumeRoot(string path)
    {
        if (!Path.EndsInDirectorySeparator(path))
        {
            path += Path.DirectorySeparatorChar;
        }

        var capacity = Math.Clamp(path.Length + 2, 260, MaximumWindowsPathChars);
        var mount = new StringBuilder(capacity);
        if (!GetVolumePathName(path, mount, (uint)mount.Capacity))
        {
            return null;
        }

        var volumePath = mount.ToString();
        if (volumePath.Length == 0)
        {
            return null;
        }

        if (!Path.EndsInDirectorySeparator(volumePath))
        {
            volumePath += Path.DirectorySeparatorChar;
        }

        // Mounted folders need the GUID so the seek-penalty query can open the backing device.
        return GetDevicePath(volumePath) is not null ? volumePath : GetVolumeName(volumePath);
    }

    internal static VolumeMountAlias[] GetMountAliases(IReadOnlyList<DriveDescriptor> drives,
        CancellationToken cancellationToken)
    {
        var aliases = new List<VolumeMountAlias>();
        foreach (var drive in drives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (drive.VolumeName is not { } volumeName)
            {
                continue;
            }

            var paths = GetAccessPaths(volumeName).Append(volumeName);
            if (drive.DriveLetterRoot is { } driveLetter)
            {
                paths = paths.Append(driveLetter).Distinct(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var path in paths)
            {
                if (Path.IsPathFullyQualified(path) && Path.EndsInDirectorySeparator(path) &&
                    !path.Equals(drive.RootPath, StringComparison.OrdinalIgnoreCase))
                {
                    aliases.Add(new VolumeMountAlias(path, drive.RootPath, volumeName));
                }
            }
        }

        return [.. aliases];
    }

    internal static ResolvedVolumeMount? ResolveMountAlias(string path, ReadOnlySpan<VolumeMountAlias> aliases)
    {
        if (aliases.IsEmpty)
        {
            return null;
        }

        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        var bestLength = 0;
        VolumeMountAlias best = default;
        foreach (var alias in aliases)
        {
            // Include the separator in the length but also accept a mount root entered without it.
            var length = alias.AccessPath.Length;
            if (length <= bestLength || normalized.Length < length - 1 ||
                !normalized.AsSpan().StartsWith(alias.AccessPath.AsSpan(0, length - 1), StringComparison.OrdinalIgnoreCase) ||
                (normalized.Length >= length && normalized[length - 1] != Path.DirectorySeparatorChar))
            {
                continue;
            }

            bestLength = length;
            best = alias;
        }

        return bestLength == 0
            ? null
            : new ResolvedVolumeMount(best, string.Concat(best.ScanRoot.AsSpan(),
                normalized.AsSpan(Math.Min(bestLength, normalized.Length))));
    }

    internal static string[] GetAccessPaths(string volumeName)
    {
        if (!OperatingSystem.IsWindows() || !GetVolumeGuid(volumeName).HasValue)
        {
            return [];
        }

        var buffer = new char[256];
        while (true)
        {
            if (GetVolumePathNamesForVolumeName(volumeName, buffer, (uint)buffer.Length, out var required))
            {
                return required <= buffer.Length
                    ? new string(buffer, 0, (int)required).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                    : [];
            }

            if (Marshal.GetLastPInvokeError() != ErrorMoreData || required <= buffer.Length || required > MaximumAccessPathChars)
            {
                return [];
            }

            buffer = new char[required];
        }
    }

    internal static bool IsDiskVolumeType(DriveType type, bool includeRemovable) =>
        type == DriveType.Fixed || (includeRemovable && type == DriveType.Removable);

    internal static bool IsRemovable(string rootPath) =>
        OperatingSystem.IsWindows() && GetDriveType(rootPath) == DriveType.Removable;

    internal static IEnumerable<(string Name, DriveType Type)> GetDiskVolumeNames(bool includeRemovable)
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
                var type = GetDriveType(name);
                if (IsDiskVolumeType(type, includeRemovable))
                {
                    yield return (name, type);
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

    // A card reader can lose its media between any two metadata calls. Keep the
    // thread's other error flags and let storage errors return through the API.
    internal static CriticalErrorScope SuppressCriticalErrors()
    {
        if (!OperatingSystem.IsWindows())
        {
            return default;
        }

        const uint failCriticalErrors = 1;
        var mode = GetThreadErrorMode();
        return (mode & failCriticalErrors) == 0 && SetThreadErrorMode(mode | failCriticalErrors, out var previous)
            ? new CriticalErrorScope(previous)
            : default;
    }

    internal readonly struct CriticalErrorScope(uint? previousMode) : IDisposable
    {
        public void Dispose()
        {
            if (previousMode is uint mode)
            {
                SetThreadErrorMode(mode, out _);
            }
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetThreadErrorMode();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadErrorMode(uint mode, out uint previousMode);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string rootPath, StringBuilder name, int capacity);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string path, StringBuilder volumePath, uint length);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNamesForVolumeNameW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNamesForVolumeName(string volumeName, [Out] char[] paths, uint length, out uint required);

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
