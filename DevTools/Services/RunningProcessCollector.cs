using System.Management;
using DevTools.Models;
using DevTools.Services.Resources;

namespace DevTools.Services;

public sealed class RunningProcessCollector
{
    private const int MinimumWorkingSetBytes = 4 * 1024 * 1024;
    private const int MaxProcessCount = 40;

    /// <summary>
    /// Supplies each process's working directory, which is what lets the Processes tab group by agent
    /// session (the repo being worked in) instead of by executable name.
    /// </summary>
    private readonly ProcessDetailsReader _detailsReader = new();

    public IReadOnlyList<RunningProcessSnapshot> Capture(CodexSessionCatalog? sessionCatalog = null)
    {
        var catalog = sessionCatalog ?? CodexSessionCatalog.LoadDefault();
        var processes = new List<RunningProcessSnapshot>();

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

                    if (workingSetBytes < MinimumWorkingSetBytes &&
                        !IsAlwaysInteresting(processName, commandLine, executablePath))
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(commandLine))
                    {
                        commandLine = executablePath;
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
                        _detailsReader.Read(processId).WorkingDirectory));
                }
            }
        }
        catch (ManagementException)
        {
            return [];
        }

        return processes
            .GroupBy(process => process.CommandLine, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.WorkingSetBytes).First())
            .OrderByDescending(process => process.WorkingSetBytes)
            .ThenBy(process => process.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Take(MaxProcessCount)
            .ToList();
    }

    private static bool IsAlwaysInteresting(string? processName, string? commandLine, string? executablePath)
    {
        var haystack = string.Join(
            ' ',
            new[] { processName, commandLine, executablePath }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return haystack.Contains("Codex", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains(@"\Google\Chrome\", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains(@"\Mozilla Firefox\", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("AnthropicClaude", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("VBCSCompiler", StringComparison.OrdinalIgnoreCase) ||
               haystack.Contains("MSBuild", StringComparison.OrdinalIgnoreCase);
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
    string WorkingDirectory = "");
