using DevTools.Infrastructure;
using DevTools.Services.Resources;

namespace DevTools.Models;

/// <summary>
/// One developer process tree rolled up: the agent (or shell) at the root, the workspace it is working in,
/// and everything it spawned.
/// </summary>
public sealed record AgentSessionUsage(
    int RootProcessId,
    string RootName,
    WorkloadCategory Category,
    string WorkspacePath,
    string WorkspaceName,
    DateTimeOffset StartTime,
    int ProcessCount,
    double CpuPercent,
    long CommitBytes,
    long WorkingSetBytes,
    double DiskBytesPerSecond,
    int ThreadCount,
    IReadOnlyList<ProcessUsage> Members,
    double SharePercent,
    string? SessionTitle = null)
{
    /// <summary>True when the agent reported a conversation title for this session.</summary>
    public bool HasSessionTitle => !string.IsNullOrWhiteSpace(SessionTitle);

    /// <summary>The title if the agent gave one, otherwise the workspace it is working in.</summary>
    public string TitleText => HasSessionTitle ? SessionTitle! : WorkspaceName;

    /// <summary>Stable across ticks so the UI can keep a session expanded while it refreshes.</summary>
    public string Key => $"{RootProcessId}|{RootName}";

    public string DisplayName => $"{FriendlyRootName}  ·  {WorkspaceName}";

    public string CommitText => ByteFormatter.Format(CommitBytes);

    /// <summary>
    /// Leads with the session title when there is one - "which agent session is this" is answered far
    /// better by what it is working on than by the executable that happens to be at the root of the tree.
    /// </summary>
    public string HeadlineText => HasSessionTitle
        ? $"{SessionTitle}  —  {CommitText}"
        : $"{DisplayName}  —  {CommitText}";

    /// <summary>Agent and workspace, shown beneath the title so the identity is never lost.</summary>
    public string SubtitleText => HasSessionTitle ? DisplayName : WorkspaceText;

    public string UptimeText => StartTime == DateTimeOffset.MinValue
        ? "unknown age"
        : FormatUptime(DateTimeOffset.Now - StartTime);

    public string DetailText =>
        $"{ProcessCount} process(es) · {CpuPercent:N1}% CPU · working set {ByteFormatter.Format(WorkingSetBytes)} · " +
        $"disk {ByteFormatter.Format((long)DiskBytesPerSecond)}/s · {ThreadCount} threads · up {UptimeText}";

    public string WorkspaceText => WorkspacePath.Length == 0 ? "working directory unavailable" : WorkspacePath;

    /// <summary>
    /// True once a session has been alive long enough that it is more likely forgotten than in use. Stale
    /// dev servers are the classic case: they keep leaking long after the work that started them ended.
    /// </summary>
    public bool IsLongLived => StartTime != DateTimeOffset.MinValue &&
                               DateTimeOffset.Now - StartTime > TimeSpan.FromHours(12);

    private string FriendlyRootName => RootName.ToLowerInvariant() switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        "opencode" => "OpenCode",
        "cursor-agent" => "Cursor Agent",
        "aider" => "Aider",
        "gemini" => "Gemini CLI",
        _ => RootName
    };

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
        {
            return $"{uptime.TotalDays:N1}d";
        }

        return uptime.TotalHours >= 1
            ? $"{uptime.TotalHours:N1}h"
            : $"{Math.Max(1, uptime.TotalMinutes):N0}m";
    }
}
