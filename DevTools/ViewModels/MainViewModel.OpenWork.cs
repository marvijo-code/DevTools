using System.Collections.ObjectModel;
using DevTools.Infrastructure;
using DevTools.Models;
using DevTools.Services;

namespace DevTools.ViewModels;

/// <summary>
/// The Open Work tab: the open pull requests this account authored, with review state.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _openWorkRefreshGate = new(1, 1);
    private string _openWorkStatusText = "Open work has not been loaded yet.";
    private string _openWorkPullRequestSummary = "No pull requests loaded.";
    private bool _isOpenWorkRefreshing;
    private bool _hasOpenWorkSnapshot;

    public ObservableCollection<OpenPullRequestItem> OpenPullRequests { get; } = [];

    public bool IsOpenWorkVisible => _activeTab == MainContentTab.OpenWork;

    public bool IsOpenWorkRefreshing
    {
        get => _isOpenWorkRefreshing;
        private set => SetProperty(ref _isOpenWorkRefreshing, value);
    }

    public bool HasOpenWorkSnapshot => _hasOpenWorkSnapshot;

    public string OpenWorkStatusText => _openWorkStatusText;

    public string OpenWorkPullRequestSummary => _openWorkPullRequestSummary;

    public void ShowOpenWork()
    {
        SetActiveTab(MainContentTab.OpenWork);
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
    /// Loads the open pull requests this account authored.
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
            SetOpenWorkStatus("Loading open pull requests...");

            var owners = Repositories
                .Select(repository => repository.Owner)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var pullRequests = await LoadOpenPullRequestsAsync(owners, cancellationToken);

            ReplaceAllItems(OpenPullRequests, pullRequests);

            _hasOpenWorkSnapshot = true;
            SetOpenWorkStatus($"Last open-work refresh: {DateTimeOffset.Now.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            RaiseOpenWorkProperties();

            StatusMessage = $"Loaded {OpenPullRequests.Count} open PR(s).";
            AppLog.Info($"RefreshOpenWorkAsync loaded {OpenPullRequests.Count} PRs.");
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

    private void RaiseOpenWorkProperties()
    {
        RaisePropertyChanged(nameof(HasOpenWorkSnapshot));
    }
}
