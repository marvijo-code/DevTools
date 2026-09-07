using DevTools.Models;

namespace DevTools.Services;

public enum ProcessSortColumn { Process, StartPath, Memory }

public static class ProcessKillFilter
{
    public static IReadOnlyList<ProcessKillSection> Apply(
        IReadOnlyList<ProcessKillSection> sections,
        string? filterText,
        ProcessSortColumn sortColumn = ProcessSortColumn.Memory,
        bool descending = true)
    {
        var terms = SplitFilterTerms(filterText);
        var filteredSections = new List<ProcessKillSection>();
        foreach (var section in sections)
        {
            var matchingEntries = section.Entries
                .Where(entry => Matches(entry, section.Title, section.StartPath, terms))
                .ToList();
            matchingEntries = OrderEntries(matchingEntries, sortColumn, descending).ToList();

            if (matchingEntries.Count == 0)
            {
                continue;
            }

            var totalWorkingSet = matchingEntries.Sum(entry => entry.WorkingSetBytes);
            filteredSections.Add(new ProcessKillSection(
                section.Title,
                terms.Count == 0 ? section.Summary : $"{matchingEntries.Count} process(es), {FormatBytes(totalWorkingSet)} working set combined.",
                section.StartPath,
                matchingEntries)
            {
                IsExpanded = section.IsExpanded
            });
        }

        Func<ProcessKillSection, object> key = sortColumn switch
        {
            ProcessSortColumn.Process => section => section.Title,
            ProcessSortColumn.StartPath => section => section.StartPath,
            _ => section => section.Entries.Sum(entry => entry.WorkingSetBytes)
        };
        var ordered = descending
            ? filteredSections.OrderByDescending(key, SortComparer)
            : filteredSections.OrderBy(key, SortComparer);
        return ordered.ThenBy(section => section.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static readonly IComparer<object> SortComparer = Comparer<object>.Create((left, right) =>
        left is string a && right is string b ? StringComparer.OrdinalIgnoreCase.Compare(a, b)
            : Comparer<object>.Default.Compare(left, right));

    private static IEnumerable<ProcessKillEntry> OrderEntries(IEnumerable<ProcessKillEntry> entries, ProcessSortColumn column, bool descending)
    {
        Func<ProcessKillEntry, object> key = column switch
        {
            ProcessSortColumn.Process => entry => entry.DisplayName,
            ProcessSortColumn.StartPath => entry => entry.StartPath,
            _ => entry => entry.WorkingSetBytes
        };
        var ordered = descending ? entries.OrderByDescending(key, SortComparer) : entries.OrderBy(key, SortComparer);
        return ordered.ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.ProcessId);
    }

    private static bool Matches(
        ProcessKillEntry entry,
        string sectionTitle,
        string sectionStartPath,
        IReadOnlyList<string> terms)
    {
        var haystack = string.Join(
            ' ',
            new[]
            {
                sectionTitle,
                sectionStartPath,
                entry.DisplayName,
                entry.CommandLine,
                entry.ExecutablePath,
                entry.StartPath,
                entry.Note,
                entry.ProcessId.ToString(),
                entry.ParentProcessId.ToString(),
                entry.Usage
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> SplitFilterTerms(string? filterText)
    {
        if (string.IsNullOrWhiteSpace(filterText))
        {
            return [];
        }

        return filterText
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Trim())
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
