using System.Management;
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
            foreach (ManagementObject processObject in searcher.Get())
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

                    var measurementPath = ProcessDisplayNameResolver.TryExtractExecutablePath(commandLine) ??
                                          executablePath ??
                                          commandLine;
                    var displayName = ProcessDisplayNameResolver.Resolve(
                        commandLine,
                        processName,
                        processId,
                        parentProcessId,
                        workingSetBytes,
                        catalog);

                    processes.Add(new RunningProcessSnapshot(
                        processId,
                        parentProcessId,
                        processName ?? Path.GetFileName(measurementPath),
                        commandLine,
                        measurementPath,
                        displayName,
                        workingSetBytes,
                        _detailsReader.Read(processId).WorkingDirectory,
                        firefoxTitles.TryGetValue(processId, out var tabTitles) ? tabTitles : string.Empty));
                }
            }
        }
        catch (ManagementException)
        {
            return [];
        }

        return OrderSnapshot(processes);
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
