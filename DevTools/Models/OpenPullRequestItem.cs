using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace DevTools.Models;

public enum PullRequestReviewState
{
    Approved,
    ChangesRequested,
    AwaitingReview
}

public sealed class OpenPullRequestItem
{
    private static readonly Brush ApprovedBackgroundBrush = CreateBrush(0x1c, 0x1f, 0x8b, 0x4c);
    private static readonly Brush ApprovedBorderBrush = CreateBrush(0x70, 0x1f, 0x8b, 0x4c);
    private static readonly Brush ApprovedAccentBrush = CreateBrush(0xff, 0x1f, 0x8b, 0x4c);
    private static readonly Brush ChangesBackgroundBrush = CreateBrush(0x1c, 0xd1, 0x43, 0x43);
    private static readonly Brush ChangesBorderBrush = CreateBrush(0x70, 0xd1, 0x43, 0x43);
    private static readonly Brush ChangesAccentBrush = CreateBrush(0xff, 0xd1, 0x43, 0x43);
    private static readonly Brush AwaitingBackgroundBrush = CreateBrush(0x14, 0x4b, 0x55, 0x67);
    private static readonly Brush AwaitingBorderBrush = CreateBrush(0x55, 0x4b, 0x55, 0x67);
    private static readonly Brush AwaitingAccentBrush = CreateBrush(0xff, 0x4b, 0x55, 0x67);
    private static readonly Brush WhiteForegroundBrush = CreateBrush(0xff, 0xff, 0xff, 0xff);

    public required RepositoryDefinition Repository { get; init; }

    public required int Number { get; init; }

    public required string Title { get; init; }

    public required string HtmlUrl { get; init; }

    public required string AuthorLogin { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required bool IsDraft { get; init; }

    public required PullRequestReviewState ReviewState { get; init; }

    /// <summary>
    /// Human reviewers whose latest review is an approval. Bot reviewers never count.
    /// </summary>
    public IReadOnlyList<string> Approvers { get; init; } = [];

    /// <summary>
    /// Human reviewers whose latest review still requests changes.
    /// </summary>
    public IReadOnlyList<string> ChangeRequesters { get; init; } = [];

    /// <summary>
    /// TargetProcess ticket number parsed from the "&lt;number&gt; - [Category] - description" title.
    /// Null when the title starts with 0 or carries no ticket at all.
    /// </summary>
    public int? TargetProcessId { get; init; }

    /// <summary>TargetProcess portal root, filled in by the view model so the ticket link can be built.</summary>
    public string TargetProcessPortalUrl { get; set; } = string.Empty;

    public string Reference => $"{Repository.Name} #{Number}";

    public bool HasTargetProcessId => TargetProcessId is > 0;

    public string TargetProcessDisplay => TargetProcessId is > 0 ? TargetProcessId.Value.ToString() : "No ticket";

    public string TargetProcessUrl =>
        TargetProcessId is > 0 && !string.IsNullOrWhiteSpace(TargetProcessPortalUrl)
            ? $"{TargetProcessPortalUrl.TrimEnd('/')}/entity/{TargetProcessId.Value}"
            : string.Empty;

    public bool HasTargetProcessUrl => TargetProcessUrl.Length > 0;

    public int AgeInDays => Math.Max(0, (int)(DateTimeOffset.Now - CreatedAt).TotalDays);

    public string AgeDisplay => $"{AgeInDays}d";

    public string OpenedSummary => $"Opened {CreatedAt.LocalDateTime:yyyy-MM-dd} by @{AuthorLogin}";

    /// <summary>
    /// Approved first, then changes requested, then awaiting review. Oldest first inside each group.
    /// </summary>
    public int ReviewSortRank =>
        ReviewState switch
        {
            PullRequestReviewState.Approved => 0,
            PullRequestReviewState.ChangesRequested => 1,
            _ => 2
        };

    public string ReviewBadgeText =>
        ReviewState switch
        {
            PullRequestReviewState.Approved => Approvers.Count > 1
                ? $"Approved ({Approvers.Count})"
                : "Approved",
            PullRequestReviewState.ChangesRequested => "Changes Requested",
            _ => IsDraft ? "Draft, No Review" : "Awaiting Review"
        };

    public string ReviewerSummary =>
        ReviewState switch
        {
            PullRequestReviewState.Approved => $"Approved by {FormatLogins(Approvers)}",
            PullRequestReviewState.ChangesRequested => $"Changes requested by {FormatLogins(ChangeRequesters)}",
            _ => IsDraft
                ? "Draft. No human review recorded yet."
                : "No human review recorded yet."
        };

    public Brush ReviewBackgroundBrush => GetReviewBrushSet().Background;

    public Brush ReviewBorderBrush => GetReviewBrushSet().Border;

    public Brush ReviewAccentBrush => GetReviewBrushSet().Accent;

    public Brush ReviewBadgeForegroundBrush => WhiteForegroundBrush;

    private (Brush Background, Brush Border, Brush Accent) GetReviewBrushSet()
    {
        return ReviewState switch
        {
            PullRequestReviewState.Approved => (ApprovedBackgroundBrush, ApprovedBorderBrush, ApprovedAccentBrush),
            PullRequestReviewState.ChangesRequested => (ChangesBackgroundBrush, ChangesBorderBrush, ChangesAccentBrush),
            _ => (AwaitingBackgroundBrush, AwaitingBorderBrush, AwaitingAccentBrush)
        };
    }

    private static string FormatLogins(IReadOnlyList<string> logins)
    {
        return logins.Count == 0
            ? "an unnamed reviewer"
            : string.Join(", ", logins.Select(login => $"@{login}"));
    }

    private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
    {
        return new SolidColorBrush(ColorHelper.FromArgb(alpha, red, green, blue));
    }
}
