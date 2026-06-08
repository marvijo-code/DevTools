using DevTools.Infrastructure;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace DevTools.Models;

public sealed class WorkflowRunItem : ObservableObject
{
    private static readonly Brush SuccessBackgroundBrush = CreateBrush(0x16, 0x1f, 0x8b, 0x4c);
    private static readonly Brush SuccessBorderBrush = CreateBrush(0x70, 0x1f, 0x8b, 0x4c);
    private static readonly Brush SuccessForegroundBrush = CreateBrush(0xff, 0x0d, 0x53, 0x29);
    private static readonly Brush FailureBackgroundBrush = CreateBrush(0x16, 0xd1, 0x43, 0x43);
    private static readonly Brush FailureBorderBrush = CreateBrush(0x70, 0xd1, 0x43, 0x43);
    private static readonly Brush FailureForegroundBrush = CreateBrush(0xff, 0x8b, 0x1e, 0x1e);
    private static readonly Brush WarningBackgroundBrush = CreateBrush(0x18, 0xd9, 0x77, 0x06);
    private static readonly Brush WarningBorderBrush = CreateBrush(0x70, 0xd9, 0x77, 0x06);
    private static readonly Brush WarningForegroundBrush = CreateBrush(0xff, 0x7c, 0x4a, 0x03);
    private static readonly Brush NeutralBackgroundBrush = CreateBrush(0x12, 0x25, 0x62, 0xeb);
    private static readonly Brush NeutralBorderBrush = CreateBrush(0x55, 0x25, 0x62, 0xeb);
    private static readonly Brush NeutralForegroundBrush = CreateBrush(0xff, 0x17, 0x3e, 0xb1);
    private static readonly Brush WhiteTagForegroundBrush = CreateBrush(0xff, 0xff, 0xff, 0xff);
    private static readonly Brush PullRequestTagBackgroundBrush = CreateBrush(0xe6, 0x9a, 0x34, 0x1e);
    private static readonly Brush WorkflowRunTagBackgroundBrush = CreateBrush(0xe6, 0x1f, 0x5b, 0xd8);
    private static readonly Brush PushTagBackgroundBrush = CreateBrush(0xe6, 0x1f, 0x8b, 0x4c);
    private static readonly Brush ScheduledTagBackgroundBrush = CreateBrush(0xe6, 0xa3, 0x5a, 0x00);
    private static readonly Brush DispatchTagBackgroundBrush = CreateBrush(0xe6, 0x5f, 0x35, 0xa5);
    private static readonly Brush OtherEventTagBackgroundBrush = CreateBrush(0xe6, 0x4b, 0x55, 0x67);
    private static readonly Brush PipelineBuildTagBackgroundBrush = CreateBrush(0xe6, 0x1b, 0x6f, 0xb1);
    private static readonly Brush ValidationTagBackgroundBrush = CreateBrush(0xe6, 0x17, 0x79, 0x4c);
    private static readonly Brush DeployTagBackgroundBrush = CreateBrush(0xe6, 0x9d, 0x36, 0x06);
    private static readonly Brush SecurityTagBackgroundBrush = CreateBrush(0xe6, 0x98, 0x28, 0x38);
    private static readonly Brush OpsTagBackgroundBrush = CreateBrush(0xe6, 0x46, 0x5b, 0x72);
    private static readonly Brush GeneralTagBackgroundBrush = CreateBrush(0xe6, 0x5e, 0x6a, 0x7a);

    private string? _actualBranch;
    private string? _actualSha;
    private string? _actualCommitUrl;
    private string? _pullRequestUrl;
    private string? _pullRequestTitle;
    private string? _pullRequestDescription;
    private string? _commitDescription;
    private string? _commitAuthorDisplay;
    private DateTimeOffset? _runStartedAt;
    private string? _pullRequestAuthor;
    private string? _upstreamRunUrl;
    private string? _upstreamRunLabel;
    private string? _detectJobUrl;
    private string? _setBranchJobUrl;
    private string? _resolutionError;
    private bool _isResolving;

    public required RepositoryDefinition Repository { get; init; }

    public required long Id { get; init; }

    public required string WorkflowName { get; init; }

    public required string DisplayTitle { get; init; }

    public required string EventName { get; init; }

    public required string UiBranch { get; init; }

    public required string UiSha { get; init; }

    public required string Status { get; init; }

    public required string Conclusion { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public required string RunUrl { get; init; }

    public required string DisplayedCommitUrl { get; init; }

    public required string ActorLogin { get; init; }

    public required string TriggeringActorLogin { get; init; }

    public IReadOnlyList<int> AssociatedPullRequestNumbers { get; init; } = [];

    public bool IsWorkflowRun => string.Equals(EventName, "workflow_run", StringComparison.OrdinalIgnoreCase);
    public bool IsManualDispatch => string.Equals(EventName, "workflow_dispatch", StringComparison.OrdinalIgnoreCase);

    public string UiSummary => $"{UiBranch} @ {ShortSha(UiSha)}";

    public string WorkflowMetaSummary => $"{UiBranch}  |  {DisplayAuthor}  |  {EventName}";

    public string ActualSummary =>
        string.IsNullOrWhiteSpace(ActualSha)
            ? (IsWorkflowRun ? "Select this run to resolve the actual trigger branch." : UiSummary)
            : $"{ActualBranch} @ {ShortSha(ActualSha)}";

    public string StatusSummary =>
        string.IsNullOrWhiteSpace(Conclusion) || string.Equals(Conclusion, "null", StringComparison.OrdinalIgnoreCase)
            ? Status
            : $"{Status} / {Conclusion}";

    public string StatusBadgeText =>
        string.IsNullOrWhiteSpace(Conclusion) || string.Equals(Conclusion, "null", StringComparison.OrdinalIgnoreCase)
            ? ToDisplayLabel(Status)
            : ToDisplayLabel(Conclusion);

    public string StatusCategory => GetStatusCategory();

    public string EventTagText => GetEventTagText();

    public Brush EventTagBackgroundBrush => GetEventTagBrushSet().Background;

    public Brush EventTagForegroundBrush => GetEventTagBrushSet().Foreground;

    public string WorkflowTagText => GetWorkflowTagText();

    public Brush WorkflowTagBackgroundBrush => GetWorkflowTagBrushSet().Background;

    public Brush WorkflowTagForegroundBrush => GetWorkflowTagBrushSet().Foreground;

    public string PullRequestSummary =>
        string.IsNullOrWhiteSpace(PullRequestUrl)
            ? "No pull request resolved."
            : string.IsNullOrWhiteSpace(PullRequestTitle)
                ? PullRequestUrl!
                : PullRequestTitle!;

    public string PullRequestDescription =>
        string.IsNullOrWhiteSpace(_pullRequestDescription)
            ? "PR description not available."
            : _pullRequestDescription!;

    public string PullRequestDescriptionPreview =>
        string.IsNullOrWhiteSpace(_pullRequestDescription)
            ? string.Empty
            : Truncate(CompactText(_pullRequestDescription), 180);

    public string RunDescription =>
        !string.IsNullOrWhiteSpace(_pullRequestDescription)
            ? _pullRequestDescription!
            : !string.IsNullOrWhiteSpace(_commitDescription)
                ? _commitDescription!
                : "Run description not available.";

    public string RunDescriptionPreview =>
        string.IsNullOrWhiteSpace(RunDescriptionRaw)
            ? string.Empty
            : Truncate(CompactText(RunDescriptionRaw), 180);

    public string RunDescriptionHeader =>
        !string.IsNullOrWhiteSpace(_pullRequestDescription)
            ? "Pull Request Description"
            : !string.IsNullOrWhiteSpace(_commitDescription)
                ? "Commit Description"
                : "Run Description";

    public string? RunDescriptionRaw =>
        !string.IsNullOrWhiteSpace(_pullRequestDescription)
            ? _pullRequestDescription
            : _commitDescription;

    public string UpstreamRunSummary =>
        string.IsNullOrWhiteSpace(UpstreamRunUrl)
            ? "No upstream run resolved."
            : string.IsNullOrWhiteSpace(UpstreamRunLabel)
                ? UpstreamRunUrl!
                : UpstreamRunLabel!;

    public string DisplayAuthor =>
        !string.IsNullOrWhiteSpace(PullRequestAuthor)
            ? $"@{PullRequestAuthor}"
            : !string.IsNullOrWhiteSpace(_commitAuthorDisplay)
                ? _commitAuthorDisplay!
            : !string.IsNullOrWhiteSpace(ActorLogin)
                ? $"@{ActorLogin}"
                : "unknown author";

    public string CreatedAtDisplay => StartedAtDisplay;

    public string StartedAtDisplay => EffectiveExecutionStart.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public string UpdatedAtDisplay => UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public string DurationSummary
    {
        get
        {
            var durationStart = RunStartedAt ?? (IsActivelyRunning ? null : CreatedAt);
            if (durationStart is null)
            {
                return "0s";
            }

            var durationEnd = IsActivelyRunning ? DateTimeOffset.Now : UpdatedAt;
            return FormatDuration(durationEnd > durationStart ? durationEnd - durationStart.Value : TimeSpan.Zero);
        }
    }

    public string TimelineSummary => $"{StartedAtDisplay}  |  {DurationSummary}";

    public string UiShaDisplay => ShortSha(UiSha);

    public string ActualShaDisplay => ShortSha(ActualSha);

    public DateTimeOffset EffectiveExecutionStart => RunStartedAt ?? CreatedAt;

    public bool IsActivelyRunning
    {
        get
        {
            var normalizedStatus = Status.Trim().ToLowerInvariant();
            return normalizedStatus is "in_progress" or "requested" or "waiting";
        }
    }

    public string TriggerExplanation =>
        IsWorkflowRun
            ? "GitHub records workflow_run runs against the default branch in the UI. The actual trigger branch and SHA are resolved from job logs."
            : "This run was triggered directly, so the displayed branch and SHA are the trigger.";

    public Brush StatusBackgroundBrush => GetStatusBrushSet().Background;

    public Brush StatusBorderBrush => GetStatusBrushSet().Border;

    public Brush StatusForegroundBrush => GetStatusBrushSet().Foreground;

    public string ActualBranch
    {
        get => string.IsNullOrWhiteSpace(_actualBranch) ? UiBranch : _actualBranch!;
        set
        {
            if (SetProperty(ref _actualBranch, value))
            {
                RaisePropertyChanged(nameof(ActualSummary));
            }
        }
    }

    public string ActualSha
    {
        get => string.IsNullOrWhiteSpace(_actualSha) ? UiSha : _actualSha!;
        set
        {
            if (SetProperty(ref _actualSha, value))
            {
                RaisePropertyChanged(nameof(ActualSummary));
            }
        }
    }

    public string? ActualCommitUrl
    {
        get => _actualCommitUrl;
        set
        {
            if (SetProperty(ref _actualCommitUrl, value))
            {
                RaisePropertyChanged(nameof(HasActualCommitUrl));
            }
        }
    }

    public string? PullRequestUrl
    {
        get => _pullRequestUrl;
        set
        {
            if (SetProperty(ref _pullRequestUrl, value))
            {
                RaisePropertyChanged(nameof(PullRequestSummary));
                RaisePropertyChanged(nameof(HasPullRequestUrl));
                RaisePropertyChanged(nameof(HasTriggerDetails));
                RaiseTagProperties();
            }
        }
    }

    public string? PullRequestTitle
    {
        get => _pullRequestTitle;
        set
        {
            if (SetProperty(ref _pullRequestTitle, value))
            {
                RaisePropertyChanged(nameof(PullRequestSummary));
            }
        }
    }

    public string? PullRequestDescriptionRaw
    {
        get => _pullRequestDescription;
        set
        {
            if (SetProperty(ref _pullRequestDescription, value))
            {
                RaisePropertyChanged(nameof(PullRequestDescription));
                RaisePropertyChanged(nameof(PullRequestDescriptionPreview));
                RaisePropertyChanged(nameof(HasPullRequestDescription));
                RaisePropertyChanged(nameof(RunDescription));
                RaisePropertyChanged(nameof(RunDescriptionPreview));
                RaisePropertyChanged(nameof(RunDescriptionHeader));
                RaisePropertyChanged(nameof(RunDescriptionRaw));
                RaisePropertyChanged(nameof(HasRunDescription));
            }
        }
    }

    public string? CommitDescriptionRaw
    {
        get => _commitDescription;
        set
        {
            if (SetProperty(ref _commitDescription, value))
            {
                RaisePropertyChanged(nameof(RunDescription));
                RaisePropertyChanged(nameof(RunDescriptionPreview));
                RaisePropertyChanged(nameof(RunDescriptionHeader));
                RaisePropertyChanged(nameof(RunDescriptionRaw));
                RaisePropertyChanged(nameof(HasRunDescription));
            }
        }
    }

    public string? CommitAuthorDisplay
    {
        get => _commitAuthorDisplay;
        set
        {
            if (SetProperty(ref _commitAuthorDisplay, value))
            {
                RaisePropertyChanged(nameof(DisplayAuthor));
                RaisePropertyChanged(nameof(WorkflowMetaSummary));
            }
        }
    }

    public DateTimeOffset? RunStartedAt
    {
        get => _runStartedAt;
        set
        {
            if (SetProperty(ref _runStartedAt, value))
            {
                RaisePropertyChanged(nameof(CreatedAtDisplay));
                RaisePropertyChanged(nameof(StartedAtDisplay));
                RaisePropertyChanged(nameof(DurationSummary));
                RaisePropertyChanged(nameof(TimelineSummary));
            }
        }
    }

    public string? PullRequestAuthor
    {
        get => _pullRequestAuthor;
        set
        {
            if (SetProperty(ref _pullRequestAuthor, value))
            {
                RaisePropertyChanged(nameof(DisplayAuthor));
                RaisePropertyChanged(nameof(WorkflowMetaSummary));
            }
        }
    }

    public string? UpstreamRunUrl
    {
        get => _upstreamRunUrl;
        set
        {
            if (SetProperty(ref _upstreamRunUrl, value))
            {
                RaisePropertyChanged(nameof(UpstreamRunSummary));
                RaisePropertyChanged(nameof(HasUpstreamRunUrl));
                RaisePropertyChanged(nameof(HasTriggerDetails));
            }
        }
    }

    public string? UpstreamRunLabel
    {
        get => _upstreamRunLabel;
        set
        {
            if (SetProperty(ref _upstreamRunLabel, value))
            {
                RaisePropertyChanged(nameof(UpstreamRunSummary));
            }
        }
    }

    public string? DetectJobUrl
    {
        get => _detectJobUrl;
        set
        {
            if (SetProperty(ref _detectJobUrl, value))
            {
                RaisePropertyChanged(nameof(HasDetectJobUrl));
                RaisePropertyChanged(nameof(HasTriggerDetails));
            }
        }
    }

    public string? SetBranchJobUrl
    {
        get => _setBranchJobUrl;
        set
        {
            if (SetProperty(ref _setBranchJobUrl, value))
            {
                RaisePropertyChanged(nameof(HasSetBranchJobUrl));
                RaisePropertyChanged(nameof(HasTriggerDetails));
            }
        }
    }

    public string? ResolutionError
    {
        get => _resolutionError;
        set
        {
            if (SetProperty(ref _resolutionError, value))
            {
                RaiseStatusBrushProperties();
                RaisePropertyChanged(nameof(HasTriggerDetails));
            }
        }
    }

    public bool IsResolving
    {
        get => _isResolving;
        set
        {
            if (SetProperty(ref _isResolving, value))
            {
                RaiseStatusBrushProperties();
            }
        }
    }

    public bool HasPullRequestUrl => !string.IsNullOrWhiteSpace(PullRequestUrl);

    public bool HasPullRequestDescription => !string.IsNullOrWhiteSpace(_pullRequestDescription);
    public bool HasRunDescription => !string.IsNullOrWhiteSpace(RunDescriptionRaw);

    public bool HasUpstreamRunUrl => !string.IsNullOrWhiteSpace(UpstreamRunUrl);

    public bool HasDetectJobUrl => !string.IsNullOrWhiteSpace(DetectJobUrl);

    public bool HasSetBranchJobUrl => !string.IsNullOrWhiteSpace(SetBranchJobUrl);

    public bool HasActualCommitUrl => !string.IsNullOrWhiteSpace(ActualCommitUrl);

    public bool HasTriggerDetails =>
        IsWorkflowRun ||
        HasPullRequestUrl ||
        HasUpstreamRunUrl ||
        HasDetectJobUrl ||
        HasSetBranchJobUrl ||
        !string.IsNullOrWhiteSpace(ResolutionError);

    private void RaiseStatusBrushProperties()
    {
        RaisePropertyChanged(nameof(StatusBackgroundBrush));
        RaisePropertyChanged(nameof(StatusBorderBrush));
        RaisePropertyChanged(nameof(StatusForegroundBrush));
        RaisePropertyChanged(nameof(StatusBadgeText));
        RaisePropertyChanged(nameof(DurationSummary));
        RaisePropertyChanged(nameof(TimelineSummary));
    }

    private void RaiseTagProperties()
    {
        RaisePropertyChanged(nameof(EventTagText));
        RaisePropertyChanged(nameof(EventTagBackgroundBrush));
        RaisePropertyChanged(nameof(EventTagForegroundBrush));
        RaisePropertyChanged(nameof(WorkflowTagText));
        RaisePropertyChanged(nameof(WorkflowTagBackgroundBrush));
        RaisePropertyChanged(nameof(WorkflowTagForegroundBrush));
    }

    private (Brush Background, Brush Border, Brush Foreground) GetStatusBrushSet()
    {
        if (!string.IsNullOrWhiteSpace(ResolutionError))
        {
            return (FailureBackgroundBrush, FailureBorderBrush, FailureForegroundBrush);
        }

        var normalizedConclusion = Conclusion.Trim().ToLowerInvariant();
        var normalizedStatus = Status.Trim().ToLowerInvariant();

        if (normalizedConclusion is "success")
        {
            return (SuccessBackgroundBrush, SuccessBorderBrush, SuccessForegroundBrush);
        }

        if (normalizedConclusion is "failure" or "timed_out" or "startup_failure" or "action_required")
        {
            return (FailureBackgroundBrush, FailureBorderBrush, FailureForegroundBrush);
        }

        if (normalizedConclusion is "cancelled" or "stale")
        {
            return (WarningBackgroundBrush, WarningBorderBrush, WarningForegroundBrush);
        }

        if (IsResolving)
        {
            return (WarningBackgroundBrush, WarningBorderBrush, WarningForegroundBrush);
        }

        if (normalizedStatus is "queued" or "in_progress" or "requested" or "waiting" or "pending")
        {
            return (WarningBackgroundBrush, WarningBorderBrush, WarningForegroundBrush);
        }

        return (NeutralBackgroundBrush, NeutralBorderBrush, NeutralForegroundBrush);
    }

    private string GetStatusCategory()
    {
        if (!string.IsNullOrWhiteSpace(ResolutionError))
        {
            return "Failed";
        }

        var normalizedConclusion = Conclusion.Trim().ToLowerInvariant();
        var normalizedStatus = Status.Trim().ToLowerInvariant();

        if (normalizedConclusion is "success")
        {
            return "Success";
        }

        if (normalizedConclusion is "failure" or "timed_out" or "startup_failure" or "action_required")
        {
            return "Failed";
        }

        if (normalizedConclusion is "cancelled" or "stale")
        {
            return "Cancelled";
        }

        if (IsResolving || normalizedStatus is "queued" or "in_progress" or "requested" or "waiting" or "pending")
        {
            return "In Progress";
        }

        return "Other";
    }

    private string GetEventTagText()
    {
        if (HasPullRequestUrl ||
            EventName.Equals("pull_request", StringComparison.OrdinalIgnoreCase) ||
            EventName.Equals("pull_request_target", StringComparison.OrdinalIgnoreCase))
        {
            return "Pull Request";
        }

        if (EventName.Equals("workflow_run", StringComparison.OrdinalIgnoreCase))
        {
            return "Workflow Run";
        }

        if (EventName.Equals("workflow_dispatch", StringComparison.OrdinalIgnoreCase))
        {
            return "Manual Dispatch";
        }

        if (EventName.Equals("repository_dispatch", StringComparison.OrdinalIgnoreCase))
        {
            return "Repo Dispatch";
        }

        if (EventName.Equals("schedule", StringComparison.OrdinalIgnoreCase))
        {
            return "Scheduled";
        }

        if (EventName.Equals("push", StringComparison.OrdinalIgnoreCase))
        {
            return "Push";
        }

        return ToDisplayLabel(EventName);
    }

    private (Brush Background, Brush Foreground) GetEventTagBrushSet()
    {
        if (HasPullRequestUrl ||
            EventName.Equals("pull_request", StringComparison.OrdinalIgnoreCase) ||
            EventName.Equals("pull_request_target", StringComparison.OrdinalIgnoreCase))
        {
            return (PullRequestTagBackgroundBrush, WhiteTagForegroundBrush);
        }

        if (EventName.Equals("workflow_run", StringComparison.OrdinalIgnoreCase))
        {
            return (WorkflowRunTagBackgroundBrush, WhiteTagForegroundBrush);
        }

        if (EventName.Equals("push", StringComparison.OrdinalIgnoreCase))
        {
            return (PushTagBackgroundBrush, WhiteTagForegroundBrush);
        }

        if (EventName.Equals("schedule", StringComparison.OrdinalIgnoreCase))
        {
            return (ScheduledTagBackgroundBrush, WhiteTagForegroundBrush);
        }

        if (EventName.Equals("workflow_dispatch", StringComparison.OrdinalIgnoreCase) ||
            EventName.Equals("repository_dispatch", StringComparison.OrdinalIgnoreCase))
        {
            return (DispatchTagBackgroundBrush, WhiteTagForegroundBrush);
        }

        return (OtherEventTagBackgroundBrush, WhiteTagForegroundBrush);
    }

    private string GetWorkflowTagText()
    {
        var normalized = WorkflowName.Trim().ToLowerInvariant();

        if (ContainsAny(normalized, "build", "pipeline", "compile", "ci"))
        {
            return "Pipeline Build";
        }

        if (ContainsAny(normalized, "test", "validate", "verification", "qa", "check"))
        {
            return "Validation";
        }

        if (ContainsAny(normalized, "deploy", "release", "publish", "rollout", "ship"))
        {
            return "Release/Deploy";
        }

        if (ContainsAny(normalized, "security", "scan", "codeql", "dependency", "sast"))
        {
            return "Security";
        }

        if (ContainsAny(normalized, "cleanup", "sync", "maintenance", "infra", "ops", "backup"))
        {
            return "Ops";
        }

        return "General";
    }

    private (Brush Background, Brush Foreground) GetWorkflowTagBrushSet()
    {
        var label = GetWorkflowTagText();

        if (label == "Pipeline Build")
        {
            return (PipelineBuildTagBackgroundBrush, WhiteTagForegroundBrush);
        }

        if (label == "Validation")
        {
            return (ValidationTagBackgroundBrush, WhiteTagForegroundBrush);
        }

        if (label == "Release/Deploy")
        {
            return (DeployTagBackgroundBrush, WhiteTagForegroundBrush);
        }

        if (label == "Security")
        {
            return (SecurityTagBackgroundBrush, WhiteTagForegroundBrush);
        }

        if (label == "Ops")
        {
            return (OpsTagBackgroundBrush, WhiteTagForegroundBrush);
        }

        return (GeneralTagBackgroundBrush, WhiteTagForegroundBrush);
    }

    private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
    {
        return new SolidColorBrush(ColorHelper.FromArgb(alpha, red, green, blue));
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        foreach (var needle in needles)
        {
            if (value.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ShortSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return "?";
        }

        return sha.Length <= 10 ? sha : sha[..10];
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

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return $"{value[..(maxLength - 1)]}…";
    }

    private static string ToDisplayLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var normalized = value
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Trim()
            .ToLowerInvariant();

        return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(normalized);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.Minutes}m {duration.Seconds}s";
        }

        return $"{Math.Max(0, duration.Seconds)}s";
    }

    public void RefreshLiveTiming()
    {
        if (!IsActivelyRunning)
        {
            return;
        }

        RaisePropertyChanged(nameof(DurationSummary));
        RaisePropertyChanged(nameof(TimelineSummary));
    }
}
