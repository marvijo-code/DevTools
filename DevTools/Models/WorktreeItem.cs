using DevTools.Infrastructure;
using DevTools.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace DevTools.Models;

/// <summary>
/// One git worktree on this machine, with what it costs in disk and whether its work is already merged.
/// </summary>
public sealed class WorktreeItem : ObservableObject
{
    private static readonly Brush GreenBackgroundBrush = CreateBrush(0x1c, 0x1f, 0x8b, 0x4c);
    private static readonly Brush GreenBorderBrush = CreateBrush(0x70, 0x1f, 0x8b, 0x4c);
    private static readonly Brush GreenAccentBrush = CreateBrush(0xff, 0x1f, 0x8b, 0x4c);
    private static readonly Brush RedBackgroundBrush = CreateBrush(0x1c, 0xd1, 0x43, 0x43);
    private static readonly Brush RedBorderBrush = CreateBrush(0x70, 0xd1, 0x43, 0x43);
    private static readonly Brush RedAccentBrush = CreateBrush(0xff, 0xd1, 0x43, 0x43);
    private static readonly Brush AmberBackgroundBrush = CreateBrush(0x1c, 0xb7, 0x79, 0x1f);
    private static readonly Brush AmberBorderBrush = CreateBrush(0x70, 0xb7, 0x79, 0x1f);
    private static readonly Brush AmberAccentBrush = CreateBrush(0xff, 0xb7, 0x79, 0x1f);
    private static readonly Brush BlueBackgroundBrush = CreateBrush(0x1c, 0x2f, 0x6f, 0xd0);
    private static readonly Brush BlueBorderBrush = CreateBrush(0x70, 0x2f, 0x6f, 0xd0);
    private static readonly Brush BlueAccentBrush = CreateBrush(0xff, 0x2f, 0x6f, 0xd0);
    private static readonly Brush GreyBackgroundBrush = CreateBrush(0x14, 0x4b, 0x55, 0x67);
    private static readonly Brush GreyBorderBrush = CreateBrush(0x55, 0x4b, 0x55, 0x67);
    private static readonly Brush GreyAccentBrush = CreateBrush(0xff, 0x4b, 0x55, 0x67);
    private static readonly Brush WhiteForegroundBrush = CreateBrush(0xff, 0xff, 0xff, 0xff);

    private WorktreeState _state;
    private string _reason = "Checking...";
    private long? _sizeBytes;
    private int? _dirtyCount;
    private int? _aheadCount;
    private DateTimeOffset? _lastCommitAt;
    private BranchPullRequest? _pullRequest;
    private bool _isAnalyzing = true;
    private bool _isRemoving;
    private bool _isHeadOnAnyBranch = true;

    public required string RepositoryName { get; init; }

    /// <summary>The main checkout that owns this worktree; git commands for it run from here.</summary>
    public required string RepositoryPath { get; init; }

    public required string Path { get; init; }

    public required string Branch { get; init; }

    public required string HeadSha { get; init; }

    public required bool IsDetached { get; init; }

    public required bool IsLocked { get; init; }

    public required bool IsMissing { get; init; }

    /// <summary>
    /// Worktrees under a <c>.claude\worktrees</c> folder belong to the Claude app, which creates and reaps them
    /// itself, so removing one here would pull it out from under a session.
    /// </summary>
    public bool IsAppManaged => Path.Contains(@"\.claude\worktrees\", StringComparison.OrdinalIgnoreCase);

    public WorktreeState State => _state;

    public string Reason => _reason;

    public long? SizeBytes => _sizeBytes;

    public int? DirtyCount => _dirtyCount;

    public bool IsAnalyzing => _isAnalyzing;

    public bool IsRemoving => _isRemoving;

    public BranchPullRequest? PullRequest => _pullRequest;

    public bool HasPullRequest => _pullRequest is not null;

    public string PullRequestUrl => _pullRequest?.HtmlUrl ?? string.Empty;

    public string PullRequestButtonText => _pullRequest is null ? "No PR" : $"PR #{_pullRequest.Number}";

    public bool IsDirty => _dirtyCount is > 0;

    /// <summary>
    /// Removing the folder loses nothing: the work is merged (or the folder is already gone), there are no
    /// uncommitted files, and nobody locked it.
    /// </summary>
    public bool IsSafeToRemove =>
        !_isAnalyzing &&
        !IsAppManaged &&
        (IsMissing || (_state == WorktreeState.Merged && _dirtyCount == 0 && !IsLocked));

    public bool CanRemove => !_isAnalyzing && !_isRemoving && !IsAppManaged;

    public string RemoveButtonText =>
        _isRemoving ? "Removing..." :
        IsMissing ? "Prune Entry" :
        IsDirty ? "Force Remove" :
        "Remove";

    public string BranchDisplay => IsDetached ? $"(detached at {ShortSha})" : Branch;

    public string ShortSha => HeadSha.Length > 10 ? HeadSha[..10] : HeadSha;

    public string SizeDisplay =>
        IsMissing ? "0 B" :
        _sizeBytes is null ? (_isAnalyzing ? "Measuring..." : "Unknown") :
        FormatBytes(_sizeBytes.Value);

    public string StateBadgeText =>
        _isAnalyzing ? "Checking" :
        IsSafeToRemove && !IsMissing ? "Merged, safe to remove" :
        _state switch
        {
            WorktreeState.Missing => "Folder missing",
            WorktreeState.Merged => "Merged",
            WorktreeState.UpToDate => "No changes yet",
            WorktreeState.OpenPullRequest => "Open PR",
            WorktreeState.MergedPullRequestLocalDiffers => "PR merged, local differs",
            WorktreeState.ClosedPullRequest => "PR closed",
            WorktreeState.Detached => "Detached",
            _ => "Not merged"
        };

    public string DetailSummary
    {
        get
        {
            var parts = new List<string>();
            if (_lastCommitAt is not null)
            {
                parts.Add($"Last commit {_lastCommitAt.Value.LocalDateTime:yyyy-MM-dd}");
            }

            if (_aheadCount is > 0)
            {
                parts.Add($"{_aheadCount} commit(s) not on the base branch");
            }

            if (_dirtyCount is not null)
            {
                parts.Add(_dirtyCount == 0 ? "no uncommitted files" : $"{_dirtyCount} uncommitted file(s)");
            }

            if (IsLocked)
            {
                parts.Add("locked");
            }

            if (IsAppManaged)
            {
                parts.Add("managed by the Claude app");
            }

            if (IsDetached && !_isHeadOnAnyBranch)
            {
                parts.Add("commit is on no branch");
            }

            return string.Join(" · ", parts);
        }
    }

    public Brush StateBackgroundBrush => GetBrushSet().Background;

    public Brush StateBorderBrush => GetBrushSet().Border;

    public Brush StateAccentBrush => GetBrushSet().Accent;

    public Brush StateBadgeForegroundBrush => WhiteForegroundBrush;

    /// <summary>Largest first within a group, and groups in the order a cleanup would work through them.</summary>
    public int SortRank =>
        IsSafeToRemove ? 0 :
        _state switch
        {
            WorktreeState.Merged => 1,
            WorktreeState.ClosedPullRequest => 2,
            WorktreeState.MergedPullRequestLocalDiffers => 3,
            WorktreeState.Detached => 4,
            WorktreeState.Unmerged => 5,
            WorktreeState.UpToDate => 6,
            _ => 7
        };

    public void ApplyAnalysis(
        WorktreeVerdict verdict,
        int? dirtyCount,
        int? aheadCount,
        DateTimeOffset? lastCommitAt,
        bool isHeadOnAnyBranch)
    {
        _state = verdict.State;
        _reason = verdict.Reason;
        _pullRequest = verdict.PullRequest;
        _dirtyCount = dirtyCount;
        _aheadCount = aheadCount;
        _lastCommitAt = lastCommitAt;
        _isHeadOnAnyBranch = isHeadOnAnyBranch;
        _isAnalyzing = false;
        RaiseAll();
    }

    public void ApplySize(long? sizeBytes)
    {
        _sizeBytes = sizeBytes;
        RaisePropertyChanged(nameof(SizeBytes));
        RaisePropertyChanged(nameof(SizeDisplay));
    }

    public void SetRemoving(bool isRemoving)
    {
        _isRemoving = isRemoving;
        RaisePropertyChanged(nameof(IsRemoving));
        RaisePropertyChanged(nameof(CanRemove));
        RaisePropertyChanged(nameof(RemoveButtonText));
    }

    /// <summary>
    /// Spells out what removing this worktree costs, so the confirmation never hides a loss behind one button.
    /// </summary>
    public string BuildRemovalWarning()
    {
        var lines = new List<string> { Path, string.Empty, $"Branch: {BranchDisplay}", $"Status: {StateBadgeText}. {Reason}" };

        if (IsMissing)
        {
            lines.Add(string.Empty);
            lines.Add("The folder is already gone. This only clears git's leftover record of it.");
            return string.Join(Environment.NewLine, lines);
        }

        lines.Add($"Size: {SizeDisplay}");
        lines.Add(string.Empty);
        lines.Add(IsDetached
            ? "The folder is deleted. This checkout is not on a branch."
            : "The folder is deleted. The branch and its commits are kept.");

        if (IsDirty)
        {
            lines.Add($"WARNING: {_dirtyCount} uncommitted file(s) in this folder will be lost for good.");
        }

        if (IsDetached && !_isHeadOnAnyBranch)
        {
            lines.Add("WARNING: its commit is on no branch, so git may eventually discard it.");
        }

        if (!IsSafeToRemove && !IsDirty && _state != WorktreeState.Merged)
        {
            lines.Add("This work is not proven merged. The commits stay on the branch, but check before removing.");
        }

        if (IsLocked)
        {
            lines.Add("This worktree is locked. It will be unlocked first.");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private (Brush Background, Brush Border, Brush Accent) GetBrushSet()
    {
        if (_isAnalyzing)
        {
            return (GreyBackgroundBrush, GreyBorderBrush, GreyAccentBrush);
        }

        if (IsSafeToRemove)
        {
            return (GreenBackgroundBrush, GreenBorderBrush, GreenAccentBrush);
        }

        return _state switch
        {
            WorktreeState.Merged => (AmberBackgroundBrush, AmberBorderBrush, AmberAccentBrush),
            WorktreeState.OpenPullRequest => (BlueBackgroundBrush, BlueBorderBrush, BlueAccentBrush),
            WorktreeState.Unmerged or WorktreeState.Detached or WorktreeState.MergedPullRequestLocalDiffers => (RedBackgroundBrush, RedBorderBrush, RedAccentBrush),
            _ => (GreyBackgroundBrush, GreyBorderBrush, GreyAccentBrush)
        };
    }

    private void RaiseAll()
    {
        RaisePropertyChanged(nameof(State));
        RaisePropertyChanged(nameof(Reason));
        RaisePropertyChanged(nameof(DirtyCount));
        RaisePropertyChanged(nameof(IsDirty));
        RaisePropertyChanged(nameof(IsAnalyzing));
        RaisePropertyChanged(nameof(PullRequest));
        RaisePropertyChanged(nameof(HasPullRequest));
        RaisePropertyChanged(nameof(PullRequestUrl));
        RaisePropertyChanged(nameof(PullRequestButtonText));
        RaisePropertyChanged(nameof(IsSafeToRemove));
        RaisePropertyChanged(nameof(CanRemove));
        RaisePropertyChanged(nameof(RemoveButtonText));
        RaisePropertyChanged(nameof(SizeDisplay));
        RaisePropertyChanged(nameof(StateBadgeText));
        RaisePropertyChanged(nameof(DetailSummary));
        RaisePropertyChanged(nameof(StateBackgroundBrush));
        RaisePropertyChanged(nameof(StateBorderBrush));
        RaisePropertyChanged(nameof(StateAccentBrush));
        RaisePropertyChanged(nameof(SortRank));
    }

    public static string FormatBytes(long bytes)
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

        return suffixIndex == 0 ? $"{displayValue:0} {suffixes[suffixIndex]}" : $"{displayValue:0.00} {suffixes[suffixIndex]}";
    }

    private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
    {
        return new SolidColorBrush(ColorHelper.FromArgb(alpha, red, green, blue));
    }
}
