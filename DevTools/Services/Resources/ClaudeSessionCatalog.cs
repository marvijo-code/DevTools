using System.Text;
using System.Text.Json;
using DevTools.Infrastructure;

namespace DevTools.Services.Resources;

/// <summary>Identity of one Claude session, as recorded by the CLI for its own process.</summary>
public sealed record ClaudeSessionInfo(int ProcessId, string SessionId, string WorkingDirectory, string Name);

/// <summary>
/// Resolves the human-readable title of a running Claude session.
///
/// Two files make this possible without guessing. <c>~/.claude/sessions/{pid}.json</c> is a small record
/// the CLI writes for its own process, giving an exact PID to session-id mapping. The session transcript
/// then carries the title, rewritten periodically as the conversation evolves.
///
/// Both lookups are cached, and the transcript is read from its tail rather than in full: transcripts run
/// to megabytes, and re-reading them on a one-second sampling loop would be the kind of I/O this tool
/// exists to catch.
/// </summary>
public sealed class ClaudeSessionCatalog
{
    /// <summary>Enough of the tail to contain a recent title record without reading the whole transcript.</summary>
    private const int TailBytesToScan = 512 * 1024;

    /// <summary>The opening prompt sits on the first line, so a small head read always reaches it.</summary>
    private const int HeadBytesToScan = 128 * 1024;

    /// <summary>Longest title shown before eliding; opening prompts are frequently paragraphs.</summary>
    private const int MaxTitleLength = 80;

    /// <summary>Titles change rarely, so a resolved one is trusted for this long before re-reading.</summary>
    private static readonly TimeSpan TitleCacheLifetime = TimeSpan.FromMinutes(5);

    private readonly string _sessionsDirectory;
    private readonly string _projectsDirectory;
    private readonly Dictionary<int, ClaudeSessionInfo?> _sessionsByPid = new(64);
    private readonly Dictionary<string, CachedTitle> _titlesBySessionId = new(64, StringComparer.OrdinalIgnoreCase);

    public ClaudeSessionCatalog()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"))
    {
    }

    public ClaudeSessionCatalog(string claudeHome)
    {
        _sessionsDirectory = Path.Combine(claudeHome, "sessions");
        _projectsDirectory = Path.Combine(claudeHome, "projects");
    }

    /// <summary>Returns the session title for whichever process in <paramref name="processIds"/> owns one.</summary>
    public string? ResolveTitle(IEnumerable<int> processIds)
    {
        foreach (var processId in processIds)
        {
            var session = TryGetSession(processId);
            if (session is null)
            {
                continue;
            }

            var title = TryGetTitle(session);
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }
        }

        return null;
    }

    public ClaudeSessionInfo? TryGetSession(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        if (_sessionsByPid.TryGetValue(processId, out var cached))
        {
            return cached;
        }

        var session = ReadSessionFile(processId);
        _sessionsByPid[processId] = session;
        return session;
    }

    /// <summary>Drops cache entries for processes that have exited so PID reuse cannot return a stale title.</summary>
    public void RetainOnly(IReadOnlySet<int> livePids)
    {
        if (_sessionsByPid.Count == 0)
        {
            return;
        }

        var stale = _sessionsByPid.Keys.Where(pid => !livePids.Contains(pid)).ToList();
        foreach (var pid in stale)
        {
            _sessionsByPid.Remove(pid);
        }
    }

    private ClaudeSessionInfo? ReadSessionFile(int processId)
    {
        var path = Path.Combine(_sessionsDirectory, $"{processId}.json");

        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            var sessionId = ReadString(root, "sessionId");
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            return new ClaudeSessionInfo(
                processId,
                sessionId,
                ReadString(root, "cwd") ?? string.Empty,
                ReadString(root, "name") ?? string.Empty);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            // A session file being rewritten as we read it is normal; skip this tick.
            return null;
        }
    }

    private string? TryGetTitle(ClaudeSessionInfo session)
    {
        if (_titlesBySessionId.TryGetValue(session.SessionId, out var cached) &&
            DateTimeOffset.UtcNow - cached.ResolvedAt < TitleCacheLifetime)
        {
            return cached.Title;
        }

        var title = ReadTitleFromTranscript(session);
        _titlesBySessionId[session.SessionId] = new CachedTitle(title, DateTimeOffset.UtcNow);
        return title;
    }

    private string? ReadTitleFromTranscript(ClaudeSessionInfo session)
    {
        var transcriptPath = FindTranscript(session);
        if (transcriptPath is null)
        {
            return null;
        }

        try
        {
            // Titles are appended repeatedly as a conversation develops, so the newest lives in the tail.
            var title = ParseTitle(ReadTail(transcriptPath, TailBytesToScan));
            if (!string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            // Many live sessions never get a generated title. Their opening prompt describes the work at
            // least as well, and it is always on the first line, so fall back to that rather than nothing.
            var head = ReadHead(transcriptPath, HeadBytesToScan);
            return ParseTitle(head) ?? ParseFirstPrompt(head);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string? FindTranscript(ClaudeSessionInfo session)
    {
        var workingDirectory = session.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        var candidate = Path.Combine(
            _projectsDirectory,
            EncodeProjectDirectory(workingDirectory),
            $"{session.SessionId}.jsonl");

        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Mirrors the slug Claude uses for project folders: every character that is not a letter, digit or
    /// hyphen becomes a hyphen, so <c>C:\dev\DevTools</c> becomes <c>C--dev-DevTools</c>.
    /// </summary>
    public static string EncodeProjectDirectory(string workingDirectory)
    {
        var builder = new StringBuilder(workingDirectory.Length + 4);

        foreach (var character in workingDirectory.TrimEnd('\\', '/'))
        {
            builder.Append(char.IsLetterOrDigit(character) || character == '-' ? character : '-');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Picks the newest title record out of a transcript fragment. A user-assigned title always wins over
    /// the generated one, regardless of which appears later.
    /// </summary>
    public static string? ParseTitle(string transcriptFragment)
    {
        if (string.IsNullOrWhiteSpace(transcriptFragment))
        {
            return null;
        }

        string? aiTitle = null;
        string? customTitle = null;

        foreach (var line in transcriptFragment.Split('\n'))
        {
            if (line.Length == 0 || line[0] != '{')
            {
                // The first line of a tail read is usually a partial record; skip anything unparseable.
                continue;
            }

            if (!line.Contains("-title\"", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                var type = ReadString(root, "type");
                if (string.Equals(type, "custom-title", StringComparison.Ordinal))
                {
                    customTitle = ReadString(root, "customTitle") ?? customTitle;
                }
                else if (string.Equals(type, "ai-title", StringComparison.Ordinal))
                {
                    aiTitle = ReadString(root, "aiTitle") ?? aiTitle;
                }
            }
            catch (JsonException)
            {
                // Truncated final line of a live transcript; ignore.
            }
        }

        return Tidy(customTitle ?? aiTitle);
    }

    /// <summary>
    /// Extracts the prompt that opened the session. Both the queue record and the user message carry it;
    /// the queue record is written first, so whichever appears earliest in the fragment wins.
    /// </summary>
    public static string? ParseFirstPrompt(string transcriptFragment)
    {
        if (string.IsNullOrWhiteSpace(transcriptFragment))
        {
            return null;
        }

        foreach (var line in transcriptFragment.Split('\n'))
        {
            if (line.Length == 0 || line[0] != '{')
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                var type = ReadString(root, "type");

                if (string.Equals(type, "queue-operation", StringComparison.Ordinal))
                {
                    var queued = Tidy(ReadString(root, "content"));
                    if (queued is not null)
                    {
                        return queued;
                    }

                    continue;
                }

                if (string.Equals(type, "user", StringComparison.Ordinal) &&
                    root.TryGetProperty("message", out var message) &&
                    message.ValueKind == JsonValueKind.Object)
                {
                    // Multimodal turns store content as an array of blocks; only plain text is useful here.
                    var content = Tidy(ReadString(message, "content"));
                    if (content is not null)
                    {
                        return content;
                    }
                }
            }
            catch (JsonException)
            {
                // Partial record at a read boundary; keep looking.
            }
        }

        return null;
    }

    /// <summary>Collapses whitespace and elides so a pasted paragraph still fits on one line.</summary>
    private static string? Tidy(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var collapsed = string.Join(' ', value.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (collapsed.Length == 0)
        {
            return null;
        }

        return collapsed.Length <= MaxTitleLength
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, MaxTitleLength - 1).TrimEnd(), "…");
    }

    private static string ReadTail(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var length = stream.Length;
        var count = (int)Math.Min(maxBytes, length);
        stream.Seek(length - count, SeekOrigin.Begin);

        var buffer = new byte[count];
        stream.ReadExactly(buffer, 0, count);

        return Encoding.UTF8.GetString(buffer);
    }

    private static string ReadHead(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var count = (int)Math.Min(maxBytes, stream.Length);
        var buffer = new byte[count];
        stream.ReadExactly(buffer, 0, count);

        return Encoding.UTF8.GetString(buffer);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private readonly record struct CachedTitle(string? Title, DateTimeOffset ResolvedAt);
}
