using DevTools.Services;

namespace DevTools.Models;

public sealed class ProcessViewState
{
    public string FilterText { get; set; } = string.Empty;
    public ProcessSortColumn SortColumn { get; set; } = ProcessSortColumn.Memory;
    public bool SortDescending { get; set; } = true;
    public Dictionary<string, bool> ExpandedSections { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Normalize()
    {
        FilterText ??= string.Empty;
        if (!Enum.IsDefined(SortColumn)) SortColumn = ProcessSortColumn.Memory;
        ExpandedSections = new Dictionary<string, bool>(ExpandedSections ?? [], StringComparer.OrdinalIgnoreCase);
    }

    public void ToggleSort(ProcessSortColumn column)
    {
        SortDescending = SortColumn == column ? !SortDescending : column == ProcessSortColumn.Memory;
        SortColumn = column;
    }
}
