using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DevTools.Infrastructure;
using DevTools.Models;

namespace DevTools.Services;

public sealed class GitHubApiClient
{
    private enum AuthSource
    {
        ManualPat,
        EnvironmentToken,
        GhCliToken
    }

    private static readonly string[] GhExecutableCandidates =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe")
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// AI review bots that do not carry the [bot] login suffix, so they cannot be detected by shape alone.
    /// </summary>
    private static readonly HashSet<string> KnownBotReviewers = new(StringComparer.OrdinalIgnoreCase)
    {
        "copilot-pull-request-reviewer",
        "chatgpt-codex-connector",
        "github-actions"
    };

    private static readonly Regex TargetProcessIdPattern = new(@"^(?<id>\d+)\s*-", RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private string? _token;
    private string? _ghExecutablePath;
    private string? _authenticatedLogin;
    private bool _isManualPatPinned;
    private AuthSource? _authSource;

    public GitHubApiClient()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/")
        };

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DevTools/1.0");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<string> GetAuthSummaryAsync(CancellationToken cancellationToken)
    {
        AppLog.Info("GetAuthSummaryAsync started.");
        try
        {
            var configuredCredential = await EnsureTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(configuredCredential))
            {
                AppLog.Info("GetAuthSummaryAsync found no token.");
                return "No token configured. Paste a PAT and click Use PAT, or click Use gh Token for a temporary session token.";
            }

            AppLog.Info("GetAuthSummaryAsync succeeded.");
            return _authSource switch
            {
                AuthSource.ManualPat => "Using manual PAT (default).",
                AuthSource.EnvironmentToken => "Using token from GH_TOKEN/GITHUB_TOKEN.",
                AuthSource.GhCliToken => "Using temporary token from gh CLI.",
                _ => "GitHub auth ready."
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("GetAuthSummaryAsync failed.", ex);
            return $"GitHub auth failed: {ex.Message}";
        }
    }

    public async Task<string> ReloadAuthAsync(CancellationToken cancellationToken)
    {
        if (!_isManualPatPinned)
        {
            _token = null;
            _authSource = null;
        }

        return await GetAuthSummaryAsync(cancellationToken);
    }

    public void SetPersonalAccessToken(string? personalAccessToken)
    {
        var trimmed = personalAccessToken?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        _token = trimmed;
        _isManualPatPinned = true;
        _authSource = AuthSource.ManualPat;
        AppLog.Info($"SetPersonalAccessToken applied. Length={_token.Length}.");
    }

    public void ClearPersonalAccessToken()
    {
        if (_isManualPatPinned)
        {
            _token = null;
            _authSource = null;
        }

        _isManualPatPinned = false;
        AppLog.Info("ClearPersonalAccessToken executed.");
    }

    public async Task<string> UseGhTokenAsync(CancellationToken cancellationToken)
    {
        var (output, error, exitCode) = await RunGhCommandAsync("auth token", cancellationToken);
        if (exitCode != 0)
        {
            AppLog.Error($"UseGhTokenAsync failed. ExitCode={exitCode}. Error={error}");
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "Unable to load token from gh auth."
                : error.Trim());
        }

        var ghCredential = output.Trim();
        if (string.IsNullOrWhiteSpace(ghCredential))
        {
            throw new InvalidOperationException("gh auth returned an empty token.");
        }

        _token = ghCredential;
        _isManualPatPinned = false;
        _authSource = AuthSource.GhCliToken;
        AppLog.Info($"UseGhTokenAsync succeeded. Length={_token.Length}.");
        return "Using temporary token from gh CLI for this session.";
    }

    public bool TryLaunchGhLogin()
    {
        var ghExecutablePath = ResolveGhExecutablePath();
        if (string.IsNullOrWhiteSpace(ghExecutablePath))
        {
            AppLog.Info("TryLaunchGhLogin failed because gh.exe was not found.");
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k \"\"{ghExecutablePath}\" auth login -h github.com -w\"",
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        Process.Start(startInfo);
        AppLog.Info($"TryLaunchGhLogin launched cmd.exe for {ghExecutablePath}.");
        return true;
    }

    public async Task<IReadOnlyList<WorkflowRunItem>> GetWorkflowRunsAsync(RepositoryDefinition repository, int perPage, CancellationToken cancellationToken)
    {
        AppLog.Info($"GetWorkflowRunsAsync started for {repository.FullName}.");
        await EnsureTokenConfiguredAsync(cancellationToken);

        var response = await SendAsync<WorkflowRunsResponse>(
            $"repos/{repository.Owner}/{repository.Name}/actions/runs?per_page={perPage}",
            cancellationToken);

        var runs = response.WorkflowRuns
            .Select(run => new WorkflowRunItem
            {
                Repository = repository,
                Id = run.Id,
                WorkflowName = string.IsNullOrWhiteSpace(run.Name) ? "Unknown Workflow" : run.Name,
                DisplayTitle = string.IsNullOrWhiteSpace(run.DisplayTitle) ? "No title" : run.DisplayTitle,
                EventName = string.IsNullOrWhiteSpace(run.EventName) ? "unknown" : run.EventName,
                UiBranch = string.IsNullOrWhiteSpace(run.HeadBranch) ? "unknown" : run.HeadBranch,
                UiSha = string.IsNullOrWhiteSpace(run.HeadSha) ? "unknown" : run.HeadSha,
                Status = string.IsNullOrWhiteSpace(run.Status) ? "unknown" : run.Status,
                Conclusion = run.Conclusion ?? string.Empty,
                CreatedAt = run.CreatedAt ?? DateTimeOffset.MinValue,
                UpdatedAt = run.UpdatedAt ?? DateTimeOffset.MinValue,
                RunStartedAt = run.RunStartedAt,
                RunUrl = run.HtmlUrl ?? $"https://github.com/{repository.FullName}/actions/runs/{run.Id}",
                DisplayedCommitUrl = BuildCommitUrl(repository, run.HeadSha),
                ActorLogin = run.Actor?.Login ?? string.Empty,
                TriggeringActorLogin = run.TriggeringActor?.Login ?? string.Empty,
                CommitAuthorDisplay = GetCommitAuthorDisplay(run.HeadCommit),
                AssociatedPullRequestNumbers = ExtractPullRequestNumbers(run.PullRequests)
            })
            .ToList();

        AppLog.Info($"GetWorkflowRunsAsync returned {runs.Count} runs for {repository.FullName}.");
        return runs;
    }

    /// <summary>
    /// Login of the account the configured token belongs to. Cached because it never changes for a token.
    /// </summary>
    public async Task<string> GetAuthenticatedLoginAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_authenticatedLogin))
        {
            return _authenticatedLogin;
        }

        await EnsureTokenConfiguredAsync(cancellationToken);

        var user = await SendAsync<ActorDto>("user", cancellationToken);
        _authenticatedLogin = user.Login?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(_authenticatedLogin))
        {
            throw new InvalidOperationException("GitHub did not return a login for the configured token.");
        }

        AppLog.Info($"GetAuthenticatedLoginAsync resolved {_authenticatedLogin}.");
        return _authenticatedLogin;
    }

    /// <summary>
    /// Every open pull request the authenticated user has authored across an owner, with review state resolved.
    /// The search endpoint is used rather than per-repo listing so PRs in repos that are not configured in the
    /// Runs tab still show up.
    /// </summary>
    public async Task<IReadOnlyList<OpenPullRequestItem>> GetOpenPullRequestsAsync(
        IReadOnlyList<string> owners,
        CancellationToken cancellationToken)
    {
        await EnsureTokenConfiguredAsync(cancellationToken);

        var login = await GetAuthenticatedLoginAsync(cancellationToken);
        var results = new List<OpenPullRequestItem>();

        foreach (var owner in owners.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var query = Uri.EscapeDataString($"is:pr is:open author:{login} org:{owner}");
            var response = await SendAsync<SearchIssuesResponse>(
                $"search/issues?q={query}&per_page=100&sort=created&order=asc",
                cancellationToken);

            AppLog.Info($"GetOpenPullRequestsAsync found {response.Items.Count} open PRs in {owner}.");

            foreach (var item in response.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var repository = ParseRepositoryFromApiUrl(item.RepositoryUrl);
                if (repository is null)
                {
                    AppLog.Info($"Skipped PR {item.Number} because its repository URL could not be parsed.");
                    continue;
                }

                var review = await GetReviewOutcomeAsync(repository, item.Number, cancellationToken);

                results.Add(new OpenPullRequestItem
                {
                    Repository = repository,
                    Number = item.Number,
                    Title = string.IsNullOrWhiteSpace(item.Title) ? "No title" : item.Title.Trim(),
                    HtmlUrl = item.HtmlUrl ?? $"https://github.com/{repository.FullName}/pull/{item.Number}",
                    AuthorLogin = item.User?.Login ?? login,
                    CreatedAt = item.CreatedAt ?? DateTimeOffset.MinValue,
                    UpdatedAt = item.UpdatedAt ?? item.CreatedAt ?? DateTimeOffset.MinValue,
                    IsDraft = item.Draft,
                    ReviewState = review.State,
                    Approvers = review.Approvers,
                    ChangeRequesters = review.ChangeRequesters,
                    TargetProcessId = ParseTargetProcessId(item.Title)
                });
            }
        }

        return results
            .OrderBy(pullRequest => pullRequest.ReviewSortRank)
            .ThenBy(pullRequest => pullRequest.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Every pull request, open or not, whose head is the given branch of the repository itself (not a fork).
    /// </summary>
    public async Task<IReadOnlyList<BranchPullRequest>> GetPullRequestsForBranchAsync(
        RepositoryDefinition repository,
        string branch,
        CancellationToken cancellationToken)
    {
        await EnsureTokenConfiguredAsync(cancellationToken);

        var head = Uri.EscapeDataString($"{repository.Owner}:{branch}");
        var response = await SendAsync<List<BranchPullRequestDto>>(
            $"repos/{repository.Owner}/{repository.Name}/pulls?state=all&head={head}&per_page=20",
            cancellationToken);

        return response
            .Select(item => new BranchPullRequest(
                item.Number,
                string.Equals(item.State, "open", StringComparison.OrdinalIgnoreCase),
                item.MergedAt is not null,
                item.Draft,
                item.Head?.Sha ?? string.Empty,
                item.Base?.Ref ?? string.Empty,
                item.HtmlUrl ?? $"https://github.com/{repository.FullName}/pull/{item.Number}"))
            .ToList();
    }

    /// <summary>
    /// Collapses a PR's review history to a single outcome.
    /// <para>
    /// GitHub's own reviewDecision is deliberately not used: it reports merge readiness, so it can still read
    /// REVIEW_REQUIRED after a genuine human approval when branch protection wants a second reviewer or a
    /// CODEOWNER. What matters here is whether a person signed off, so only the latest review per human
    /// reviewer counts, and bot reviewers are ignored entirely.
    /// </para>
    /// </summary>
    private async Task<(PullRequestReviewState State, IReadOnlyList<string> Approvers, IReadOnlyList<string> ChangeRequesters)>
        GetReviewOutcomeAsync(RepositoryDefinition repository, int number, CancellationToken cancellationToken)
    {
        var reviews = await SendAsync<List<ReviewDto>>(
            $"repos/{repository.Owner}/{repository.Name}/pulls/{number}/reviews?per_page=100",
            cancellationToken);

        var latestStateByReviewer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var review in reviews)
        {
            var reviewer = review.User?.Login?.Trim();
            if (string.IsNullOrWhiteSpace(reviewer) || IsBotReviewer(reviewer))
            {
                continue;
            }

            var state = review.State?.Trim().ToUpperInvariant() ?? string.Empty;

            // COMMENTED and DISMISSED leave the reviewer's standing verdict untouched, so they are not
            // recorded as the latest state. The API returns reviews oldest first, so the last write wins.
            if (state is "APPROVED" or "CHANGES_REQUESTED")
            {
                latestStateByReviewer[reviewer] = state;
            }
        }

        var approvers = latestStateByReviewer
            .Where(entry => entry.Value == "APPROVED")
            .Select(entry => entry.Key)
            .OrderBy(login => login, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var changeRequesters = latestStateByReviewer
            .Where(entry => entry.Value == "CHANGES_REQUESTED")
            .Select(entry => entry.Key)
            .OrderBy(login => login, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (changeRequesters.Count > 0)
        {
            return (PullRequestReviewState.ChangesRequested, approvers, changeRequesters);
        }

        return approvers.Count > 0
            ? (PullRequestReviewState.Approved, approvers, changeRequesters)
            : (PullRequestReviewState.AwaitingReview, approvers, changeRequesters);
    }

    /// <summary>
    /// Review bots do not gate a merge, so their verdicts must not decide the review badge. Most identify
    /// themselves with a [bot] suffix; the AI reviewers wired into these repos do not, so they are named.
    /// </summary>
    private static bool IsBotReviewer(string login)
    {
        if (login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return KnownBotReviewers.Contains(login);
    }

    /// <summary>
    /// Pulls the ticket number off a commit or title of the form "&lt;number&gt; - [Category] - Short description".
    /// A leading 0 is the house convention for "no ticket", so it resolves to null rather than ticket 0.
    /// </summary>
    private static int? ParseTargetProcessId(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var match = TargetProcessIdPattern.Match(title.Trim());
        if (!match.Success)
        {
            return null;
        }

        return int.TryParse(match.Groups["id"].Value, out var id) && id > 0 ? id : null;
    }

    private static RepositoryDefinition? ParseRepositoryFromApiUrl(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return null;
        }

        var segments = repositoryUrl
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length < 2)
        {
            return null;
        }

        return new RepositoryDefinition(segments[^2], segments[^1]);
    }

    public async Task ResolveRunDetailsAsync(WorkflowRunItem run, CancellationToken cancellationToken)
    {
        AppLog.Info($"ResolveRunDetailsAsync started for run {run.Id}.");
        await EnsureTokenConfiguredAsync(cancellationToken);

        run.IsResolving = true;
        run.ResolutionError = null;

        try
        {
            if (!run.IsWorkflowRun)
            {
                run.ActualBranch = run.UiBranch;
                run.ActualSha = run.UiSha;
                run.ActualCommitUrl = run.DisplayedCommitUrl;
                await PopulatePullRequestAsync(run, run.UiSha, cancellationToken);
                await PopulatePullRequestFromReferencesAsync(run, run.AssociatedPullRequestNumbers, cancellationToken);
                if (run.IsManualDispatch)
                {
                    await PopulateCommitDescriptionAsync(run, run.UiSha, cancellationToken);
                }
                return;
            }

            var runDetails = await SendAsync<WorkflowRunDto>(
                $"repos/{run.Repository.Owner}/{run.Repository.Name}/actions/runs/{run.Id}",
                cancellationToken);
            var referencedPullRequestNumbers = MergePullRequestNumbers(
                run.AssociatedPullRequestNumbers,
                ExtractPullRequestNumbers(runDetails.PullRequests));
            run.CommitAuthorDisplay ??= GetCommitAuthorDisplay(runDetails.HeadCommit);
            run.RunStartedAt ??= runDetails.RunStartedAt;

            var jobsResponse = await SendAsync<JobsResponse>(
                $"repos/{run.Repository.Owner}/{run.Repository.Name}/actions/runs/{run.Id}/jobs",
                cancellationToken);

            var detectJob = jobsResponse.Jobs.FirstOrDefault(job =>
                string.Equals(job.Name, "Modified Service Detection", StringComparison.OrdinalIgnoreCase));
            var setBranchJob = jobsResponse.Jobs.FirstOrDefault(job =>
                string.Equals(job.Name, "Set Branch Name", StringComparison.OrdinalIgnoreCase));

            run.DetectJobUrl = detectJob?.HtmlUrl;
            run.SetBranchJobUrl = setBranchJob?.HtmlUrl;

            var logText = string.Empty;
            if (detectJob is not null)
            {
                logText = await GetJobLogAsync(run.Repository, detectJob.Id, cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(logText) && setBranchJob is not null)
            {
                logText = await GetJobLogAsync(run.Repository, setBranchJob.Id, cancellationToken);
            }

            var actualBranch = ParseActualBranch(logText) ?? run.UiBranch;
            var actualSha = ParseActualSha(logText) ?? run.UiSha;

            run.ActualBranch = actualBranch;
            run.ActualSha = actualSha;
            run.ActualCommitUrl = BuildCommitUrl(run.Repository, actualSha);

            await PopulatePullRequestAsync(run, actualSha, cancellationToken);
            await PopulatePullRequestFromReferencesAsync(run, referencedPullRequestNumbers, cancellationToken);
            await PopulateUpstreamRunAsync(run, actualBranch, actualSha, cancellationToken);
            AppLog.Info($"ResolveRunDetailsAsync resolved run {run.Id} to {actualBranch} @ {actualSha}.");
        }
        catch (Exception ex)
        {
            run.ResolutionError = ex.Message;
            AppLog.Error($"ResolveRunDetailsAsync failed for run {run.Id}.", ex);
        }
        finally
        {
            run.IsResolving = false;
            AppLog.Info($"ResolveRunDetailsAsync finished for run {run.Id}.");
        }
    }

    private async Task PopulatePullRequestAsync(WorkflowRunItem run, string sha, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sha) || string.Equals(sha, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var pulls = await SendAsync<List<PullRequestDto>>(
            $"repos/{run.Repository.Owner}/{run.Repository.Name}/commits/{sha}/pulls",
            cancellationToken);

        var pull = pulls
            .OrderByDescending(item => item.MergedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (pull is null)
        {
            return;
        }

        run.PullRequestUrl = pull.HtmlUrl;
        run.PullRequestTitle = $"PR #{pull.Number}: {pull.Title}";
        run.PullRequestDescriptionRaw = pull.Body?.Trim();
        run.PullRequestAuthor = pull.User?.Login;
    }

    private async Task PopulatePullRequestFromReferencesAsync(
        WorkflowRunItem run,
        IReadOnlyList<int> pullRequestNumbers,
        CancellationToken cancellationToken)
    {
        if (run.HasPullRequestUrl || pullRequestNumbers.Count == 0)
        {
            return;
        }

        foreach (var pullRequestNumber in pullRequestNumbers.Distinct())
        {
            if (pullRequestNumber <= 0)
            {
                continue;
            }

            var pull = await SendAsync<PullRequestDto>(
                $"repos/{run.Repository.Owner}/{run.Repository.Name}/pulls/{pullRequestNumber}",
                cancellationToken);

            run.PullRequestUrl = pull.HtmlUrl;
            run.PullRequestTitle = $"PR #{pull.Number}: {pull.Title}";
            run.PullRequestDescriptionRaw = pull.Body?.Trim();
            run.PullRequestAuthor = pull.User?.Login;

            if (run.HasPullRequestUrl)
            {
                return;
            }
        }
    }

    private async Task PopulateCommitDescriptionAsync(WorkflowRunItem run, string sha, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sha) || string.Equals(sha, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var commit = await SendAsync<CommitDto>(
            $"repos/{run.Repository.Owner}/{run.Repository.Name}/commits/{sha}",
            cancellationToken);

        run.CommitDescriptionRaw = commit.Commit?.Message?.Trim();
    }

    private async Task PopulateUpstreamRunAsync(WorkflowRunItem run, string actualBranch, string actualSha, CancellationToken cancellationToken)
    {
        var response = await SendAsync<WorkflowRunsResponse>(
            $"repos/{run.Repository.Owner}/{run.Repository.Name}/actions/runs?per_page=100",
            cancellationToken);

        var upstream = response.WorkflowRuns
            .Where(candidate =>
                candidate.Id != run.Id &&
                !string.Equals(candidate.EventName, "workflow_run", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.HeadBranch, actualBranch, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.HeadSha, actualSha, StringComparison.OrdinalIgnoreCase) &&
                (candidate.CreatedAt ?? DateTimeOffset.MaxValue) <= run.CreatedAt)
            .OrderByDescending(candidate => candidate.CreatedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        if (upstream is null)
        {
            return;
        }

        run.UpstreamRunUrl = upstream.HtmlUrl;
        run.UpstreamRunLabel = $"{upstream.Name} ({upstream.EventName})";
    }

    private async Task<string> GetJobLogAsync(RepositoryDefinition repository, long jobId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"repos/{repository.Owner}/{repository.Name}/actions/jobs/{jobId}/logs");

        AddAuthHeader(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<T> SendAsync<T>(string relativeUrl, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        AddAuthHeader(request);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            AppLog.Error($"GitHub API call failed: {relativeUrl} => {(int)response.StatusCode} {response.StatusCode}. Body: {content}");
            throw new InvalidOperationException($"GitHub API {response.StatusCode}: {content}");
        }

        var result = JsonSerializer.Deserialize<T>(content, JsonOptions);
        if (result is null)
        {
            throw new InvalidOperationException($"GitHub API returned empty JSON for {relativeUrl}.");
        }

        return result;
    }

    private void AddAuthHeader(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
    }

    private async Task EnsureTokenConfiguredAsync(CancellationToken cancellationToken)
    {
        var configuredCredential = await EnsureTokenAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(configuredCredential))
        {
            throw new InvalidOperationException(
                "No token configured. Paste a PAT and click Use PAT, or click Use gh Token for a temporary session token.");
        }
    }

    private async Task<string?> EnsureTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_token))
        {
            AppLog.Info("EnsureTokenAsync reused cached token.");
            return _token;
        }

        var envToken =
            Environment.GetEnvironmentVariable("GH_TOKEN") ??
            Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        if (!string.IsNullOrWhiteSpace(envToken))
        {
            _token = envToken.Trim();
            _authSource = AuthSource.EnvironmentToken;
            AppLog.Info("EnsureTokenAsync loaded token from environment.");
            return _token;
        }

        AppLog.Info("EnsureTokenAsync found no PAT or environment token.");
        await Task.CompletedTask;
        return null;
    }

    private async Task<(string Output, string Error, int ExitCode)> RunGhCommandAsync(string arguments, CancellationToken cancellationToken)
    {
        var ghExecutablePath = ResolveGhExecutablePath();
        if (string.IsNullOrWhiteSpace(ghExecutablePath))
        {
            throw new InvalidOperationException("GitHub CLI was not found. Install GitHub CLI or add gh.exe to this machine.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = ghExecutablePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            throw new InvalidOperationException("Unable to start GitHub CLI.");
        }

        AppLog.Info($"RunGhCommandAsync started. File={ghExecutablePath}, Arguments={arguments}");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var waitTask = process.WaitForExitAsync(cancellationToken);
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

        var completedTask = await Task.WhenAny(waitTask, timeoutTask);
        if (completedTask == timeoutTask)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                }
            }
            catch
            {
            }

            throw new InvalidOperationException($"Timed out waiting for gh: {arguments}");
        }

        var output = await outputTask;
        var error = await errorTask;
        AppLog.Info($"RunGhCommandAsync finished. ExitCode={process.ExitCode}, OutputLength={output.Length}, ErrorLength={error.Length}");
        return (output, error, process.ExitCode);
    }

    private string? ResolveGhExecutablePath()
    {
        if (!string.IsNullOrWhiteSpace(_ghExecutablePath) && File.Exists(_ghExecutablePath))
        {
            return _ghExecutablePath;
        }

        foreach (var candidate in GhExecutableCandidates.Where(File.Exists))
        {
            _ghExecutablePath = candidate;
            AppLog.Info($"Resolved gh executable from candidate path: {_ghExecutablePath}");
            return _ghExecutablePath;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "where.exe",
                Arguments = "gh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            process.WaitForExit(2000);
            var output = process.StandardOutput.ReadToEnd();
            var firstMatch = output
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(File.Exists);

            if (!string.IsNullOrWhiteSpace(firstMatch))
            {
                _ghExecutablePath = firstMatch;
                AppLog.Info($"Resolved gh executable from where.exe: {_ghExecutablePath}");
            }
        }
        catch
        {
        }

        return _ghExecutablePath;
    }

    private static string BuildCommitUrl(RepositoryDefinition repository, string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return $"https://github.com/{repository.FullName}";
        }

        return $"https://github.com/{repository.FullName}/commit/{sha}";
    }

    private static IReadOnlyList<int> ExtractPullRequestNumbers(IReadOnlyList<PullRequestReferenceDto>? pullRequests)
    {
        if (pullRequests is null || pullRequests.Count == 0)
        {
            return [];
        }

        return pullRequests
            .Select(pullRequest => pullRequest.Number)
            .Where(number => number > 0)
            .Distinct()
            .ToList();
    }

    private static IReadOnlyList<int> MergePullRequestNumbers(
        IReadOnlyList<int> left,
        IReadOnlyList<int> right)
    {
        if (left.Count == 0 && right.Count == 0)
        {
            return [];
        }

        return left
            .Concat(right)
            .Where(number => number > 0)
            .Distinct()
            .ToList();
    }

    private static string? GetCommitAuthorDisplay(HeadCommitDto? headCommit)
    {
        return headCommit?.Author?.Name?.Trim();
    }

    private static string? ParseActualBranch(string logText)
    {
        if (string.IsNullOrWhiteSpace(logText))
        {
            return null;
        }

        var patterns = new[]
        {
            new Regex("BRANCH_NAME=\"(?<branch>[^\"]+)\"", RegexOptions.Compiled),
            new Regex("ref:\\s+(?<branch>[A-Za-z0-9._\\-/]+)", RegexOptions.Compiled),
            new Regex("refs/remotes/origin/(?<branch>[A-Za-z0-9._\\-/]+)", RegexOptions.Compiled)
        };

        foreach (var pattern in patterns)
        {
            var match = pattern.Match(logText);
            if (match.Success)
            {
                return match.Groups["branch"].Value.Trim();
            }
        }

        return null;
    }

    private static string? ParseActualSha(string logText)
    {
        if (string.IsNullOrWhiteSpace(logText))
        {
            return null;
        }

        var fullShaPattern = new Regex("(?<sha>[0-9a-f]{40})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        var gitLogMarker = "[command]/usr/bin/git log -1 --format=%H";
        var markerIndex = logText.IndexOf(gitLogMarker, StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            var tail = logText[markerIndex..];
            var match = fullShaPattern.Match(tail);
            if (match.Success)
            {
                return match.Groups["sha"].Value;
            }
        }

        var headMatch = new Regex("HEAD is now at\\s+(?<sha>[0-9a-f]{40})", RegexOptions.Compiled | RegexOptions.IgnoreCase)
            .Match(logText);

        return headMatch.Success ? headMatch.Groups["sha"].Value : null;
    }

    private sealed class WorkflowRunsResponse
    {
        [JsonPropertyName("workflow_runs")]
        public List<WorkflowRunDto> WorkflowRuns { get; set; } = [];
    }

    private sealed class WorkflowRunDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("conclusion")]
        public string? Conclusion { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }

        [JsonPropertyName("run_started_at")]
        public DateTimeOffset? RunStartedAt { get; set; }

        [JsonPropertyName("head_branch")]
        public string? HeadBranch { get; set; }

        [JsonPropertyName("head_sha")]
        public string? HeadSha { get; set; }

        [JsonPropertyName("display_title")]
        public string? DisplayTitle { get; set; }

        [JsonPropertyName("event")]
        public string? EventName { get; set; }

        [JsonPropertyName("actor")]
        public ActorDto? Actor { get; set; }

        [JsonPropertyName("triggering_actor")]
        public ActorDto? TriggeringActor { get; set; }

        [JsonPropertyName("head_commit")]
        public HeadCommitDto? HeadCommit { get; set; }

        [JsonPropertyName("pull_requests")]
        public List<PullRequestReferenceDto> PullRequests { get; set; } = [];
    }

    private sealed class JobsResponse
    {
        [JsonPropertyName("jobs")]
        public List<JobDto> Jobs { get; set; } = [];
    }

    private sealed class JobDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }
    }

    private sealed class PullRequestDto
    {
        [JsonPropertyName("number")]
        public int Number { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("merged_at")]
        public DateTimeOffset? MergedAt { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("user")]
        public ActorDto? User { get; set; }
    }

    private sealed class PullRequestReferenceDto
    {
        [JsonPropertyName("number")]
        public int Number { get; set; }
    }

    private sealed class CommitDto
    {
        [JsonPropertyName("commit")]
        public CommitMetadataDto? Commit { get; set; }
    }

    private sealed class HeadCommitDto
    {
        [JsonPropertyName("author")]
        public CommitAuthorDto? Author { get; set; }
    }

    private sealed class CommitAuthorDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class CommitMetadataDto
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    private sealed class ActorDto
    {
        [JsonPropertyName("login")]
        public string? Login { get; set; }
    }

    private sealed class SearchIssuesResponse
    {
        [JsonPropertyName("items")]
        public List<SearchIssueDto> Items { get; set; } = [];
    }

    private sealed class SearchIssueDto
    {
        [JsonPropertyName("number")]
        public int Number { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("repository_url")]
        public string? RepositoryUrl { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }

        [JsonPropertyName("updated_at")]
        public DateTimeOffset? UpdatedAt { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("user")]
        public ActorDto? User { get; set; }
    }

    private sealed class ReviewDto
    {
        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("user")]
        public ActorDto? User { get; set; }
    }

    private sealed class BranchPullRequestDto
    {
        [JsonPropertyName("number")]
        public int Number { get; set; }

        [JsonPropertyName("state")]
        public string? State { get; set; }

        [JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonPropertyName("merged_at")]
        public DateTimeOffset? MergedAt { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("head")]
        public BranchPullRequestRefDto? Head { get; set; }

        [JsonPropertyName("base")]
        public BranchPullRequestRefDto? Base { get; set; }
    }

    private sealed class BranchPullRequestRefDto
    {
        [JsonPropertyName("sha")]
        public string? Sha { get; set; }

        [JsonPropertyName("ref")]
        public string? Ref { get; set; }
    }
}
