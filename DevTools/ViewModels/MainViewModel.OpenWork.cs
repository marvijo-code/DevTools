using System.Collections.ObjectModel;
using DevTools.Infrastructure;
using DevTools.Models;
using DevTools.Services;

namespace DevTools.ViewModels;

/// <summary>
/// The Open Work tab: the open pull requests this account authored, plus the open TargetProcess tickets
/// assigned to the configured user, with each side linked to the other by ticket number.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly TargetProcessClient _targetProcessClient = new();
    private readonly SemaphoreSlim _openWorkRefreshGate = new(1, 1);
    private string? _encryptedTargetProcessConnectionString;
    private string _targetProcessPortalUrl = string.Empty;
    private int _targetProcessUserId;
    private string _targetProcessUserSearch = string.Empty;
    private string _openWorkStatusText = "Open work has not been loaded yet.";
    private string _openWorkPullRequestSummary = "No pull requests loaded.";
    private string _openWorkTicketSummary = "No TargetProcess tickets loaded.";
    private bool _isOpenWorkRefreshing;
    private bool _hasOpenWorkSnapshot;

    public ObservableCollection<OpenPullRequestItem> OpenPullRequests { get; } = [];

    public ObservableCollection<TargetProcessTicketItem> OpenTickets { get; } = [];

    public ObservableCollection<TargetProcessTicketItem> RecurringTrackers { get; } = [];

    public bool IsOpenWorkVisible => _activeTab == MainContentTab.OpenWork;

    public bool IsOpenWorkRefreshing
    {
        get => _isOpenWorkRefreshing;
        private set => SetProperty(ref _isOpenWorkRefreshing, value);
    }

    public bool HasOpenWorkSnapshot => _hasOpenWorkSnapshot;

    public string OpenWorkStatusText => _openWorkStatusText;

    public string OpenWorkPullRequestSummary => _openWorkPullRequestSummary;

    public string OpenWorkTicketSummary => _openWorkTicketSummary;

    public bool HasRecurringTrackers => RecurringTrackers.Count > 0;

    public string RecurringTrackerSummary =>
        RecurringTrackers.Count == 0
            ? string.Empty
            : $"{RecurringTrackers.Count} recurring tracker(s) kept out of the list above";

    public string TargetProcessUserSearch
    {
        get => _targetProcessUserSearch;
        set => SetProperty(ref _targetProcessUserSearch, value);
    }

    /// <summary>
    /// Root of the TargetProcess web portal, for example <c>https://example.tpondemand.com</c>. Only used to
    /// build "Open Ticket" links, so it stays optional.
    /// </summary>
    public string TargetProcessPortalUrl
    {
        get => _targetProcessPortalUrl;
        set
        {
            var trimmed = value?.Trim().TrimEnd('/') ?? string.Empty;
            if (SetProperty(ref _targetProcessPortalUrl, trimmed))
            {
                _targetProcessClient.SetPortalBaseUrl(trimmed);
                SaveSettings();
            }
        }
    }

    public int TargetProcessUserId
    {
        get => _targetProcessUserId;
        set
        {
            if (SetProperty(ref _targetProcessUserId, value < 0 ? 0 : value))
            {
                SaveSettings();
                RaisePropertyChanged(nameof(TargetProcessUserIdText));
            }
        }
    }

    /// <summary>
    /// Text-backed view of <see cref="TargetProcessUserId"/>. The id is bound as text rather than as an int so
    /// that clearing the box mid-edit leaves it empty instead of throwing a conversion error back at the user.
    /// </summary>
    public string TargetProcessUserIdText
    {
        get => _targetProcessUserId > 0 ? _targetProcessUserId.ToString() : string.Empty;
        set
        {
            var trimmed = value?.Trim();

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                TargetProcessUserId = 0;
                return;
            }

            if (int.TryParse(trimmed, out var parsed))
            {
                TargetProcessUserId = parsed;
            }
        }
    }

    public void ShowOpenWork()
    {
        SetActiveTab(MainContentTab.OpenWork);
    }

    public bool ApplyTargetProcessConnectionString(string? connectionString)
    {
        var trimmed = connectionString?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            StatusMessage = "Enter a TargetProcess connection string first.";
            return false;
        }

        _targetProcessClient.SetConnectionString(trimmed);
        _encryptedTargetProcessConnectionString = ProtectSecret(trimmed);
        SaveSettings();
        StatusMessage = "TargetProcess connection string saved for this machine.";
        return true;
    }

    public bool ClearTargetProcessConnectionString()
    {
        _encryptedTargetProcessConnectionString = null;
        _targetProcessClient.ClearConnectionString();
        SaveSettings();
        StatusMessage = "TargetProcess connection string cleared.";
        return true;
    }

    /// <summary>
    /// Turns a typed name into a TargetProcess user id so nobody has to hunt for their own numeric id.
    /// </summary>
    public async Task ResolveTargetProcessUserAsync(CancellationToken cancellationToken)
    {
        try
        {
            var matches = await _targetProcessClient.FindUsersAsync(TargetProcessUserSearch, cancellationToken);

            if (matches.Count == 0)
            {
                StatusMessage = $"No TargetProcess user matched '{TargetProcessUserSearch}'.";
                return;
            }

            if (matches.Count > 1)
            {
                var names = string.Join(", ", matches.Take(5).Select(match => $"{match.FullName} ({match.Id})"));
                StatusMessage = $"{matches.Count} users matched. Narrow the name or enter the id directly: {names}";
                return;
            }

            TargetProcessUserId = matches[0].Id;
            StatusMessage = $"Resolved {matches[0].FullName} to TargetProcess user {matches[0].Id}.";
        }
        catch (Exception ex)
        {
            AppLog.Error("ResolveTargetProcessUserAsync failed.", ex);
            StatusMessage = ex.Message;
        }
    }

    public async Task EnsureOpenWorkLoadedAsync(CancellationToken cancellationToken)
    {
        if (_hasOpenWorkSnapshot || IsOpenWorkRefreshing)
        {
            return;
        }

        await RefreshOpenWorkAsync(cancellationToken);
    }

    /// <summary>
    /// Loads open pull requests and open TargetProcess tickets together.
    /// <para>
    /// This is deliberately not wired to the auto-refresh timer. Resolving review state costs one API call per
    /// open pull request, so a ten-second cadence over twenty-odd PRs would burn the hourly GitHub rate limit
    /// within the hour. It loads on first visit to the tab and on an explicit refresh, the same way the disk
    /// snapshot does.
    /// </para>
    /// </summary>
    public async Task RefreshOpenWorkAsync(CancellationToken cancellationToken)
    {
        AppLog.Info("RefreshOpenWorkAsync started.");
        EnsureRepositoriesLoaded();

        if (!await _openWorkRefreshGate.WaitAsync(0, cancellationToken))
        {
            AppLog.Info("RefreshOpenWorkAsync skipped because an open-work refresh is already in progress.");
            return;
        }

        try
        {
            IsOpenWorkRefreshing = true;
            SetOpenWorkStatus("Loading open pull requests and TargetProcess tickets...");

            var owners = Repositories
                .Select(repository => repository.Owner)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var pullRequests = await LoadOpenPullRequestsAsync(owners, cancellationToken);
            var tickets = await LoadOpenTicketsAsync(cancellationToken);

            // Ticket links need the portal root; stamp it onto the pull requests so their
            // "Open Ticket" buttons can point at the same place.
            foreach (var pullRequest in pullRequests)
            {
                pullRequest.TargetProcessPortalUrl = _targetProcessPortalUrl;
            }

            LinkPullRequestsToTickets(pullRequests, tickets);

            ReplaceAllItems(OpenPullRequests, pullRequests);
            ReplaceAllItems(OpenTickets, tickets.Where(ticket => !ticket.IsRecurringTracker));
            ReplaceAllItems(RecurringTrackers, tickets.Where(ticket => ticket.IsRecurringTracker));

            _hasOpenWorkSnapshot = true;
            SetOpenWorkStatus($"Last open-work refresh: {DateTimeOffset.Now.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            RaiseOpenWorkProperties();

            StatusMessage = $"Loaded {OpenPullRequests.Count} open PR(s) and {OpenTickets.Count} open ticket(s).";
            AppLog.Info($"RefreshOpenWorkAsync loaded {OpenPullRequests.Count} PRs and {OpenTickets.Count} tickets.");
        }
        catch (OperationCanceledException)
        {
            SetOpenWorkStatus("Open work refresh canceled.");
            RaiseOpenWorkProperties();
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("RefreshOpenWorkAsync failed.", ex);
            SetOpenWorkStatus($"Open work refresh failed: {ex.Message}");
            RaiseOpenWorkProperties();
            StatusMessage = ex.Message;
        }
        finally
        {
            IsOpenWorkRefreshing = false;
            _openWorkRefreshGate.Release();
            AppLog.Info("RefreshOpenWorkAsync finished.");
        }
    }

    /// <summary>
    /// Each source is loaded so that one being unavailable still leaves the other usable: a missing database
    /// route should not blank out the pull requests, and a GitHub outage should not hide the tickets.
    /// </summary>
    private async Task<IReadOnlyList<OpenPullRequestItem>> LoadOpenPullRequestsAsync(
        IReadOnlyList<string> owners,
        CancellationToken cancellationToken)
    {
        try
        {
            var pullRequests = await _gitHubApiClient.GetOpenPullRequestsAsync(owners, cancellationToken);
            var approvedCount = pullRequests.Count(item => item.ReviewState == PullRequestReviewState.Approved);
            SetOpenWorkPullRequestSummary($"{pullRequests.Count} open, {approvedCount} approved");
            return pullRequests;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("LoadOpenPullRequestsAsync failed.", ex);
            SetOpenWorkPullRequestSummary($"Pull requests unavailable: {ex.Message}");
            return [];
        }
    }

    private async Task<IReadOnlyList<TargetProcessTicketItem>> LoadOpenTicketsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tickets = await _targetProcessClient.GetOpenTicketsAsync(TargetProcessUserId, cancellationToken);
            var trackerCount = tickets.Count(ticket => ticket.IsRecurringTracker);
            SetOpenWorkTicketSummary(trackerCount == 0
                ? $"{tickets.Count} open"
                : $"{tickets.Count - trackerCount} open, {trackerCount} recurring");
            return tickets;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("LoadOpenTicketsAsync failed.", ex);
            SetOpenWorkTicketSummary($"Tickets unavailable: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// A ticket can carry more than one open PR when the same story spans repos, so every match is kept.
    /// </summary>
    private static void LinkPullRequestsToTickets(
        IReadOnlyList<OpenPullRequestItem> pullRequests,
        IReadOnlyList<TargetProcessTicketItem> tickets)
    {
        var referencesByTicketId = pullRequests
            .Where(pullRequest => pullRequest.HasTargetProcessId)
            .GroupBy(pullRequest => pullRequest.TargetProcessId!.Value)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group.Select(item => item.Reference).ToList());

        foreach (var ticket in tickets)
        {
            ticket.LinkedPullRequestReferences = referencesByTicketId.TryGetValue(ticket.Id, out var references)
                ? references
                : [];
        }
    }

    private void SetOpenWorkStatus(string statusText)
    {
        if (_openWorkStatusText == statusText)
        {
            return;
        }

        _openWorkStatusText = statusText;
        RaisePropertyChanged(nameof(OpenWorkStatusText));
    }

    private void SetOpenWorkPullRequestSummary(string summary)
    {
        if (_openWorkPullRequestSummary == summary)
        {
            return;
        }

        _openWorkPullRequestSummary = summary;
        RaisePropertyChanged(nameof(OpenWorkPullRequestSummary));
    }

    private void SetOpenWorkTicketSummary(string summary)
    {
        if (_openWorkTicketSummary == summary)
        {
            return;
        }

        _openWorkTicketSummary = summary;
        RaisePropertyChanged(nameof(OpenWorkTicketSummary));
    }

    private void RaiseOpenWorkProperties()
    {
        RaisePropertyChanged(nameof(HasOpenWorkSnapshot));
        RaisePropertyChanged(nameof(HasRecurringTrackers));
        RaisePropertyChanged(nameof(RecurringTrackerSummary));
    }
}