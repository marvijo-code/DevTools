using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace DevTools.Models;

public sealed class TargetProcessTicketItem
{
    private static readonly Brush ProgressBackgroundBrush = CreateBrush(0x14, 0x25, 0x62, 0xeb);
    private static readonly Brush ProgressBorderBrush = CreateBrush(0x55, 0x25, 0x62, 0xeb);
    private static readonly Brush ProgressAccentBrush = CreateBrush(0xff, 0x25, 0x62, 0xeb);
    private static readonly Brush ReviewBackgroundBrush = CreateBrush(0x18, 0xd9, 0x77, 0x06);
    private static readonly Brush ReviewBorderBrush = CreateBrush(0x70, 0xd9, 0x77, 0x06);
    private static readonly Brush ReviewAccentBrush = CreateBrush(0xff, 0xd9, 0x77, 0x06);
    private static readonly Brush AttentionBackgroundBrush = CreateBrush(0x18, 0xd1, 0x43, 0x43);
    private static readonly Brush AttentionBorderBrush = CreateBrush(0x70, 0xd1, 0x43, 0x43);
    private static readonly Brush AttentionAccentBrush = CreateBrush(0xff, 0xd1, 0x43, 0x43);
    private static readonly Brush WaitingBackgroundBrush = CreateBrush(0x14, 0x4b, 0x55, 0x67);
    private static readonly Brush WaitingBorderBrush = CreateBrush(0x55, 0x4b, 0x55, 0x67);
    private static readonly Brush WaitingAccentBrush = CreateBrush(0xff, 0x4b, 0x55, 0x67);
    private static readonly Brush WhiteForegroundBrush = CreateBrush(0xff, 0xff, 0xff, 0xff);

    public required int Id { get; init; }

    public required string EntityType { get; init; }

    public required string Name { get; init; }

    public required string State { get; init; }

    public required string Project { get; init; }

    public required DateTimeOffset ModifyDate { get; init; }

    /// <summary>
    /// Open pull requests whose title carries this ticket number, filled in after both sources load.
    /// </summary>
    public IReadOnlyList<string> LinkedPullRequestReferences { get; set; } = [];

    /// <summary>TargetProcess portal root, filled in by the client so the ticket link can be built.</summary>
    public string PortalBaseUrl { get; set; } = string.Empty;

    public string Url =>
        string.IsNullOrWhiteSpace(PortalBaseUrl) ? string.Empty : $"{PortalBaseUrl.TrimEnd('/')}/entity/{Id}";

    public bool HasUrl => Url.Length > 0;

    public string Reference => $"{Id} - {EntityType}";

    public int AgeInDays => Math.Max(0, (int)(DateTimeOffset.Now - ModifyDate).TotalDays);

    public string AgeDisplay => $"{AgeInDays}d";

    public string LastTouchedSummary => $"Last touched {ModifyDate.LocalDateTime:yyyy-MM-dd}  |  {Project}";

    public bool HasLinkedPullRequests => LinkedPullRequestReferences.Count > 0;

    public string LinkedPullRequestSummary =>
        LinkedPullRequestReferences.Count == 0
            ? "No open PR"
            : string.Join(", ", LinkedPullRequestReferences);

    /// <summary>
    /// Year-long administrative buckets (leave, meetings, deployment logs) stay open all year and never
    /// carry a PR, so they are listed apart from real delivery work rather than dropped.
    /// </summary>
    public bool IsRecurringTracker => LooksLikeRecurringTracker(Name, Project);

    public string StateCategory => GetStateCategory();

    public Brush StateBackgroundBrush => GetStateBrushSet().Background;

    public Brush StateBorderBrush => GetStateBrushSet().Border;

    public Brush StateAccentBrush => GetStateBrushSet().Accent;

    public Brush StateBadgeForegroundBrush => WhiteForegroundBrush;

    /// <summary>
    /// TargetProcess stores some state names with an embedded newline, for example "Work\nIn Progress Pending".
    /// </summary>
    public string StateDisplay => CompactText(State);

    private string GetStateCategory()
    {
        var normalized = CompactText(State).Trim().ToLowerInvariant();

        if (normalized.Contains("not yet fixed", StringComparison.Ordinal) ||
            normalized.Contains("re-opened", StringComparison.Ordinal) ||
            normalized.Contains("reopened", StringComparison.Ordinal))
        {
            return "Needs Attention";
        }

        if (normalized.Contains("await", StringComparison.Ordinal) ||
            normalized.Contains("third party", StringComparison.Ordinal))
        {
            return "Waiting";
        }

        if (normalized.Contains("review", StringComparison.Ordinal) ||
            normalized.Contains("test", StringComparison.Ordinal))
        {
            return "In Review";
        }

        if (normalized.Contains("in progress", StringComparison.Ordinal))
        {
            return "In Progress";
        }

        return "Queued";
    }

    private (Brush Background, Brush Border, Brush Accent) GetStateBrushSet()
    {
        return StateCategory switch
        {
            "Needs Attention" => (AttentionBackgroundBrush, AttentionBorderBrush, AttentionAccentBrush),
            "In Review" => (ReviewBackgroundBrush, ReviewBorderBrush, ReviewAccentBrush),
            "In Progress" => (ProgressBackgroundBrush, ProgressBorderBrush, ProgressAccentBrush),
            _ => (WaitingBackgroundBrush, WaitingBorderBrush, WaitingAccentBrush)
        };
    }

    private static bool LooksLikeRecurringTracker(string name, string project)
    {
        var normalizedName = name.Trim().ToLowerInvariant();
        var normalizedProject = project.Trim().ToLowerInvariant();

        var isAdministrativeProject =
            normalizedProject.Contains("time off", StringComparison.Ordinal) ||
            normalizedProject.Contains("internal administration", StringComparison.Ordinal);

        var readsLikeAYearlyBucket =
            normalizedName.Contains("leave", StringComparison.Ordinal) ||
            normalizedName.Contains("public holiday", StringComparison.Ordinal) ||
            normalizedName.Contains("meeting", StringComparison.Ordinal) ||
            normalizedName.Contains("team building", StringComparison.Ordinal) ||
            normalizedName.Contains("training provided", StringComparison.Ordinal) ||
            normalizedName.Contains("deployments 2", StringComparison.Ordinal) ||
            normalizedName.Contains("hardware", StringComparison.Ordinal);

        return isAdministrativeProject || readsLikeAYearlyBucket;
    }

    private static string CompactText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value
            .Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
    {
        return new SolidColorBrush(ColorHelper.FromArgb(alpha, red, green, blue));
    }
}
