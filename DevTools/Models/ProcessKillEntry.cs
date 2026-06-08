using DevTools.Infrastructure;

namespace DevTools.Models;

public sealed class ProcessKillEntry : ObservableObject
{
    public ProcessKillEntry(
        int processId,
        int parentProcessId,
        string displayName,
        string commandLine,
        string executablePath,
        string startPath,
        long workingSetBytes,
        string usage,
        string note)
    {
        ProcessId = processId;
        ParentProcessId = parentProcessId;
        DisplayName = displayName;
        CommandLine = commandLine;
        ExecutablePath = executablePath;
        StartPath = startPath;
        WorkingSetBytes = workingSetBytes;
        Usage = usage;
        Note = note;
        CopyText = BuildCopyText(displayName, processId, executablePath);
    }

    public int ProcessId { get; }

    public int ParentProcessId { get; }

    public string DisplayName { get; }

    public string CommandLine { get; }

    public string ExecutablePath { get; }

    public string StartPath { get; }

    public long WorkingSetBytes { get; }

    public string Usage { get; }

    public string Note { get; }

    public string CopyText { get; }

    public bool CanKill => ProcessId > 0;

    public static string BuildCopyText(string displayName, int processId, string executablePath)
    {
        var nameLine = $"{displayName} (PID {processId})";
        return string.IsNullOrWhiteSpace(executablePath)
            ? nameLine
            : $"{nameLine}{Environment.NewLine}{executablePath}";
    }
}
