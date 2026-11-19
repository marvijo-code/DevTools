using System.Collections.ObjectModel;
using DevTools.Infrastructure;

namespace DevTools.Models;

public sealed class DiskUsageSection : ObservableObject
{
    private string _summary;
    private bool _isExpanded;
    private WeakReference<DiskUsageSection>? _lastScanSource;

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
        _lastScanSource = null;
        CopyFrom(source);
    }

    internal void UpdateFromScan(DiskUsageSection source)
    {
        // The analyzer supplies a new section when its scan results change.
        if (_lastScanSource?.TryGetTarget(out var previous) == true && ReferenceEquals(previous, source))
        {
            return;
        }

        CopyFrom(source);
        if (_lastScanSource is null)
        {
            _lastScanSource = new WeakReference<DiskUsageSection>(source);
        }
        else
        {
            _lastScanSource.SetTarget(source);
        }
    }

    private void CopyFrom(DiskUsageSection source)
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
            .GroupBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var incomingPaths = new HashSet<string>(desiredEntries.Select(entry => entry.Path), StringComparer.Ordinal);

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
            if (string.Equals(Entries[i].Path, path, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
