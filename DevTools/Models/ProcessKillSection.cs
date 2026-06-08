using System.Collections.ObjectModel;
using DevTools.Infrastructure;

namespace DevTools.Models;

public sealed class ProcessKillSection : ObservableObject
{
    private string _summary;
    private string _startPath = string.Empty;
    private bool _isExpanded;

    public ProcessKillSection(string title, string summary, string startPath, IReadOnlyList<ProcessKillEntry> entries)
    {
        Title = title;
        _summary = summary;
        _startPath = startPath;
        Entries = new ObservableCollection<ProcessKillEntry>(entries);
    }

    public string Title { get; }

    public string StartPath
    {
        get => _startPath;
        private set => SetProperty(ref _startPath, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public ObservableCollection<ProcessKillEntry> Entries { get; }

    public string ExpandGlyph => IsExpanded ? "−" : "+";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                RaisePropertyChanged(nameof(ExpandGlyph));
            }
        }
    }

    public void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    public void UpdateFrom(ProcessKillSection source)
    {
        if (!string.Equals(Title, source.Title, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Cannot update process section '{Title}' from '{source.Title}'.");
        }

        Summary = source.Summary;
        StartPath = source.StartPath;
        SynchronizeEntries(source.Entries);
    }

    private void SynchronizeEntries(IReadOnlyList<ProcessKillEntry> incomingEntries)
    {
        var desiredEntries = incomingEntries
            .GroupBy(entry => entry.ProcessId)
            .Select(group => group.First())
            .ToList();
        var incomingProcessIds = new HashSet<int>(desiredEntries.Select(entry => entry.ProcessId));

        for (var i = 0; i < desiredEntries.Count; i++)
        {
            var incomingEntry = desiredEntries[i];
            var existingIndex = FindEntryIndex(incomingEntry.ProcessId);
            if (existingIndex < 0)
            {
                Entries.Insert(i, incomingEntry);
                continue;
            }

            if (existingIndex != i)
            {
                Entries.Move(existingIndex, i);
            }
        }

        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (!incomingProcessIds.Contains(Entries[i].ProcessId))
            {
                Entries.RemoveAt(i);
            }
        }
    }

    private int FindEntryIndex(int processId)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].ProcessId == processId)
            {
                return i;
            }
        }

        return -1;
    }
}
