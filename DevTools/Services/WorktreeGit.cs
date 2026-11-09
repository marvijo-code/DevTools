namespace DevTools.Services;

/// <summary>
/// What a worktree's checkout amounts to, in the order a cleanup would care about.
/// </summary>
public enum WorktreeState
{
    /// <summary>Git still lists the worktree but its folder is gone.</summary>
    Missing,

    /// <summary>The checkout's work is already on the base branch or in a merged pull request.</summary>
    Merged,

    /// <summary>Sits exactly on the base branch tip, so there is nothing to merge yet.</summary>
    UpToDate,

    OpenPullRequest,

    /// <summary>A pull request from this branch merged, but the checkout is not at the merged commit.</summary>
    MergedPullRequestLocalDiffers,

    ClosedPullRequest,

    /// <summary>A branch with its own commits and no pull request.</summary>
    Unmerged,

    /// <summary>A detached checkout whose commit is not on the base branch.</summary>
    Detached
}

/// <summary>One record from <c>git worktree list --porcelain</c>.</summary>
public sealed record WorktreeEntry(string Path, string HeadSha, string Branch, bool IsDetached, bool IsLocked, bool IsPrunable);

/// <summary>A pull request whose head is a given branch.</summary>
public sealed record BranchPullRequest(int Number, bool IsOpen, bool IsMerged, bool IsDraft, string HeadSha, string BaseBranch, string HtmlUrl);

public sealed record WorktreeVerdict(WorktreeState State, string Reason, BranchPullRequest? PullRequest);

/// <summary>
/// The parts of worktree analysis that need no process or UI, so they can be tested on their own.
/// </summary>
public static class WorktreeGit
{
    public static IReadOnlyList<WorktreeEntry> ParseWorktreeList(IEnumerable<string> porcelainLines)
    {
        var entries = new List<WorktreeEntry>();
        string? path = null;
        var head = string.Empty;
        var branch = string.Empty;
        var isDetached = false;
        var isLocked = false;
        var isPrunable = false;

        void Flush()
        {
            if (path is not null)
            {
                entries.Add(new WorktreeEntry(path, head, branch, isDetached, isLocked, isPrunable));
            }
        }

        foreach (var rawLine in porcelainLines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = NormalizePath(line["worktree ".Length..]);
                head = string.Empty;
                branch = string.Empty;
                isDetached = false;
                isLocked = false;
                isPrunable = false;
            }
            else if (path is null)
            {
                continue;
            }
            else if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line["HEAD ".Length..];
            }
            else if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                const string headsPrefix = "refs/heads/";
                var reference = line["branch ".Length..];
                branch = reference.StartsWith(headsPrefix, StringComparison.Ordinal) ? reference[headsPrefix.Length..] : reference;
            }
            else if (line == "detached")
            {
                isDetached = true;
            }
            else if (line.StartsWith("locked", StringComparison.Ordinal))
            {
                isLocked = true;
            }
            else if (line.StartsWith("prunable", StringComparison.Ordinal))
            {
                isPrunable = true;
            }
        }

        Flush();
        return entries;
    }

    /// <summary>
    /// Decides what a worktree is, strongest proof first. Nothing is called merged on a hunch: it takes history,
    /// identical patches, or a merged pull request whose head is this exact commit.
    /// </summary>
    /// <param name="containedInReference">The remote branch that already contains the checkout's commit, if any.</param>
    /// <param name="uniqueCommitCount">
    /// Commits with no identical patch on the base branch; null when it could not be worked out.
    /// </param>
    public static WorktreeVerdict Classify(
        bool isMissing,
        bool isDetached,
        string headSha,
        string baseReference,
        string baseTipSha,
        string? containedInReference,
        int? aheadCount,
        int? uniqueCommitCount,
        bool remoteBranchExists,
        IReadOnlyList<BranchPullRequest> pullRequests)
    {
        if (isMissing)
        {
            return new WorktreeVerdict(WorktreeState.Missing, "The folder is gone. Only git's record of it is left.", null);
        }

        var mergedAtHead = pullRequests.FirstOrDefault(pullRequest => pullRequest.IsMerged && SameSha(pullRequest.HeadSha, headSha));
        var open = pullRequests.Where(pullRequest => pullRequest.IsOpen).OrderByDescending(pullRequest => pullRequest.Number).FirstOrDefault();
        var merged = pullRequests.Where(pullRequest => pullRequest.IsMerged).OrderByDescending(pullRequest => pullRequest.Number).FirstOrDefault();
        var closed = pullRequests.Where(pullRequest => !pullRequest.IsOpen && !pullRequest.IsMerged).OrderByDescending(pullRequest => pullRequest.Number).FirstOrDefault();

        if (!string.IsNullOrEmpty(baseTipSha) && SameSha(headSha, baseTipSha))
        {
            return new WorktreeVerdict(WorktreeState.UpToDate, $"Sits on the tip of {baseReference}. Nothing to merge yet.", open);
        }

        if (!string.IsNullOrEmpty(containedInReference))
        {
            return new WorktreeVerdict(WorktreeState.Merged, $"Its commit is already part of {containedInReference}.", merged);
        }

        if (mergedAtHead is not null)
        {
            return new WorktreeVerdict(WorktreeState.Merged, $"PR #{mergedAtHead.Number} merged, and this checkout is that PR's last commit.", mergedAtHead);
        }

        if (aheadCount is > 0 && uniqueCommitCount == 0)
        {
            return new WorktreeVerdict(WorktreeState.Merged, $"Every commit already exists on {baseReference} as the same change.", merged);
        }

        if (isDetached)
        {
            return new WorktreeVerdict(WorktreeState.Detached, $"Detached checkout of a commit that is not on {baseReference}.", null);
        }

        if (open is not null)
        {
            return new WorktreeVerdict(
                WorktreeState.OpenPullRequest,
                $"PR #{open.Number} is open{(open.IsDraft ? " (draft)" : string.Empty)} into {open.BaseBranch}.",
                open);
        }

        if (merged is not null)
        {
            return new WorktreeVerdict(
                WorktreeState.MergedPullRequestLocalDiffers,
                $"PR #{merged.Number} merged, but this checkout is not at the merged commit. It may hold later work.",
                merged);
        }

        if (closed is not null)
        {
            return new WorktreeVerdict(WorktreeState.ClosedPullRequest, $"PR #{closed.Number} was closed without merging.", closed);
        }

        return new WorktreeVerdict(
            WorktreeState.Unmerged,
            remoteBranchExists ? "Pushed to origin, but no pull request." : "Exists only on this machine. Never pushed.",
            null);
    }

    /// <summary>Reads owner and repository name out of a GitHub remote URL (https or ssh form).</summary>
    public static (string Owner, string Name)? ParseGitHubRemote(string? remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
        {
            return null;
        }

        var url = remoteUrl.Trim();
        var hostIndex = url.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
        if (hostIndex < 0)
        {
            return null;
        }

        var tail = url[(hostIndex + "github.com".Length)..].TrimStart(':', '/');
        if (tail.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            tail = tail[..^4];
        }

        var parts = tail.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? (parts[0], parts[1]) : null;
    }

    public static string NormalizePath(string path)
    {
        return path.Trim().Replace('/', System.IO.Path.DirectorySeparatorChar).TrimEnd(System.IO.Path.DirectorySeparatorChar);
    }

    private static bool SameSha(string left, string right)
    {
        return !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
