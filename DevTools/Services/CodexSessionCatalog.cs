using System.Text.Json;

namespace DevTools.Services;

public sealed class CodexSessionCatalog
{
    private readonly Dictionary<string, SessionRecord> _sessionsById = new(StringComparer.OrdinalIgnoreCase);

    public static CodexSessionCatalog LoadDefault()
    {
        var catalog = new CodexSessionCatalog();
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "session_index.jsonl"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                "OpenAI.Codex_2p2nqsd0c76g0",
                "LocalCache",
                "Roaming",
                "Codex",
                "session_index.jsonl")
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            catalog.MergeFromFile(candidate);
        }

        return catalog;
    }

    public string? TryGetThreadTitle(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        return _sessionsById.TryGetValue(threadId, out var record) ? record.ThreadName : null;
    }

    public string? GetMostRecentThreadTitle()
    {
        return _sessionsById.Values
            .OrderByDescending(record => record.UpdatedAt)
            .Select(record => record.ThreadName)
            .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));
    }

    public IReadOnlyList<DevTools.Models.AppActivityEntry> RecentThreads(int count = 30) => _sessionsById
        .OrderByDescending(pair => pair.Value.UpdatedAt).Take(count)
        .Select(pair => new DevTools.Models.AppActivityEntry("codex:" + pair.Key, pair.Value.ThreadName,
            "Codex thread", $"Updated {pair.Value.UpdatedAt.LocalDateTime:g}",
            "From the local Codex thread index. This is a recent thread, not a separate Windows process. Codex does not expose a reliable per-thread memory measurement.",
            pair.Key, null)).ToList();

    public static CodexSessionCatalog FromFile(string path)
    {
        var catalog = new CodexSessionCatalog();
        catalog.MergeFromFile(path);
        return catalog;
    }

    private void MergeFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var idElement) ||
                    !root.TryGetProperty("thread_name", out var titleElement))
                {
                    continue;
                }

                var id = idElement.GetString();
                var title = titleElement.GetString();
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                DateTimeOffset updatedAt = default;
                if (root.TryGetProperty("updated_at", out var updatedAtElement) &&
                    DateTimeOffset.TryParse(updatedAtElement.GetString(), out var parsedUpdatedAt))
                {
                    updatedAt = parsedUpdatedAt;
                }

                if (!_sessionsById.TryGetValue(id, out var existing) || updatedAt >= existing.UpdatedAt)
                {
                    _sessionsById[id] = new SessionRecord(title, updatedAt);
                }
            }
            catch (JsonException)
            {
            }
        }
    }

    private sealed record SessionRecord(string ThreadName, DateTimeOffset UpdatedAt);
}
