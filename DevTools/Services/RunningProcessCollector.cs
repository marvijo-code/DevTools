using System.Management;
using System.Runtime.InteropServices;
using DevTools.Models;
using DevTools.Services.Resources;

namespace DevTools.Services;

public sealed class RunningProcessCollector
{
    /// <summary>
    /// Supplies each process's working directory, which is what lets the Processes tab group by agent
    /// session (the repo being worked in) instead of by executable name.
    /// </summary>
    private readonly ProcessDetailsReader _detailsReader = new();

    public IReadOnlyList<RunningProcessSnapshot> Capture(CodexSessionCatalog? sessionCatalog = null)
    {
        var catalog = sessionCatalog ?? CodexSessionCatalog.LoadDefault();
        var processes = new List<RunningProcessSnapshot>();
        var firefoxTitles = FirefoxTabBridge.ReadProcessTitlesAsync().GetAwaiter().GetResult();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, CommandLine, ExecutablePath, WorkingSetSize FROM Win32_Process");
            searcher.Options.Rewindable = false;
            using var results = searcher.Get();
            foreach (ManagementObject processObject in results)
            {
                using (processObject)
                {
                    var processId = Convert.ToInt32(processObject["ProcessId"]);
                    var parentProcessId = Convert.ToInt32(processObject["ParentProcessId"]);
                    var processName = processObject["Name"]?.ToString();
                    var commandLine = processObject["CommandLine"]?.ToString();
                    var executablePath = processObject["ExecutablePath"]?.ToString();
                    var workingSetBytes = Convert.ToInt64(processObject["WorkingSetSize"] ?? 0);

                    if (string.IsNullOrWhiteSpace(commandLine))
                    {
                        commandLine = !string.IsNullOrWhiteSpace(executablePath) ? executablePath : processName;
                    }

                    if (string.IsNullOrWhiteSpace(commandLine))
                    {
                        continue;
                    }

                    AddProcess(processId, parentProcessId, processName, commandLine, executablePath,
                        workingSetBytes, _detailsReader.Read(processId).WorkingDirectory);
                }
            }
        }
        catch (Exception error) when (error is ManagementException or COMException or UnauthorizedAccessException)
        {
            // WMI can be unavailable even when the kernel can still enumerate processes.
            processes.Clear();
        }

        if (processes.Count == 0 && OperatingSystem.IsWindows() && Environment.Is64BitProcess)
        {
            foreach (var process in NativeResourceApi.ReadProcessCounters())
            {
                if (process.ProcessId <= 0 || string.IsNullOrWhiteSpace(process.ImageName))
                {
                    continue;
                }

                var details = ProcessDetailsReader.ReadCurrent(process.ProcessId, process.StartTime);
                var commandLine = string.IsNullOrWhiteSpace(details.CommandLine) ? process.ImageName : details.CommandLine;
                AddProcess(process.ProcessId, process.ParentProcessId, process.ImageName, commandLine,
                    executablePath: null, process.WorkingSetBytes, details.WorkingDirectory);
            }
        }

        return OrderSnapshot(processes);

        void AddProcess(int processId, int parentProcessId, string? processName, string commandLine, string? executablePath,
            long workingSetBytes, string workingDirectory)
        {
            var measurementPath = ProcessDisplayNameResolver.TryExtractExecutablePath(commandLine) ?? executablePath ?? commandLine;
            var displayName = ProcessDisplayNameResolver.Resolve(commandLine, processName, processId, parentProcessId,
                workingSetBytes, catalog);
            processes.Add(new RunningProcessSnapshot(processId, parentProcessId, processName ?? Path.GetFileName(measurementPath),
                commandLine, measurementPath, displayName, workingSetBytes, workingDirectory,
                firefoxTitles.TryGetValue(processId, out var tabTitles) ? tabTitles : string.Empty));
        }
    }

    public static IReadOnlyList<RunningProcessSnapshot> OrderSnapshot(IEnumerable<RunningProcessSnapshot> processes)
    {
        return processes
            .GroupBy(process => process.ProcessId)
            .Select(group => group.OrderByDescending(item => item.WorkingSetBytes).First())
            .OrderByDescending(process => process.WorkingSetBytes)
            .ThenBy(process => process.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public sealed record RunningProcessSnapshot(
    int ProcessId,
    int ParentProcessId,
    string ProcessName,
    string CommandLine,
    string MeasurementPath,
    string DisplayName,
    long WorkingSetBytes,
    string WorkingDirectory = "",
    string BrowserTabs = "");
