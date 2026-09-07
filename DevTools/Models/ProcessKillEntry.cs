using DevTools.Infrastructure;
using DevTools.Services;

namespace DevTools.Models;

public sealed class ProcessKillEntry : ObservableObject
{
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value)) RaisePropertyChanged(nameof(SelectionActionText));
        }
    }
    public string SelectionActionText => IsSelected ? "Selected" : "Details";
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

    public int ParentProcessId { get; private set; }

    public string DisplayName { get; private set; }

    public string CommandLine { get; private set; }

    public string ExecutablePath { get; private set; }

    public string ExecutableName => string.IsNullOrWhiteSpace(ExecutablePath)
        ? "Path unavailable" : Path.GetFileName(ExecutablePath);

    public string Role => ProcessCommandLineResolver.ResolveRole(CommandLine, ExecutablePath);

    public string ProcessIdLabel => $"PID {ProcessId}";

    public string ActionsLabel => $"Actions for {DisplayName}, PID {ProcessId}";

    public string StartPath { get; private set; }

    public long WorkingSetBytes { get; private set; }

    public string Usage { get; private set; }

    public string Note { get; private set; }

    public string CopyText { get; private set; }

    public void UpdateFrom(ProcessKillEntry source)
    {
        if (ProcessId != source.ProcessId) throw new InvalidOperationException("Process identity changed.");
        ParentProcessId = source.ParentProcessId;
        DisplayName = source.DisplayName;
        CommandLine = source.CommandLine;
        ExecutablePath = source.ExecutablePath;
        StartPath = source.StartPath;
        WorkingSetBytes = source.WorkingSetBytes;
        Usage = source.Usage;
        Note = source.Note;
        CopyText = source.CopyText;
        foreach (var property in new[] { nameof(ParentProcessId), nameof(DisplayName), nameof(CommandLine),
            nameof(ExecutablePath), nameof(ExecutableName), nameof(Role), nameof(ActionsLabel),
            nameof(StartPath), nameof(WorkingSetBytes), nameof(Usage), nameof(Note), nameof(CopyText) })
            RaisePropertyChanged(property);
    }

    public bool CanKill => ProcessId > 0;

    public static string BuildCopyText(string displayName, int processId, string executablePath)
    {
        var nameLine = $"{displayName} (PID {processId})";
        return string.IsNullOrWhiteSpace(executablePath)
            ? nameLine
            : $"{nameLine}{Environment.NewLine}{executablePath}";
    }
}
