using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class WorktreeGitTests
{
    private const string Head = "1111111111111111111111111111111111111111";
    private const string BaseTip = "2222222222222222222222222222222222222222";

    [Fact]
    public void ParseWorktreeList_ReadsBranchDetachedLockedAndPrunableEntries()
    {
        string[] lines =
        [
            "worktree C:/dev/acme-app",
            "HEAD 32c6ee0dd8",
            "branch refs/heads/master",
            "",
            "worktree C:/dev/acme-app/.worktrees/lms2",
            "HEAD 6685864c6b",
            "branch refs/heads/us/lms2",
            "prunable gitdir file points to non-existent location",
            "",
            "worktree C:/dev/acme-app_blame_bad",
            "HEAD 5f564347da",
            "detached",
            "locked reason text",
            ""
        ];

        var entries = WorktreeGit.ParseWorktreeList(lines);

        Assert.Collection(
            entries,
            entry =>
            {
                Assert.Equal(@"C:\dev\acme-app", entry.Path);
                Assert.Equal("master", entry.Branch);
                Assert.False(entry.IsDetached);
            },
            entry =>
            {
                Assert.Equal(@"C:\dev\acme-app\.worktrees\lms2", entry.Path);
                Assert.Equal("us/lms2", entry.Branch);
                Assert.True(entry.IsPrunable);
                Assert.False(entry.IsLocked);
            },
            entry =>
            {
                Assert.Equal(string.Empty, entry.Branch);
                Assert.Equal("5f564347da", entry.HeadSha);
                Assert.True(entry.IsDetached);
                Assert.True(entry.IsLocked);
            });
    }

    [Fact]
    public void Classify_IsMergedWhenTheCommitIsAlreadyOnTheBaseBranch()
    {
        var verdict = Classify(containedIn: "origin/master", ahead: 0);

        Assert.Equal(WorktreeState.Merged, verdict.State);
    }

    [Fact]
    public void Classify_IsMergedWhenAMergedPullRequestEndsAtThisCommit()
    {
        var verdict = Classify(ahead: 3, unique: 3, pullRequests: [PullRequest(12, isMerged: true, headSha: Head)]);

        Assert.Equal(WorktreeState.Merged, verdict.State);
        Assert.Equal(12, verdict.PullRequest?.Number);
    }

    [Fact]
    public void Classify_IsMergedWhenEveryCommitHasAnIdenticalPatchOnTheBase()
    {
        var verdict = Classify(ahead: 2, unique: 0);

        Assert.Equal(WorktreeState.Merged, verdict.State);
    }

    [Fact]
    public void Classify_DoesNotCallItMergedWhenTheMergedPullRequestEndedOnAnotherCommit()
    {
        var verdict = Classify(ahead: 4, unique: 1, pullRequests: [PullRequest(12, isMerged: true, headSha: "3333")]);

        Assert.Equal(WorktreeState.MergedPullRequestLocalDiffers, verdict.State);
    }

    [Fact]
    public void Classify_DoesNotCallItMergedWhenUniqueCommitsCouldNotBeWorkedOut()
    {
        var verdict = Classify(ahead: 900, unique: null, remoteBranchExists: true);

        Assert.Equal(WorktreeState.Unmerged, verdict.State);
        Assert.Contains("no pull request", verdict.Reason);
    }

    [Fact]
    public void Classify_PrefersTheOpenPullRequestOverAnOlderClosedOne()
    {
        var verdict = Classify(
            ahead: 2,
            unique: 2,
            pullRequests: [PullRequest(10, isOpen: false), PullRequest(11, isOpen: true)]);

        Assert.Equal(WorktreeState.OpenPullRequest, verdict.State);
        Assert.Equal(11, verdict.PullRequest?.Number);
    }

    [Fact]
    public void Classify_ReportsNeverPushedBranches()
    {
        var verdict = Classify(ahead: 1, unique: 1, remoteBranchExists: false);

        Assert.Equal(WorktreeState.Unmerged, verdict.State);
        Assert.Contains("Never pushed", verdict.Reason);
    }

    [Fact]
    public void Classify_TreatsACheckoutOnTheBaseTipAsHavingNothingToMerge()
    {
        var verdict = WorktreeGit.Classify(false, false, BaseTip, "origin/master", BaseTip, "origin/master", 0, null, true, []);

        Assert.Equal(WorktreeState.UpToDate, verdict.State);
    }

    [Fact]
    public void Classify_ReportsDetachedCheckoutsThatAreNotOnTheBase()
    {
        var verdict = WorktreeGit.Classify(false, true, Head, "origin/master", BaseTip, null, 5, 5, false, []);

        Assert.Equal(WorktreeState.Detached, verdict.State);
    }

    [Fact]
    public void Classify_ReportsMissingFoldersBeforeAnythingElse()
    {
        var verdict = WorktreeGit.Classify(true, false, Head, "origin/master", BaseTip, "origin/master", 0, 0, true, []);

        Assert.Equal(WorktreeState.Missing, verdict.State);
    }

    [Theory]
    [InlineData("https://github.com/example-org/acme-app", "example-org", "acme-app")]
    [InlineData("https://github.com/example-org/web-app.git", "example-org", "web-app")]
    [InlineData("git@github.com:example-org/helm-charts.git", "example-org", "helm-charts")]
    public void ParseGitHubRemote_ReadsOwnerAndName(string url, string owner, string name)
    {
        var parsed = WorktreeGit.ParseGitHubRemote(url);

        Assert.Equal((owner, name), parsed);
    }

    [Fact]
    public void ParseGitHubRemote_ReturnsNullForOtherHosts()
    {
        Assert.Null(WorktreeGit.ParseGitHubRemote("https://dev.azure.com/org/project/_git/repo"));
    }

    private static WorktreeVerdict Classify(
        string? containedIn = null,
        int? ahead = null,
        int? unique = null,
        bool remoteBranchExists = false,
        IReadOnlyList<BranchPullRequest>? pullRequests = null)
    {
        return WorktreeGit.Classify(false, false, Head, "origin/master", BaseTip, containedIn, ahead, unique, remoteBranchExists, pullRequests ?? []);
    }

    private static BranchPullRequest PullRequest(int number, bool isOpen = false, bool isMerged = false, string headSha = "")
    {
        return new BranchPullRequest(number, isOpen, isMerged, false, headSha, "master", $"https://github.com/o/r/pull/{number}");
    }
}
