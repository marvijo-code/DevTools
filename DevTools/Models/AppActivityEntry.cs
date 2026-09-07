namespace DevTools.Models;

public sealed class AppActivityEntry(string id, string title, string kind, string subtitle,
    string description, string reference, int? processId) : DevTools.Infrastructure.ObservableObject
{
    public string Id { get; } = id;
    public string Title { get; private set; } = title;
    public string Kind { get; private set; } = kind;
    public string Subtitle { get; private set; } = subtitle;
    public string Description { get; private set; } = description;
    public string Reference { get; private set; } = reference;
    public int? ProcessId { get; private set; } = processId;
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
    public string MemoryLabel => ProcessId is > 0 ? $"Shared PID {ProcessId}" : "Not available";
    public bool IsFirefoxTab => Kind == "Firefox tab";
    public bool CanEndProcess => IsFirefoxTab && ProcessId is > 0;
    public string EndProcessHint => CanEndProcess ? "End this tab's shared Firefox process"
        : "Connect Firefox to identify this tab's process.";
    public void UpdateFrom(AppActivityEntry source)
    {
        if (Id != source.Id) throw new InvalidOperationException("Activity identity changed.");
        Title = source.Title; Kind = source.Kind; Subtitle = source.Subtitle;
        Description = source.Description; Reference = source.Reference; ProcessId = source.ProcessId;
        foreach (var property in new[] { nameof(Title), nameof(Kind), nameof(Subtitle), nameof(Description),
            nameof(Reference), nameof(ProcessId), nameof(MemoryLabel), nameof(IsFirefoxTab),
            nameof(CanEndProcess), nameof(EndProcessHint) }) RaisePropertyChanged(property);
    }
}
