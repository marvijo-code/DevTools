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
        var hasFilter = terms.Count > 0;
        var filteredSections = new List<ProcessKillSection>(sections.Count);
        foreach (var section in sections)
        {
            IReadOnlyList<AppActivityEntry> activities = section.Activities;
            IReadOnlyList<ProcessKillEntry> matchingEntries = section.Entries;
            if (hasFilter)
            {
                activities = section.Activities
                    .Where(activity => MatchesActivity(activity, section.Title, terms))
                    .ToList();
                matchingEntries = section.Entries
                    .Where(entry => Matches(entry, section.Title, section.StartPath, terms))
                    .ToList();

                // A thread or tab match still belongs to this app's shared processes.
                if (activities.Count > 0 && matchingEntries.Count == 0)
                {
                    matchingEntries = section.Entries;
                }
            }

            if (matchingEntries.Count == 0 && activities.Count == 0)
            {
                continue;
            }

            var summary = hasFilter
                ? $"{matchingEntries.Count} {(matchingEntries.Count == 1 ? "process" : "processes")} · {FormatBytes(matchingEntries.Sum(entry => entry.WorkingSetBytes))} total memory"
                : section.Summary;
            filteredSections.Add(new ProcessKillSection(
                section.Title,
                summary,
                section.StartPath,
                OrderEntries(matchingEntries, sortColumn, descending).ToList(), activities, section.ActivityHeading, section.ActivityStatus)
            {
                IsExpanded = section.IsExpanded,
                ProcessColumnLabel = "Process" + (sortColumn == ProcessSortColumn.Process ? descending ? " ↓" : " ↑" : ""),
                PathColumnLabel = "Executable" + (sortColumn == ProcessSortColumn.StartPath ? descending ? " ↓" : " ↑" : ""),
                MemoryColumnLabel = "Memory" + (sortColumn == ProcessSortColumn.Memory ? descending ? " ↓" : " ↑" : "")
            });
        }

        Func<ProcessKillSection, object> key = sortColumn switch
        {
            ProcessSortColumn.Process => section => section.Title,
            ProcessSortColumn.StartPath => section => section.StartPath,
            _ => section => section.Entries.Sum(entry => entry.WorkingSetBytes)
        };
        var byActivity = filteredSections.OrderByDescending(section => section.HasActivitySource);
        var ordered = descending
            ? byActivity.ThenByDescending(key, SortComparer)
            : byActivity.ThenBy(key, SortComparer);
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
        // The query is split on spaces, so a term cannot span the separator between fields.
        string? processId = null;
        string? parentProcessId = null;
        for (var i = 0; i < terms.Count; i++)
        {
            var term = terms[i];
            if (ContainsTerm(sectionTitle, term) ||
                ContainsTerm(sectionStartPath, term) ||
                ContainsTerm(entry.DisplayName, term) ||
                ContainsTerm(entry.CommandLine, term) ||
                ContainsTerm(entry.ExecutablePath, term) ||
                ContainsTerm(entry.StartPath, term) ||
                ContainsTerm(entry.Note, term) ||
                ContainsTerm(entry.Usage, term) ||
                ContainsTerm(processId ??= entry.ProcessId.ToString(), term) ||
                ContainsTerm(parentProcessId ??= entry.ParentProcessId.ToString(), term))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool MatchesActivity(AppActivityEntry activity, string sectionTitle, IReadOnlyList<string> terms)
    {
        for (var i = 0; i < terms.Count; i++)
        {
            var term = terms[i];
            if (!ContainsTerm(sectionTitle, term) &&
                !ContainsTerm(activity.Title, term) &&
                !ContainsTerm(activity.Kind, term) &&
                !ContainsTerm(activity.Reference, term))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsTerm(string? value, string term) =>
        value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;

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
