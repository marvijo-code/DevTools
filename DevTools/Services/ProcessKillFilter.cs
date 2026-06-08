using DevTools.Models;

namespace DevTools.Services;

public static class ProcessKillFilter
{
    public static IReadOnlyList<ProcessKillSection> Apply(
        IReadOnlyList<ProcessKillSection> sections,
        string? filterText)
    {
        var terms = SplitFilterTerms(filterText);
        if (terms.Count == 0)
        {
            return OrderByMemoryDescending(sections);
        }

        var filteredSections = new List<ProcessKillSection>();
        foreach (var section in sections)
        {
            var matchingEntries = section.Entries
                .Where(entry => Matches(entry, section.Title, section.StartPath, terms))
                .OrderByDescending(entry => entry.WorkingSetBytes)
                .ThenBy(entry => entry.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (matchingEntries.Count == 0)
            {
                continue;
            }

            var totalWorkingSet = matchingEntries.Sum(entry => entry.WorkingSetBytes);
            filteredSections.Add(new ProcessKillSection(
                section.Title,
                $"{matchingEntries.Count} process(es), {FormatBytes(totalWorkingSet)} working set combined.",
                section.StartPath,
                matchingEntries)
            {
                IsExpanded = section.IsExpanded
            });
        }

        return OrderByMemoryDescending(filteredSections);
    }

    private static IReadOnlyList<ProcessKillSection> OrderByMemoryDescending(IReadOnlyList<ProcessKillSection> sections)
    {
        return sections
            .OrderByDescending(section => section.Entries.Sum(entry => entry.WorkingSetBytes))
            .ThenBy(section => section.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
