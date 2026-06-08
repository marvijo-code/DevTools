using DevTools.Models;

namespace DevTools.Services;

public sealed class ProcessKillCatalog
{
    private readonly RunningProcessCollector _runningProcessCollector = new();

    public IReadOnlyList<ProcessKillSection> CaptureSections(CodexSessionCatalog? sessionCatalog = null)
    {
        var processes = _runningProcessCollector.Capture(sessionCatalog);
        if (processes.Count == 0)
        {
            return
            [
                new ProcessKillSection(
                    "No Processes",
                    "No matching running processes were found on this machine.",
                    string.Empty,
                    [new ProcessKillEntry(0, 0, "No processes", string.Empty, string.Empty, string.Empty, 0, "Unavailable", "Process enumeration returned no results.")])
            ];
        }

        return ProcessKillFilter.Apply(
            processes
            .GroupBy(process => ResolveGroupTitle(process.DisplayName), StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group
                    .OrderByDescending(process => process.WorkingSetBytes)
                    .ThenBy(process => process.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(process =>
                    {
                        var executablePath = ProcessCommandLineResolver.ResolveExecutablePath(
                            process.CommandLine,
                            process.MeasurementPath);
                        var startPath = ProcessCommandLineResolver.FormatStartPathDisplay(
                            process.CommandLine,
                            process.MeasurementPath);
                        var childDisplayName = ResolveEntryDisplayName(group.Key, process);
                        return new ProcessKillEntry(
                            process.ProcessId,
                            process.ParentProcessId,
                            childDisplayName,
                            process.CommandLine,
                            executablePath,
                            startPath,
                            process.WorkingSetBytes,
                            FormatBytes(process.WorkingSetBytes),
                            $"PID {process.ProcessId} | {process.ProcessName}");
                    })
                    .ToList();

                var totalWorkingSet = group.Sum(process => process.WorkingSetBytes);
                var sectionStartPath = ResolveSectionStartPath(group.Key, group);
                return new ProcessKillSection(
                    group.Key,
                    $"{entries.Count} process(es), {FormatBytes(totalWorkingSet)} working set combined.",
                    sectionStartPath,
                    entries);
            })
            .ToList(),
            filterText: null);
    }

    private static string ResolveGroupTitle(string displayName)
    {
        var separatorIndex = displayName.IndexOf(" — ", StringComparison.Ordinal);
        return separatorIndex > 0
            ? displayName[..separatorIndex].Trim()
            : displayName.Trim();
    }

    public static string StripChildDisplayName(string groupTitle, string displayName)
    {
        var prefix = $"{groupTitle} — ";
        return displayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? displayName[prefix.Length..].Trim()
            : displayName.Trim();
    }

    public static string ResolveEntryDisplayName(string groupTitle, RunningProcessSnapshot process)
    {
        var childDisplayName = StripChildDisplayName(groupTitle, process.DisplayName);
        if (!string.Equals(childDisplayName, groupTitle, StringComparison.OrdinalIgnoreCase))
        {
            return childDisplayName;
        }

        var executableBaseName = ProcessDisplayNameResolver.ResolveExecutableBaseName(
            process.CommandLine,
            process.ProcessName);
        return string.IsNullOrWhiteSpace(executableBaseName)
            ? childDisplayName
            : executableBaseName;
    }

    public static string ResolveSectionStartPath(string groupTitle, IEnumerable<RunningProcessSnapshot> processes)
    {
        var orderedProcesses = processes
            .OrderByDescending(process => process.WorkingSetBytes)
            .ToList();

        var parentProcess = orderedProcesses.FirstOrDefault(process => IsParentProcess(groupTitle, process));
        parentProcess ??= orderedProcesses.FirstOrDefault();

        return parentProcess is null
            ? string.Empty
            : ProcessCommandLineResolver.ResolveExecutablePath(
                parentProcess.CommandLine,
                parentProcess.MeasurementPath);
    }

    private static bool IsParentProcess(string groupTitle, RunningProcessSnapshot process)
    {
        var childDisplayName = StripChildDisplayName(groupTitle, process.DisplayName);
        if (!string.Equals(childDisplayName, groupTitle, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !ProcessCommandLineResolver.IsSubprocessCommandLine(process.CommandLine);
    }

    private static string FormatBytes(long bytes)
    {
        const double scale = 1024d;
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        var suffixIndex = 0;
        double displayValue = bytes;
        while (displayValue >= scale && suffixIndex < suffixes.Length - 1)
        {
            displayValue /= scale;
            suffixIndex++;
        }

        return suffixIndex == 0
            ? $"{displayValue:0} {suffixes[suffixIndex]}"
            : $"{displayValue:0.00} {suffixes[suffixIndex]}";
    }
}
