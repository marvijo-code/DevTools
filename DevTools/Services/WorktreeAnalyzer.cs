using System.Diagnostics;
using System.Globalization;
using DevTools.Infrastructure;
using DevTools.Models;

namespace DevTools.Services;

/// <summary>
/// Finds every git worktree under the repository root, works out whether each one's work is already merged,
/// and removes or prunes them one at a time on request.
/// </summary>
public sealed class WorktreeAnalyzer
{
    private const int WorkerLimit = 3;
    private const int PatchComparisonCommitLimit = 300;
    private readonly GitHubApiClient _gitHubApiClient;
    private readonly DiskUsageAnalyzer _diskUsageAnalyzer;

    public WorktreeAnalyzer(GitHubApiClient gitHubApiClient, DiskUsageAnalyzer diskUsageAnalyzer)
    {
        _gitHubApiClient = gitHubApiClient;
        _diskUsageAnalyzer = diskUsageAnalyzer;
    }

    public async Task<WorktreeSnapshot> CaptureAsync(IProgress<WorktreeProgress>? progress, CancellationToken cancellationToken)
    {
        var capturedAt = DateTimeOffset.Now;
        var (rootPath, repositoryPaths) = await Task.Run(() =>
        {
            var root = ResolveRepositoryRootPath();
            return (root, EnumerateRepositoryPaths(root));
        }, cancellationToken);
        var repositories = new List<RepositoryWorktrees>();
        var items = new List<WorktreeItem>();
        AppLog.Info($"Worktree scan: root '{rootPath}', {repositoryPaths.Count} repositories with a .git folder.");

        foreach (var repositoryPath in repositoryPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var listing = await RunGitAsync(repositoryPath, ["worktree", "list", "--porcelain"], cancellationToken);
            if (listing.ExitCode != 0)
            {
                continue;
            }

            var entries = WorktreeGit.ParseWorktreeList(listing.Lines);
            if (entries.Count < 2)
            {
                continue;
            }

            var repositoryItems = entries
                .Skip(1)
                .Select(entry => new WorktreeItem
                {
                    RepositoryName = Path.GetFileName(repositoryPath),
                    RepositoryPath = repositoryPath,
                    Path = entry.Path,
                    Branch = entry.Branch,
                    HeadSha = entry.HeadSha,
                    IsDetached = entry.IsDetached,
                    IsLocked = entry.IsLocked,
                    IsMissing = entry.IsPrunable || !Directory.Exists(entry.Path)
                })
                .ToList();

            repositories.Add(new RepositoryWorktrees(repositoryPath, repositoryItems));
            items.AddRange(repositoryItems);
        }

        progress?.Report(new WorktreeProgress($"Found {items.Count} worktrees in {repositories.Count} repositories. Checking each one...", items.ToList(), false));

        var pullRequestLookup = new PullRequestLookup(_gitHubApiClient);
        var completed = 0;
        using var gate = new SemaphoreSlim(WorkerLimit);

        foreach (var repository in repositories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new WorktreeProgress($"Fetching origin for {Path.GetFileName(repository.Path)}...", items.ToList(), false));
            var context = await BuildRepositoryContextAsync(repository.Path, cancellationToken);

            var tasks = repository.Items.Select(async item =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    await AnalyzeAsync(item, context, pullRequestLookup, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Worktree analysis failed for {item.Path}.", ex);
                    item.ApplyAnalysis(new WorktreeVerdict(WorktreeState.Unmerged, $"Could not be checked: {ex.Message}", null), null, null, null, true);
                }
                finally
                {
                    gate.Release();
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new WorktreeProgress($"Checked {done} of {items.Count} worktrees. Current: {item.Path}", items.ToList(), false));
                }
            });
            await Task.WhenAll(tasks);
        }

        var note = pullRequestLookup.FailureMessage is null
            ? string.Empty
            : $" Pull request status was unavailable ({pullRequestLookup.FailureMessage}), so only git history was used.";
        progress?.Report(new WorktreeProgress($"Last worktree refresh: {capturedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}.{note}", items.ToList(), true));
        return new WorktreeSnapshot(capturedAt, items, rootPath, repositories.Count, note);
    }

    /// <summary>
    /// Removes one worktree folder using the fast parallel native deleter (safe junction unlinking +
    /// POSIX semantics) followed by <c>git worktree prune</c>, falling back to <c>git worktree remove</c>
    /// if locked files remain. The branch stays unless explicitly requested and permitted by
    /// <see cref="WorktreeBulkPolicy"/>.
    /// </summary>
    public async Task<(bool Success, string Message)> RemoveAsync(
        WorktreeItem item,
        bool force,
        CancellationToken cancellationToken,
        bool deleteBranch = false,
        bool allowUnmergedBranchDelete = false,
        bool pruneImmediately = true)
    {
        if (item.IsAppManaged)
        {
            return (false, $"Refused to remove app-managed worktree {item.Path}.");
        }

        if (item.IsDirty && !force)
        {
            return (false, $"Refused to remove dirty worktree {item.Path} without explicit force approval.");
        }

        var clock = Stopwatch.StartNew();
        if (item.IsLocked)
        {
            await RunGitAsync(item.RepositoryPath, ["worktree", "unlock", item.Path], cancellationToken);
        }

        if (item.IsMissing || !Directory.Exists(item.Path))
        {
            if (pruneImmediately)
            {
                var prune = await RunGitAsync(item.RepositoryPath, ["worktree", "prune"], cancellationToken);
                if (prune.ExitCode != 0)
                {
                    return (false, $"Prune failed for {item.Path}: {prune.Error}");
                }
            }

            var branchNote = await TryDeleteWorktreeBranchAsync(item, deleteBranch, allowUnmergedBranchDelete, cancellationToken);
            return (true, $"Pruned the leftover record for {item.Path}.{branchNote}");
        }

        // Fast path: delete the folder using our multi-threaded Rust/native deleter (safely unlinks
        // junctions without recursing into targets), then prune git's worktree metadata.
        var deleteOutcome = await FastPathDeleter.DeleteAsync(item.Path, keepRoot: false, workerCount: 0, cancellationToken);
        if (deleteOutcome.RootRemoved || !Directory.Exists(item.Path))
        {
            if (pruneImmediately)
            {
                var prune = await RunGitAsync(item.RepositoryPath, ["worktree", "prune"], cancellationToken);
                if (prune.ExitCode != 0)
                {
                    AppLog.Info($"git worktree prune after fast delete returned {prune.ExitCode}: {prune.Error}");
                }
            }

            clock.Stop();
            var branchNote = await TryDeleteWorktreeBranchAsync(item, deleteBranch, allowUnmergedBranchDelete, cancellationToken);
            var engine = deleteOutcome.UsedNative ? "Rust native" : "parallel";
            AppLog.Info($"Fast worktree remove ({engine}) {item.Path}: {deleteOutcome.FilesDeleted:N0} files, {CleanupRules.FormatBytes(deleteOutcome.BytesFreed)} in {clock.ElapsedMilliseconds} ms.");
            return (true, $"Removed {item.Path} ({CleanupRules.FormatBytes(deleteOutcome.BytesFreed)}) in {clock.Elapsed.TotalSeconds:F2}s ({engine}).{branchNote}");
        }

        // Fallback to git worktree remove if any files remained locked.
        var arguments = new List<string> { "worktree", "remove" };
        if (force)
        {
            arguments.Add("--force");
        }

        arguments.Add(item.Path);
        var result = await RunGitAsync(item.RepositoryPath, arguments, cancellationToken, timeoutSeconds: 600);
        AppLog.Info($"git worktree remove fallback {(force ? "--force " : string.Empty)}{item.Path} => {result.ExitCode}. {result.Error}");
        if (result.ExitCode == 0)
        {
            var branchNote = await TryDeleteWorktreeBranchAsync(item, deleteBranch, allowUnmergedBranchDelete, cancellationToken);
            return (true, $"Removed worktree {item.Path}.{branchNote}");
        }

        return (false, $"Could not remove {item.Path}: {FirstLine(result.Error)}");
    }

    /// <summary>
    /// Removes multiple worktrees in parallel while enforcing <see cref="WorktreeBulkPolicy"/>.
    /// Unmerged/dirty/unsafe worktrees are refused unless <paramref name="explicitApprovalGranted"/> is true.
    /// </summary>
    public async Task<WorktreeBulkResult> RemoveBulkAsync(
        IReadOnlyList<WorktreeItem> selectedItems,
        bool explicitApprovalGranted,
        bool deleteBranches,
        bool allowUnmergedBranchDelete,
        IProgress<WorktreeBulkProgress>? progress,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var itemByPath = selectedItems.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var plan = WorktreeBulkPolicy.Plan(selectedItems.Select(item => item.ToBulkCandidate()));
        var targets = plan.GetApprovedTargets(explicitApprovalGranted)
            .Select(candidate => itemByPath[candidate.Path])
            .ToList();

        var removed = new List<WorktreeItem>();
        var failed = new List<(WorktreeItem Item, string Error)>();
        var skippedUnsafe = explicitApprovalGranted
            ? new List<WorktreeItem>()
            : plan.ApprovalRequiredItems.Select(blocked => itemByPath[blocked.Item.Path]).ToList();
        long bytesFreed = 0;
        var completed = 0;
        var resultsLock = new object();
        var touchedRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var gate = new SemaphoreSlim(3, 3);
        var tasks = targets.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                item.SetRemoving(true);
                var force = item.IsDirty && explicitApprovalGranted;
                var (success, message) = await RemoveAsync(
                    item,
                    force,
                    cancellationToken,
                    deleteBranch: false, // Defer branch delete until after repo prune so git sees worktree gone.
                    allowUnmergedBranchDelete: false,
                    pruneImmediately: false);

                lock (resultsLock)
                {
                    if (success)
                    {
                        removed.Add(item);
                        bytesFreed += item.SizeBytes ?? 0;
                        touchedRepos.Add(item.RepositoryPath);
                    }
                    else
                    {
                        failed.Add((item, message));
                    }

                    completed++;
                    progress?.Report(new WorktreeBulkProgress(completed, targets.Count, bytesFreed, item.Path));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (resultsLock)
                {
                    failed.Add((item, ex.Message));
                    completed++;
                    progress?.Report(new WorktreeBulkProgress(completed, targets.Count, bytesFreed, item.Path));
                }
            }
            finally
            {
                item.SetRemoving(false);
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Prune once per touched repository (much faster than pruning after every single worktree).
        foreach (var repoPath in touchedRepos)
        {
            await RunGitAsync(repoPath, ["worktree", "prune"], CancellationToken.None);
        }

        // Now safely delete branches for removed worktrees if requested and allowed by policy.
        var branchesDeleted = 0;
        if (deleteBranches)
        {
            foreach (var item in removed)
            {
                var (allowed, useForce, _) = WorktreeBulkPolicy.EvaluateBranchDeletion(
                    item.ToBulkCandidate(),
                    deleteBranchesRequested: true,
                    allowUnmergedBranchDelete: allowUnmergedBranchDelete && explicitApprovalGranted);
                if (!allowed)
                {
                    continue;
                }

                var branchFlag = useForce ? "-D" : "-d";
                var branchResult = await RunGitAsync(item.RepositoryPath, ["branch", branchFlag, item.Branch], CancellationToken.None);
                if (branchResult.ExitCode == 0)
                {
                    branchesDeleted++;
                }
            }
        }

        clock.Stop();
        return new WorktreeBulkResult(removed, failed, skippedUnsafe, bytesFreed, branchesDeleted, clock.Elapsed);
    }

    private static async Task<string> TryDeleteWorktreeBranchAsync(
        WorktreeItem item,
        bool deleteBranch,
        bool allowUnmergedBranchDelete,
        CancellationToken cancellationToken)
    {
        var (allowed, useForce, _) = WorktreeBulkPolicy.EvaluateBranchDeletion(
            item.ToBulkCandidate(),
            deleteBranch,
            allowUnmergedBranchDelete);
        if (!allowed)
        {
            return string.Empty;
        }

        var flag = useForce ? "-D" : "-d";
        var res = await RunGitAsync(item.RepositoryPath, ["branch", flag, item.Branch], cancellationToken);
        return res.ExitCode == 0
            ? $" Deleted local branch '{item.Branch}'."
            : $" Local branch '{item.Branch}' was kept ({FirstLine(res.Error)}).";
    }

    /// <summary>Clears git's records of worktrees whose folders no longer exist, across every repository.</summary>
    public async Task<int> PruneAllAsync(CancellationToken cancellationToken)
    {
        var pruned = 0;
        foreach (var repositoryPath in EnumerateRepositoryPaths(ResolveRepositoryRootPath()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await RunGitAsync(repositoryPath, ["worktree", "prune", "--verbose"], cancellationToken);
            if (result.ExitCode == 0)
            {
                pruned += result.Lines.Concat(result.Error.Split('\n')).Count(line => line.Contains("Removing", StringComparison.OrdinalIgnoreCase));
            }
        }

        return pruned;
    }

    private async Task AnalyzeAsync(WorktreeItem item, RepositoryContext context, PullRequestLookup pullRequestLookup, CancellationToken cancellationToken)
    {
        if (item.IsMissing)
        {
            item.ApplySize(0);
            item.ApplyAnalysis(
                WorktreeGit.Classify(true, item.IsDetached, item.HeadSha, context.BaseReference, context.BaseTipSha, null, null, null, false, []),
                null,
                null,
                null,
                true);
            return;
        }

        var status = await RunGitAsync(item.Path, ["status", "--porcelain"], cancellationToken);
        int? dirtyCount = status.ExitCode == 0 ? status.Lines.Count(line => !string.IsNullOrWhiteSpace(line)) : null;

        var pullRequests = item.IsDetached || context.GitHubRepository is null
            ? []
            : await pullRequestLookup.GetAsync(context.GitHubRepository, item.Branch, cancellationToken);

        string? containedIn = null;
        int? aheadCount = null;
        int? uniqueCommitCount = null;

        if (!string.IsNullOrEmpty(context.BaseReference))
        {
            // A branch aimed at a release branch is merged once that release branch holds it, so those count too.
            var candidates = new List<string> { context.BaseReference };
            candidates.AddRange(pullRequests.Where(pullRequest => pullRequest.IsMerged).Select(pullRequest => $"origin/{pullRequest.BaseBranch}"));

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var ancestor = await RunGitAsync(context.Path, ["merge-base", "--is-ancestor", item.HeadSha, candidate], cancellationToken);
                if (ancestor.ExitCode == 0)
                {
                    containedIn = candidate;
                    break;
                }
            }

            var ahead = await RunGitAsync(context.Path, ["rev-list", "--count", $"{context.BaseReference}..{item.HeadSha}"], cancellationToken);
            if (ahead.ExitCode == 0 && int.TryParse(ahead.Lines.FirstOrDefault(), out var parsedAhead))
            {
                aheadCount = parsedAhead;
            }

            if (containedIn is null && aheadCount is > 0 and <= PatchComparisonCommitLimit)
            {
                var cherry = await RunGitAsync(context.Path, ["cherry", context.BaseReference, item.HeadSha], cancellationToken, timeoutSeconds: 300);
                if (cherry.ExitCode == 0)
                {
                    uniqueCommitCount = cherry.Lines.Count(line => line.StartsWith('+'));
                }
            }
        }

        var remoteBranchExists = false;
        if (!item.IsDetached)
        {
            var remote = await RunGitAsync(context.Path, ["rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{item.Branch}"], cancellationToken);
            remoteBranchExists = remote.ExitCode == 0;
        }

        var isHeadOnAnyBranch = true;
        if (item.IsDetached)
        {
            var containing = await RunGitAsync(context.Path, ["for-each-ref", "--count=1", "--contains", item.HeadSha, "refs/heads", "refs/remotes"], cancellationToken);
            isHeadOnAnyBranch = containing.ExitCode != 0 || containing.Lines.Any(line => !string.IsNullOrWhiteSpace(line));
        }

        DateTimeOffset? lastCommitAt = null;
        var lastCommit = await RunGitAsync(context.Path, ["log", "-1", "--format=%cI", item.HeadSha], cancellationToken);
        if (lastCommit.ExitCode == 0 &&
            DateTimeOffset.TryParse(lastCommit.Lines.FirstOrDefault(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedDate))
        {
            lastCommitAt = parsedDate;
        }

        item.ApplyAnalysis(
            WorktreeGit.Classify(
                false,
                item.IsDetached,
                item.HeadSha,
                context.BaseReference,
                context.BaseTipSha,
                containedIn,
                aheadCount,
                uniqueCommitCount,
                remoteBranchExists,
                pullRequests),
            dirtyCount,
            aheadCount,
            lastCommitAt,
            isHeadOnAnyBranch);

        item.ApplySize(await _diskUsageAnalyzer.MeasureDirectoryBytesAsync(item.Path, cancellationToken));
    }

    private static async Task<RepositoryContext> BuildRepositoryContextAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        // Merged-or-not is judged against origin, so stale remote refs would only ever under-report merges.
        // A failed fetch (offline, VPN down) is therefore safe to ignore.
        await RunGitAsync(repositoryPath, ["fetch", "origin", "--prune", "--quiet"], cancellationToken, timeoutSeconds: 90);

        var baseReference = string.Empty;
        var head = await RunGitAsync(repositoryPath, ["symbolic-ref", "--quiet", "refs/remotes/origin/HEAD"], cancellationToken);
        if (head.ExitCode == 0 && head.Lines.FirstOrDefault() is { Length: > 0 } headReference)
        {
            baseReference = headReference.Replace("refs/remotes/", string.Empty, StringComparison.Ordinal);
        }
        else
        {
            foreach (var candidate in new[] { "origin/master", "origin/main" })
            {
                var verify = await RunGitAsync(repositoryPath, ["rev-parse", "--verify", "--quiet", candidate], cancellationToken);
                if (verify.ExitCode == 0)
                {
                    baseReference = candidate;
                    break;
                }
            }
        }

        var baseTipSha = string.Empty;
        if (!string.IsNullOrEmpty(baseReference))
        {
            var tip = await RunGitAsync(repositoryPath, ["rev-parse", baseReference], cancellationToken);
            baseTipSha = tip.ExitCode == 0 ? tip.Lines.FirstOrDefault() ?? string.Empty : string.Empty;
        }

        var remoteUrl = await RunGitAsync(repositoryPath, ["remote", "get-url", "origin"], cancellationToken);
        var parsedRemote = remoteUrl.ExitCode == 0 ? WorktreeGit.ParseGitHubRemote(remoteUrl.Lines.FirstOrDefault()) : null;
        var gitHubRepository = parsedRemote is null ? null : new RepositoryDefinition(parsedRemote.Value.Owner, parsedRemote.Value.Name);

        return new RepositoryContext(repositoryPath, baseReference, baseTipSha, gitHubRepository);
    }

    private static List<string> EnumerateRepositoryPaths(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return [];
        }

        try
        {
            // Only main checkouts own worktrees. A linked worktree has a .git file, not a .git folder.
            return Directory.EnumerateDirectories(rootPath)
                .Where(path => Directory.Exists(Path.Combine(path, ".git")))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string ResolveRepositoryRootPath()
    {
        return RepositoryRootLocator.Resolve();
    }

    private static async Task<GitResult> RunGitAsync(string workingDirectory, IEnumerable<string> arguments, CancellationToken cancellationToken, int timeoutSeconds = 60)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Never let git stop to ask for credentials: there is no console for it to ask on.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return new GitResult(-1, [], "git could not be started.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                cancellationToken.ThrowIfCancellationRequested();
                return new GitResult(-1, [], $"git timed out after {timeoutSeconds} seconds.");
            }

            var output = await outputTask;
            var error = await errorTask;
            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            return new GitResult(process.ExitCode, lines, error.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new GitResult(-1, [], ex.Message);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static string FirstLine(string text)
    {
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "git gave no reason.";
    }

    private sealed record GitResult(int ExitCode, IReadOnlyList<string> Lines, string Error);

    private sealed record RepositoryWorktrees(string Path, IReadOnlyList<WorktreeItem> Items);

    private sealed record RepositoryContext(string Path, string BaseReference, string BaseTipSha, RepositoryDefinition? GitHubRepository);

    /// <summary>
    /// Looks up pull requests per branch, and stops asking after the first failure so a missing token or an
    /// outage costs one failed call rather than one per worktree.
    /// </summary>
    private sealed class PullRequestLookup(GitHubApiClient gitHubApiClient)
    {
        public string? FailureMessage { get; private set; }

        public async Task<IReadOnlyList<BranchPullRequest>> GetAsync(RepositoryDefinition repository, string branch, CancellationToken cancellationToken)
        {
            if (FailureMessage is not null || string.IsNullOrWhiteSpace(branch))
            {
                return [];
            }

            try
            {
                return await gitHubApiClient.GetPullRequestsForBranchAsync(repository, branch, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Error($"Pull request lookup failed for {repository.FullName} {branch}.", ex);
                FailureMessage = FirstLine(ex.Message);
                return [];
            }
        }
    }
}

public sealed record WorktreeSnapshot(DateTimeOffset CapturedAt, IReadOnlyList<WorktreeItem> Items, string RootPath, int RepositoryCount, string Note);

public sealed record WorktreeProgress(string StatusText, IReadOnlyList<WorktreeItem> Items, bool IsCompleted);

public sealed record WorktreeBulkProgress(int Completed, int Total, long BytesFreed, string CurrentPath);

public sealed record WorktreeBulkResult(
    IReadOnlyList<WorktreeItem> Removed,
    IReadOnlyList<(WorktreeItem Item, string Error)> Failed,
    IReadOnlyList<WorktreeItem> SkippedUnsafe,
    long BytesFreed,
    int BranchesDeleted,
    TimeSpan Elapsed);
