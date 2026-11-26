using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class WorktreeBulkPolicyTests
{
    private static WorktreeBulkCandidate Candidate(
        string name,
        WorktreeState state,
        bool isDetached = false,
        bool isLocked = false,
        bool isMissing = false,
        bool isDirty = false,
        int dirtyCount = 0,
        bool isAppManaged = false,
        bool isAnalyzing = false,
        long sizeBytes = 1024 * 1024) =>
        new(
            $@"C:\dev\repo-{name}",
            @"C:\dev\repo",
            "repo",
            isDetached ? string.Empty : $"feat/{name}",
            state,
            isDetached,
            isLocked,
            isMissing,
            isDirty,
            dirtyCount,
            isAppManaged,
            isAnalyzing,
            sizeBytes);

    [Fact]
    public void Plan_DisallowsUnmergedAndDirtyWithoutExplicitApproval()
    {
        var safeMerged = Candidate("merged-clean", WorktreeState.Merged, sizeBytes: 5_000);
        var safeMissing = Candidate("missing", WorktreeState.Missing, isMissing: true, sizeBytes: 0);
        var unmerged = Candidate("unmerged", WorktreeState.Unmerged, sizeBytes: 8_000);
        var openPr = Candidate("open-pr", WorktreeState.OpenPullRequest, sizeBytes: 4_000);
        var dirtyMerged = Candidate("dirty-merged", WorktreeState.Merged, isDirty: true, dirtyCount: 3, sizeBytes: 6_000);
        var detached = Candidate("detached", WorktreeState.Detached, isDetached: true, sizeBytes: 3_000);
        var appManaged = Candidate("claude", WorktreeState.Merged, isAppManaged: true, sizeBytes: 9_000);
        var analyzing = Candidate("checking", WorktreeState.Unmerged, isAnalyzing: true, sizeBytes: 2_000);

        var plan = WorktreeBulkPolicy.Plan([safeMerged, safeMissing, unmerged, openPr, dirtyMerged, detached, appManaged, analyzing]);

        Assert.Equal(2, plan.SafeItems.Count);
        Assert.Equal(4, plan.ApprovalRequiredItems.Count);
        Assert.Equal(2, plan.DisallowedItems.Count);
        Assert.True(plan.RequiresExplicitApproval);

        // Without explicit approval, ONLY safe items are eligible.
        var withoutApproval = plan.GetApprovedTargets(explicitApprovalGranted: false);
        Assert.Equal(2, withoutApproval.Count);
        Assert.Contains(safeMerged, withoutApproval);
        Assert.Contains(safeMissing, withoutApproval);
        Assert.DoesNotContain(unmerged, withoutApproval);
        Assert.DoesNotContain(dirtyMerged, withoutApproval);

        // With explicit approval, safe + approval-required items are returned, NEVER app-managed or analyzing.
        var withApproval = plan.GetApprovedTargets(explicitApprovalGranted: true);
        Assert.Equal(6, withApproval.Count);
        Assert.Contains(unmerged, withApproval);
        Assert.Contains(dirtyMerged, withApproval);
        Assert.DoesNotContain(appManaged, withApproval);
        Assert.DoesNotContain(analyzing, withApproval);
    }

    [Fact]
    public void EvaluateBranchDeletion_DisallowsUnmergedAndProtectedBranchesWithoutApproval()
    {
        var merged = Candidate("done", WorktreeState.Merged);
        var unmerged = Candidate("wip", WorktreeState.Unmerged);
        var mainBranch = merged with { Branch = "main" };
        var detached = Candidate("det", WorktreeState.Merged, isDetached: true);

        // Merged branch uses safe -d flag (not force).
        var mergedEval = WorktreeBulkPolicy.EvaluateBranchDeletion(merged, deleteBranchesRequested: true, allowUnmergedBranchDelete: false);
        Assert.True(mergedEval.Allowed);
        Assert.False(mergedEval.UseForceFlag);

        // Unmerged branch is blocked without explicit approval.
        var unmergedBlocked = WorktreeBulkPolicy.EvaluateBranchDeletion(unmerged, deleteBranchesRequested: true, allowUnmergedBranchDelete: false);
        Assert.False(unmergedBlocked.Allowed);

        // Unmerged branch allowed only with explicit approval (-D).
        var unmergedApproved = WorktreeBulkPolicy.EvaluateBranchDeletion(unmerged, deleteBranchesRequested: true, allowUnmergedBranchDelete: true);
        Assert.True(unmergedApproved.Allowed);
        Assert.True(unmergedApproved.UseForceFlag);

        // Protected base branches are NEVER deleted, even with explicit approval.
        var mainEval = WorktreeBulkPolicy.EvaluateBranchDeletion(mainBranch, deleteBranchesRequested: true, allowUnmergedBranchDelete: true);
        Assert.False(mainEval.Allowed);

        // Detached checkouts have no branch to delete.
        var detachedEval = WorktreeBulkPolicy.EvaluateBranchDeletion(detached, deleteBranchesRequested: true, allowUnmergedBranchDelete: true);
        Assert.False(detachedEval.Allowed);
    }
}
