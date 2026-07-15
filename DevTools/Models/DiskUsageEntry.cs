using DevTools.Infrastructure;

namespace DevTools.Models;

public sealed class DiskUsageEntry : ObservableObject
{
    private string _usage;
    private string _note;
    private string? _displayName;
    private bool _isMeasuring;
    private bool _existsOnDisk = true;
    private bool _isExpanded;
    private long? _sizeBytes;
    private DateTimeOffset? _measurementStartedAt;
    private IReadOnlyList<DiskUsageEntry> _children = [];

    public DiskUsageEntry(string path, string usage, string? note = null)
    {
        Path = path;
        _usage = usage;
        _note = note ?? string.Empty;
    }

    public string Path { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(_displayName) ? Path : _displayName!;

    public bool HasDistinctDisplayName => !string.IsNullOrWhiteSpace(_displayName) &&
                                          !string.Equals(_displayName, Path, StringComparison.Ordinal);

    public string Usage
    {
        get => _usage;
        private set => SetProperty(ref _usage, value);
    }

    public string Note
    {
        get => _note;
        private set
        {
            if (SetProperty(ref _note, value))
            {
                RaisePropertyChanged(nameof(HasNote));
            }
        }
    }

    public bool IsMeasuring
    {
        get => _isMeasuring;
        private set => SetProperty(ref _isMeasuring, value);
    }

    public long? SizeBytes
    {
        get => _sizeBytes;
        private set => SetProperty(ref _sizeBytes, value);
    }

    public bool ExistsOnDisk
    {
        get => _existsOnDisk;
        private set => SetProperty(ref _existsOnDisk, value);
    }

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    public bool CanClear => !IsMeasuring && Directory.Exists(Path) && IsClearableDirectory(Path);

    public IReadOnlyList<DiskUsageEntry> Children => _children;

    public bool HasChildren => _children.Count > 0;

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

    public static DiskUsageEntry CreateQueued(string path, string? note = null)
    {
        return new DiskUsageEntry(path, "Queued", note ?? "Waiting for an analyzer worker.");
    }

    public static DiskUsageEntry CreateCompleted(string path, bool existsOnDisk, long? sizeBytes, string usage, string? note = null, string? displayName = null)
    {
        var entry = new DiskUsageEntry(path, usage, note);
        entry._displayName = displayName;
        entry._measurementStartedAt = null;
        entry.ExistsOnDisk = existsOnDisk;
        entry.SizeBytes = sizeBytes;
        entry.IsMeasuring = false;
        return entry;
    }

    public void SetDisplayName(string? displayName)
    {
        if (SetProperty(ref _displayName, displayName))
        {
            RaisePropertyChanged(nameof(DisplayName));
            RaisePropertyChanged(nameof(HasDistinctDisplayName));
        }
    }

    public void UpdateFrom(DiskUsageEntry source)
    {
        if (!string.Equals(Path, source.Path, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Cannot update disk usage entry '{Path}' from '{source.Path}'.");
        }

        _measurementStartedAt = source._measurementStartedAt;
        ExistsOnDisk = source.ExistsOnDisk;
        SizeBytes = source.SizeBytes;
        IsMeasuring = source.IsMeasuring;
        Usage = source.Usage;
        Note = source.Note;
        _displayName = source._displayName;
        RaisePropertyChanged(nameof(DisplayName));
        RaisePropertyChanged(nameof(HasDistinctDisplayName));
        SetChildren(source.Children);
        RaisePropertyChanged(nameof(CanClear));
    }

    public void SetChildren(IEnumerable<DiskUsageEntry> children)
    {
        _children = children
            .OrderByDescending(child => child.SizeBytes ?? -1)
            .ThenBy(child => child.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        RaisePropertyChanged(nameof(Children));
        RaisePropertyChanged(nameof(HasChildren));
    }

    public void StartMeasurement(string? note = null, DateTimeOffset? startedAt = null)
    {
        _measurementStartedAt = startedAt ?? DateTimeOffset.Now;
        IsMeasuring = true;
        Usage = $"Measuring... {FormatDuration(TimeSpan.Zero)}";
        Note = note ?? "Scanning this path now.";
        RaisePropertyChanged(nameof(CanClear));
    }

    public void CompleteMeasurement(bool existsOnDisk, long? sizeBytes, string usage, string? note, TimeSpan elapsed)
    {
        _measurementStartedAt = null;
        ExistsOnDisk = existsOnDisk;
        SizeBytes = sizeBytes;
        IsMeasuring = false;
        Usage = usage;
        Note = BuildCompletedNote(note, elapsed);
        RaisePropertyChanged(nameof(CanClear));
    }

    public void RefreshLiveState(DateTimeOffset now)
    {
        if (!IsMeasuring || _measurementStartedAt is null)
        {
            return;
        }

        Usage = $"Measuring... {FormatDuration(now - _measurementStartedAt.Value)}";
    }

    private static bool IsClearableDirectory(string rawPath)
    {
        try
        {
            var fullPath = NormalizePath(System.IO.Path.GetFullPath(rawPath));
            if (string.IsNullOrWhiteSpace(fullPath) || fullPath.Length <= 3)
            {
                return false;
            }

            var protectedPaths = new[]
            {
                NormalizePath(System.IO.Path.GetPathRoot(fullPath) ?? string.Empty),
                NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)),
                NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
                NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
                NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
                NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
                NormalizePath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
                NormalizePath(System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    "AppData",
                    "LocalLow"))
            };

            return !protectedPaths.Contains(fullPath, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePath(string path)
    {
        return path.Trim().TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }

    private static string BuildCompletedNote(string? note, TimeSpan elapsed)
    {
        var durationText = $"Completed in {FormatDuration(elapsed)}.";
        if (string.IsNullOrWhiteSpace(note))
        {
            return durationText;
        }

        return $"{note} {durationText}";
    }

    private static string FormatDuration(TimeSpan elapsed)
    {
        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }
}
