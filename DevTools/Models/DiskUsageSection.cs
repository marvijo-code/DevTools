using System.Collections.ObjectModel;
using DevTools.Infrastructure;

namespace DevTools.Models;

public sealed class DiskUsageSection : ObservableObject
{
    private string _summary;
    private bool _isExpanded;

    public DiskUsageSection(string title, string summary, IReadOnlyList<DiskUsageEntry> entries)
    {
        Title = title;
        _summary = summary;
        Entries = new ObservableCollection<DiskUsageEntry>(entries);
    }

    public string Title { get; }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public ObservableCollection<DiskUsageEntry> Entries { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public void UpdateFrom(DiskUsageSection source)
    {
        if (!string.Equals(Title, source.Title, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Cannot update disk usage section '{Title}' from '{source.Title}'.");
        }

        Summary = source.Summary;
        SynchronizeEntries(source.Entries);
    }

    private void SynchronizeEntries(IReadOnlyList<DiskUsageEntry> incomingEntries)
    {
        var desiredEntries = incomingEntries
            .GroupBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var incomingPaths = new HashSet<string>(desiredEntries.Select(entry => entry.Path), StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < desiredEntries.Count; i++)
        {
            var incomingEntry = desiredEntries[i];
            var existingIndex = FindEntryIndex(incomingEntry.Path);
            if (existingIndex < 0)
            {
                Entries.Insert(i, incomingEntry);
                continue;
            }

            var existingEntry = Entries[existingIndex];
            existingEntry.UpdateFrom(incomingEntry);
            if (existingIndex != i)
            {
                Entries.Move(existingIndex, i);
            }
        }

        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (!incomingPaths.Contains(Entries[i].Path))
            {
                Entries.RemoveAt(i);
            }
        }
    }

    private int FindEntryIndex(string path)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (string.Equals(Entries[i].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
