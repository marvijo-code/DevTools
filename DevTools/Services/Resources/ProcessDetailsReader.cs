using System.Runtime.InteropServices;

namespace DevTools.Services.Resources;

/// <summary>Command line and working directory for one process, or empty strings when unreadable.</summary>
public readonly record struct ProcessDetails(string CommandLine, string WorkingDirectory)
{
    public static ProcessDetails Empty { get; } = new(string.Empty, string.Empty);

    public bool HasWorkingDirectory => WorkingDirectory.Length > 0;
}

/// <summary>
/// Reads a process's command line and current working directory out of its PEB.
///
/// The working directory is what identifies an agent session - a Claude or Codex process is only
/// meaningfully named by the repo it is working in, and neither WMI nor the kernel process snapshot
/// exposes it. WMI can supply the command line but costs hundreds of milliseconds for a full sweep,
/// which is far too slow for a per-second sampler.
///
/// Results are cached per PID because neither value changes over a process's lifetime.
/// </summary>
public sealed class ProcessDetailsReader
{
    private const int ProcessBasicInformation = 0;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    // Offsets into PEB / RTL_USER_PROCESS_PARAMETERS on x64.
    private const int PebProcessParametersOffset = 0x20;
    private const int ParametersCurrentDirectoryOffset = 0x38;
    private const int ParametersCommandLineOffset = 0x70;

    private readonly Dictionary<int, ProcessDetails> _cache = new(256);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        out ProcessBasicInfo processInformation,
        int processInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out IntPtr bytesRead);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInfo
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    /// <summary>Drops cache entries for processes that have exited, so PID reuse cannot return stale data.</summary>
    public void RetainOnly(IReadOnlySet<int> livePids)
    {
        if (_cache.Count == 0)
        {
            return;
        }

        var stale = _cache.Keys.Where(pid => !livePids.Contains(pid)).ToList();
        foreach (var pid in stale)
        {
            _cache.Remove(pid);
        }
    }

    public ProcessDetails Read(int processId)
    {
        if (processId <= 0)
        {
            return ProcessDetails.Empty;
        }

        if (_cache.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        var details = ReadUncached(processId);
        _cache[processId] = details;
        return details;
    }

    private static ProcessDetails ReadUncached(int processId)
    {
        // Protected and elevated processes deny the handle; that is expected and simply yields no detail.
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        if (handle == IntPtr.Zero)
        {
            return ProcessDetails.Empty;
        }

        try
        {
            if (NtQueryInformationProcess(handle, ProcessBasicInformation, out var info, Marshal.SizeOf<ProcessBasicInfo>(), out _) < 0 ||
                info.PebBaseAddress == IntPtr.Zero)
            {
                return ProcessDetails.Empty;
            }

            if (!TryReadPointer(handle, info.PebBaseAddress + PebProcessParametersOffset, out var parameters) ||
                parameters == IntPtr.Zero)
            {
                return ProcessDetails.Empty;
            }

            var commandLine = ReadUnicodeString(handle, parameters + ParametersCommandLineOffset);
            var workingDirectory = ReadUnicodeString(handle, parameters + ParametersCurrentDirectoryOffset);

            return new ProcessDetails(commandLine, NormalizeDirectory(workingDirectory));
        }
        catch
        {
            return ProcessDetails.Empty;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TryReadPointer(IntPtr handle, IntPtr address, out IntPtr value)
    {
        var buffer = new byte[IntPtr.Size];
        if (!ReadProcessMemory(handle, address, buffer, buffer.Length, out _))
        {
            value = IntPtr.Zero;
            return false;
        }

        value = (IntPtr)BitConverter.ToInt64(buffer, 0);
        return true;
    }

    /// <summary>Reads a UNICODE_STRING (ushort Length, ushort MaximumLength, padding, char* Buffer).</summary>
    private static string ReadUnicodeString(IntPtr handle, IntPtr address)
    {
        var header = new byte[16];
        if (!ReadProcessMemory(handle, address, header, header.Length, out _))
        {
            return string.Empty;
        }

        var length = BitConverter.ToUInt16(header, 0);
        var buffer = (IntPtr)BitConverter.ToInt64(header, 8);

        if (length == 0 || buffer == IntPtr.Zero || length > 32 * 1024)
        {
            return string.Empty;
        }

        var content = new byte[length];
        if (!ReadProcessMemory(handle, buffer, content, content.Length, out _))
        {
            return string.Empty;
        }

        return System.Text.Encoding.Unicode.GetString(content).TrimEnd('\0');
    }

    private static string NormalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return string.Empty;
        }

        var trimmed = directory.Trim();

        // The PEB stores the working directory with a trailing separator; drop it except for drive roots.
        return trimmed.Length > 3 && (trimmed.EndsWith('\\') || trimmed.EndsWith('/'))
            ? trimmed[..^1]
            : trimmed;
    }
}
