namespace DevTools.Services;

public enum WorktreeSafetyCategory
{
    /// <summary>Merged with no uncommitted files (or folder already missing), unlocked, and not app-managed.</summary>
    Safe,

    /// <summary>Unmerged branch, open PR, uncommitted files, detached commit, or locked: requires explicit approval.</summary>
    ApprovalRequired,

    /// <summary>App-managed (.claude\worktrees\) or still analyzing: never eligible for bulk removal.</summary>
    Disallowed
}

public sealed record WorktreeBulkCandidate(
    string Path,
    string RepositoryPath,
    string RepositoryName,
    string Branch,
    WorktreeState State,
    bool IsDetached,
    bool IsLocked,
    bool IsMissing,
    bool IsDirty,
    int DirtyCount,
    bool IsAppManaged,
    bool IsAnalyzing,
    long SizeBytes)
{
    public string DisplayLabel => IsDetached ? $"{RepositoryName} (detached)" : $"{RepositoryName} / {Branch}";
}

public sealed record BlockedWorktreeItem(WorktreeBulkCandidate Item, string Reason);

public sealed record WorktreeBulkPlan(
    IReadOnlyList<WorktreeBulkCandidate> SafeItems,
    IReadOnlyList<BlockedWorktreeItem> ApprovalRequiredItems,
    IReadOnlyList<BlockedWorktreeItem> DisallowedItems)
{
    public long SafeBytes => SafeItems.Sum(item => item.SizeBytes);

    public long ApprovalRequiredBytes => ApprovalRequiredItems.Sum(entry => entry.Item.SizeBytes);

    public long TotalRemovableBytes => SafeBytes + ApprovalRequiredBytes;

    public bool RequiresExplicitApproval => ApprovalRequiredItems.Count > 0;

    /// <summary>
    /// Returns the items permitted to be removed. Disallowed items are never returned.
    /// Items requiring approval (unmerged, dirty, open PR, detached, locked) are ONLY returned when
    /// <paramref name="explicitApprovalGranted"/> is true.
    /// </summary>
    public IReadOnlyList<WorktreeBulkCandidate> GetApprovedTargets(bool explicitApprovalGranted)
    {
        if (!explicitApprovalGranted)
        {
            return SafeItems;
        }

        var combined = new List<WorktreeBulkCandidate>(SafeItems.Count + ApprovalRequiredItems.Count);
        combined.AddRange(SafeItems);
        combined.AddRange(ApprovalRequiredItems.Select(entry => entry.Item));
        return combined;
    }

    public string BuildConfirmationSummary(bool includeApprovalRequired)
    {
        var lines = new List<string>();
        if (SafeItems.Count > 0)
        {
            lines.Add($"Safe to remove: {SafeItems.Count} worktree(s) ({CleanupRules.FormatBytes(SafeBytes)})");
            foreach (var item in SafeItems.Take(12))
            {
                lines.Add($"  [Safe] {item.DisplayLabel} ({CleanupRules.FormatBytes(item.SizeBytes)}) - {item.Path}");
            }

            if (SafeItems.Count > 12)
            {
                lines.Add($"  ...and {SafeItems.Count - 12} more safe worktree(s).");
            }
        }

        if (ApprovalRequiredItems.Count > 0)
        {
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add(includeApprovalRequired
                ? $"Requires explicit approval ({ApprovalRequiredItems.Count}, {CleanupRules.FormatBytes(ApprovalRequiredBytes)}):"
                : $"Blocked without explicit approval ({ApprovalRequiredItems.Count}, {CleanupRules.FormatBytes(ApprovalRequiredBytes)}):");
            foreach (var blocked in ApprovalRequiredItems.Take(12))
            {
                lines.Add($"  [Approval needed] {blocked.Item.DisplayLabel}: {blocked.Reason} - {blocked.Item.Path}");
            }

            if (ApprovalRequiredItems.Count > 12)
            {
                lines.Add($"  ...and {ApprovalRequiredItems.Count - 12} more worktree(s) needing approval.");
            }
        }

        if (DisallowedItems.Count > 0)
        {
            if (lines.Count > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"Skipped (protected, never bulk-removed): {DisallowedItems.Count} worktree(s).");
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// Pure safety rules for bulk worktree removal and branch deletion. Enforces that unmerged branches,
/// open PRs, dirty checkouts, and detached commits are never removed without an explicit approval prompt.
/// </summary>
public static class WorktreeBulkPolicy
{
    private static readonly HashSet<string> ProtectedBranchNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "main",
        "master",
        "develop",
        "development",
        "trunk",
        "HEAD"
    };

    public static (WorktreeSafetyCategory Category, string Reason) Classify(WorktreeBulkCandidate item)
    {
        if (item.IsAppManaged)
        {
            return (WorktreeSafetyCategory.Disallowed, "Managed by an active app session (.claude\\worktrees).");
        }

        if (item.IsAnalyzing)
        {
            return (WorktreeSafetyCategory.Disallowed, "Still analyzing git and PR status.");
        }

        if (item.IsMissing)
        {
            return (WorktreeSafetyCategory.Safe, "Folder is already gone; only git metadata will be pruned.");
        }

        var reasons = new List<string>();
        if (item.IsDirty)
        {
            reasons.Add($"{item.DirtyCount} uncommitted file(s)");
        }

        if (item.IsLocked)
        {
            reasons.Add("worktree is locked");
        }

        switch (item.State)
        {
            case WorktreeState.Merged:
                break;
            case WorktreeState.OpenPullRequest:
                reasons.Add("has an open pull request");
                break;
            case WorktreeState.MergedPullRequestLocalDiffers:
                reasons.Add("local checkout differs from merged PR");
                break;
            case WorktreeState.ClosedPullRequest:
                reasons.Add("branch PR was closed without merging");
                break;
            case WorktreeState.Detached:
                reasons.Add("detached HEAD not on base branch");
                break;
            case WorktreeState.UpToDate:
                reasons.Add("branch has no commits yet");
                break;
            default:
                reasons.Add("branch is not merged");
                break;
        }

        if (reasons.Count == 0)
        {
            return (WorktreeSafetyCategory.Safe, "Merged with no uncommitted files.");
        }

        return (WorktreeSafetyCategory.ApprovalRequired, string.Join(", ", reasons));
    }

    public static WorktreeBulkPlan Plan(IEnumerable<WorktreeBulkCandidate> candidates)
    {
        var safe = new List<WorktreeBulkCandidate>();
        var approvalRequired = new List<BlockedWorktreeItem>();
        var disallowed = new List<BlockedWorktreeItem>();

        foreach (var candidate in candidates)
        {
            var (category, reason) = Classify(candidate);
            switch (category)
            {
                case WorktreeSafetyCategory.Safe:
                    safe.Add(candidate);
                    break;
                case WorktreeSafetyCategory.ApprovalRequired:
                    approvalRequired.Add(new BlockedWorktreeItem(candidate, reason));
                    break;
                default:
                    disallowed.Add(new BlockedWorktreeItem(candidate, reason));
                    break;
            }
        }

        return new WorktreeBulkPlan(safe, approvalRequired, disallowed);
    }

    /// <summary>
    /// Decides whether a local git branch may be deleted after its worktree is removed.
    /// Protected base branches (main/master/develop) are NEVER deleted. Unmerged branches are
    /// disallowed unless <paramref name="allowUnmergedBranchDelete"/> was explicitly approved.
    /// </summary>
    public static (bool Allowed, bool UseForceFlag, string Reason) EvaluateBranchDeletion(
        WorktreeBulkCandidate item,
        bool deleteBranchesRequested,
        bool allowUnmergedBranchDelete)
    {
        if (!deleteBranchesRequested || item.IsDetached || string.IsNullOrWhiteSpace(item.Branch))
        {
            return (false, false, "Branch kept.");
        }

        if (ProtectedBranchNames.Contains(item.Branch.Trim()))
        {
            return (false, false, $"Protected branch '{item.Branch}' is never deleted.");
        }

        if (item.State == WorktreeState.Merged)
        {
            // Merged branches use safe `git branch -d` so git double-checks merge ancestry.
            return (true, false, "Merged branch eligible for safe git branch -d.");
        }

        if (!allowUnmergedBranchDelete)
        {
            return (false, false, $"Unmerged branch '{item.Branch}' cannot be deleted without explicit approval.");
        }

        return (true, true, $"Unmerged branch '{item.Branch}' explicitly approved for forced deletion.");
    }
}
