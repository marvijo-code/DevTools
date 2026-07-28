using DevTools.Models;
using DevTools.Services.Resources;

namespace DevTools.Services;

public sealed class ProcessKillCatalog
{
    private readonly RunningProcessCollector _runningProcessCollector = new();
    private readonly ClaudeSessionCatalog _claudeSessions = new();

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
            .GroupBy(ResolveSessionGroupTitle, StringComparer.OrdinalIgnoreCase)
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
                var summary = $"{entries.Count} process(es), {FormatBytes(totalWorkingSet)} working set combined.";

                // Lead the summary with what the session is actually working on, when the agent reports it.
                var sessionTitle = _claudeSessions.ResolveTitle(group.Select(process => process.ProcessId));
                if (!string.IsNullOrWhiteSpace(sessionTitle))
                {
                    summary = $"{sessionTitle} — {summary}";
                }

                return new ProcessKillSection(
                    group.Key,
                    summary,
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

    /// <summary>
    /// Group title for one process. Developer processes are keyed by the workspace they are running in, so
    /// two Claude sessions in different repos become two groups instead of collapsing into one "Claude"
    /// row. Child processes inherit their parent's working directory, so they land in the same group
    /// without needing to walk the process tree.
    /// </summary>
    public static string ResolveSessionGroupTitle(RunningProcessSnapshot process)
    {
        var baseTitle = ResolveGroupTitle(process.DisplayName);
        var workspace = DescribeWorkspace(process.WorkingDirectory);

        if (workspace.Length == 0)
        {
            return baseTitle;
        }

        var category = WorkloadClassifier.Classify(process.ProcessName);
        return WorkloadClassifier.IsDeveloperWorkload(category)
            ? $"{baseTitle} · {workspace}"
            : baseTitle;
    }

    private static string DescribeWorkspace(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return string.Empty;
        }

        var trimmed = workingDirectory.TrimEnd('\\', '/');

        // A process sitting in a system directory or the user profile root tells us nothing about which
        // repo it belongs to, so it stays in the plain executable group.
        if (IsUninformativeDirectory(trimmed))
        {
            return string.Empty;
        }

        var separatorIndex = trimmed.LastIndexOfAny(['\\', '/']);
        return separatorIndex >= 0 && separatorIndex < trimmed.Length - 1
            ? trimmed[(separatorIndex + 1)..]
            : trimmed;
    }

    private static bool IsUninformativeDirectory(string directory)
    {
        if (directory.Length <= 3)
        {
            return true;
        }

        string[] uninformativeRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
        ];

        return uninformativeRoots.Any(root =>
            !string.IsNullOrEmpty(root) &&
            string.Equals(directory, root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Strips the " · workspace" suffix off a session group title, leaving the executable name that the
    /// per-entry display names are actually prefixed with.
    /// </summary>
    private static string BaseTitleOf(string groupTitle)
    {
        var separatorIndex = groupTitle.IndexOf(" · ", StringComparison.Ordinal);
        return separatorIndex > 0 ? groupTitle[..separatorIndex].Trim() : groupTitle;
    }

    public static string StripChildDisplayName(string groupTitle, string displayName)
    {
        var prefix = $"{BaseTitleOf(groupTitle)} — ";
        return displayName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? displayName[prefix.Length..].Trim()
            : displayName.Trim();
    }

    public static string ResolveEntryDisplayName(string groupTitle, RunningProcessSnapshot process)
    {
        var childDisplayName = StripChildDisplayName(groupTitle, process.DisplayName);
        if (!string.Equals(childDisplayName, BaseTitleOf(groupTitle), StringComparison.OrdinalIgnoreCase))
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
        if (!string.Equals(childDisplayName, BaseTitleOf(groupTitle), StringComparison.OrdinalIgnoreCase))
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
