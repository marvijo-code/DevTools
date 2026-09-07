using System.Diagnostics;
using Microsoft.Win32;

namespace DevTools.Services;

/// <summary>Opens the official inspector out of process, without loading a custom plugin.</summary>
public sealed class SystemInformerIntegration
{
    public static string? FindExecutable()
    {
        var candidates = new List<string?>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SystemInformer", "SystemInformer.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "System Informer", "SystemInformer.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SystemInformer", "SystemInformer.exe")
        };
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\SystemInformer.exe");
            candidates.Add(key?.GetValue(null) as string);
        }
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    public static ProcessStartInfo CreateStartInfo(string executable, int? processId = null)
    {
        if (processId is <= 0)
            throw new ArgumentOutOfRangeException(nameof(processId));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!
        };
        if (processId.HasValue)
        {
            start.ArgumentList.Add("-selectpid");
            start.ArgumentList.Add(processId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        start.ArgumentList.Add("-selecttab");
        start.ArgumentList.Add("Processes");
        return start;
    }

    public void Open(int? processId = null)
    {
        var executable = FindExecutable() ?? throw new FileNotFoundException(
            "System Informer is not installed. Install the official version from systeminformer.com.");
        if (processId.HasValue)
        {
            using var process = Process.GetProcessById(processId.Value);
            if (process.HasExited)
                throw new InvalidOperationException("That process has exited. Refresh the process list.");
        }
        Process.Start(CreateStartInfo(executable, processId));
    }
}
