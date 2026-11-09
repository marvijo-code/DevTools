using System.Collections.ObjectModel;
using DevTools.Infrastructure;
using DevTools.Models;
using DevTools.Services;

namespace DevTools.ViewModels;

/// <summary>
/// The Worktrees tab: every git worktree under the repository root, what it costs in disk, and whether its
/// work is already merged so it can be removed.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly IReadOnlyList<string> WorktreeFilterChoices =
        ["All", "Safe to remove", "Merged", "Open PR", "Not merged", "Detached", "Folder missing", "Uncommitted files"];

    private const string WorktreeSortLargest = "Space used (largest first)";

    private static readonly IReadOnlyList<string> WorktreeSortChoices =
    [
        WorktreeSortLargest,
        "Space used (smallest first)",
        "Safe to remove first",
        "Repository, then branch"
    ];

    private readonly WorktreeAnalyzer _worktreeAnalyzer;
    private readonly SemaphoreSlim _worktreeRefreshGate = new(1, 1);
    private readonly List<WorktreeItem> _allWorktrees = [];
    private string _worktreeHeadline = "Worktrees have not been scanned yet.";
    private string _worktreeStatusText = "No worktree snapshot loaded.";
    private string _worktreeFilter = "All";
    private string _worktreeSort = WorktreeSortLargest;
    private string _worktreeSearchText = string.Empty;
    private string _selectedWorktreeRepositoryPath = string.Empty;
    private bool _isWorktreeRefreshing;
    private bool _hasWorktreeSnapshot;

    public ObservableCollection<WorktreeItem> Worktrees { get; } = [];

    /// <summary>The repository rail: "All repositories" first, then one entry per repository that has worktrees.</summary>
    public ObservableCollection<WorktreeRepositoryFilter> WorktreeRepositories { get; } = [];

    public bool IsWorktreesVisible => _activeTab == MainContentTab.Worktrees;

    public IReadOnlyList<string> WorktreeFilterOptions => WorktreeFilterChoices;

    public IReadOnlyList<string> WorktreeSortOptions => WorktreeSortChoices;

    public string WorktreeHeadline => _worktreeHeadline;

    public string WorktreeStatusText => _worktreeStatusText;

    public bool HasWorktreeSnapshot => _hasWorktreeSnapshot;

    public bool IsWorktreeRefreshing
    {
        get => _isWorktreeRefreshing;
        private set => SetProperty(ref _isWorktreeRefreshing, value);
    }

    public string WorktreeFilter
    {
        get => _worktreeFilter;
        set
        {
            if (SetProperty(ref _worktreeFilter, WorktreeFilterChoices.Contains(value) ? value : "All"))
            {
                ApplyWorktreeFilter();
            }
        }
    }

    public string WorktreeSort
    {
        get => _worktreeSort;
        set
        {
            if (SetProperty(ref _worktreeSort, WorktreeSortChoices.Contains(value) ? value : WorktreeSortLargest))
            {
                ApplyWorktreeFilter();
            }
        }
    }

    public string WorktreeSearchText
    {
        get => _worktreeSearchText;
        set
        {
            if (SetProperty(ref _worktreeSearchText, value ?? string.Empty))
            {
                ApplyWorktreeFilter();
            }
        }
    }

    public string VisibleWorktreesText =>
        _allWorktrees.Count == Worktrees.Count
            ? $"{Worktrees.Count} worktrees"
            : $"{Worktrees.Count} of {_allWorktrees.Count} worktrees";

    public void ShowWorktrees()
    {
        SetActiveTab(MainContentTab.Worktrees);
    }

    public async Task EnsureWorktreesLoadedAsync(CancellationToken cancellationToken)
    {
        if (_hasWorktreeSnapshot || IsWorktreeRefreshing)
        {
            return;
        }

        await RefreshWorktreesAsync(cancellationToken);
    }

    /// <summary>
    /// Rescans every worktree. Like the disk snapshot this only runs on first visit and on an explicit refresh:
    /// it fetches each repository, measures each folder, and spends one GitHub call per branch.
    /// </summary>
    public async Task RefreshWorktreesAsync(CancellationToken cancellationToken)
    {
        AppLog.Info("RefreshWorktreesAsync started.");

        if (!await _worktreeRefreshGate.WaitAsync(0, cancellationToken))
        {
            AppLog.Info("RefreshWorktreesAsync skipped because a worktree refresh is already in progress.");
            return;
        }

        try
        {
            IsWorktreeRefreshing = true;
            SetWorktreeStatus("Looking for worktrees...");
            StatusMessage = "Refreshing worktrees...";

            var isFirstReport = true;
            var progress = new Progress<WorktreeProgress>(update =>
            {
                if (isFirstReport)
                {
                    isFirstReport = false;
                    SetWorktrees(update.Items);
                }

                SetWorktreeStatus(update.StatusText);
                UpdateWorktreeHeadline();
            });

            var snapshot = await _worktreeAnalyzer.CaptureAsync(progress, cancellationToken);
            SetWorktrees(snapshot.Items);
            _hasWorktreeSnapshot = true;
            RaisePropertyChanged(nameof(HasWorktreeSnapshot));
            SetWorktreeStatus($"Last worktree refresh: {snapshot.CapturedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}. Scanned {snapshot.RepositoryCount} repositories under {snapshot.RootPath}.{snapshot.Note}");
            UpdateWorktreeHeadline();
            SyncWorktreeRepositories(reorder: true);
            StatusMessage = $"Loaded {_allWorktrees.Count} worktrees.";
            AppLog.Info($"RefreshWorktreesAsync loaded {_allWorktrees.Count} worktrees.");
        }
        catch (OperationCanceledException)
        {
            SetWorktreeStatus("Worktree refresh canceled.");
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("RefreshWorktreesAsync failed.", ex);
            SetWorktreeStatus($"Worktree refresh failed: {ex.Message}");
            StatusMessage = ex.Message;
        }
        finally
        {
            IsWorktreeRefreshing = false;
            _worktreeRefreshGate.Release();
            AppLog.Info("RefreshWorktreesAsync finished.");
        }
    }

    /// <summary>
    /// Removes a single worktree and drops it from the list on success. A failure leaves the row in place with
    /// git's own reason in the status line, so nothing disappears from view that is still on disk.
    /// </summary>
    public async Task<bool> RemoveWorktreeAsync(WorktreeItem item, CancellationToken cancellationToken)
    {
        if (!item.CanRemove)
        {
            return false;
        }

        item.SetRemoving(true);
        try
        {
            var (success, message) = await _worktreeAnalyzer.RemoveAsync(item, item.IsDirty, cancellationToken);
            StatusMessage = message;
            SetWorktreeStatus(message);

            if (success)
            {
                _allWorktrees.Remove(item);
                Worktrees.Remove(item);
                UpdateWorktreeHeadline();
                RaisePropertyChanged(nameof(VisibleWorktreesText));
            }

            return success;
        }
        catch (Exception ex)
        {
            AppLog.Error($"RemoveWorktreeAsync failed for {item.Path}.", ex);
            StatusMessage = ex.Message;
            SetWorktreeStatus(ex.Message);
            return false;
        }
        finally
        {
            item.SetRemoving(false);
        }
    }

    /// <summary>Clears git's leftover records for worktrees whose folders are already gone.</summary>
    public async Task PruneStaleWorktreesAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A lock on a folder that no longer exists protects nothing, and git will not prune past it, so the
            // listed missing rows go through the per-row path, which unlocks first.
            var listedMissing = _allWorktrees.Where(item => item.IsMissing && item.CanRemove).ToList();
            var cleared = 0;
            foreach (var missing in listedMissing)
            {
                var (success, _) = await _worktreeAnalyzer.RemoveAsync(missing, false, cancellationToken);
                if (success)
                {
                    cleared++;
                    _allWorktrees.Remove(missing);
                    Worktrees.Remove(missing);
                }
            }

            var pruned = cleared + await _worktreeAnalyzer.PruneAllAsync(cancellationToken);
            var message = pruned == 0 ? "No stale worktree records to prune." : $"Pruned {pruned} stale worktree record(s).";
            StatusMessage = message;
            SetWorktreeStatus(message);
            UpdateWorktreeHeadline();
            RaisePropertyChanged(nameof(VisibleWorktreesText));
        }
        catch (Exception ex)
        {
            AppLog.Error("PruneStaleWorktreesAsync failed.", ex);
            StatusMessage = ex.Message;
            SetWorktreeStatus(ex.Message);
        }
    }

    /// <summary>Narrows the list to one repository, or back to every repository for the "All" entry.</summary>
    public void SelectWorktreeRepository(WorktreeRepositoryFilter? repository)
    {
        _selectedWorktreeRepositoryPath = repository?.RepositoryPath ?? string.Empty;
        foreach (var entry in WorktreeRepositories)
        {
            entry.IsSelected = string.Equals(entry.RepositoryPath, _selectedWorktreeRepositoryPath, StringComparison.OrdinalIgnoreCase);
        }

        ApplyWorktreeFilter();
    }

    private void SetWorktrees(IReadOnlyList<WorktreeItem> items)
    {
        _allWorktrees.Clear();
        _allWorktrees.AddRange(items);
        SyncWorktreeRepositories(reorder: false);
        ApplyWorktreeFilter();
    }

    /// <summary>
    /// Keeps the repository rail in step with the worktree list. Entries are updated in place so the rail does
    /// not flicker while a scan streams in, and are only re-sorted (largest first) once a scan has finished.
    /// </summary>
    private void SyncWorktreeRepositories(bool reorder)
    {
        var all = WorktreeRepositories.FirstOrDefault(entry => entry.IsAllRepositories);
        if (all is null)
        {
            all = new WorktreeRepositoryFilter("All repositories", string.Empty) { IsSelected = _selectedWorktreeRepositoryPath.Length == 0 };
            WorktreeRepositories.Insert(0, all);
        }

        all.Update(_allWorktrees.Count, _allWorktrees.Sum(item => item.SizeBytes ?? 0), _allWorktrees.Count(item => item.IsSafeToRemove));

        var groups = _allWorktrees
            .GroupBy(item => item.RepositoryPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (repositoryPath, items) in groups)
        {
            var entry = WorktreeRepositories.FirstOrDefault(candidate => string.Equals(candidate.RepositoryPath, repositoryPath, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = new WorktreeRepositoryFilter(items[0].RepositoryName, repositoryPath)
                {
                    IsSelected = string.Equals(repositoryPath, _selectedWorktreeRepositoryPath, StringComparison.OrdinalIgnoreCase)
                };
                WorktreeRepositories.Add(entry);
            }

            entry.Update(items.Count, items.Sum(item => item.SizeBytes ?? 0), items.Count(item => item.IsSafeToRemove));
        }

        for (var i = WorktreeRepositories.Count - 1; i >= 0; i--)
        {
            var entry = WorktreeRepositories[i];
            if (entry.IsAllRepositories || groups.ContainsKey(entry.RepositoryPath))
            {
                continue;
            }

            WorktreeRepositories.RemoveAt(i);
            if (entry.IsSelected)
            {
                // Its last worktree is gone, so fall back to showing everything rather than an empty list.
                SelectWorktreeRepository(null);
            }
        }

        if (!reorder)
        {
            return;
        }

        var desired = WorktreeRepositories
            .OrderBy(entry => entry.IsAllRepositories ? 0 : 1)
            .ThenByDescending(entry => entry.SizeBytes)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < desired.Count; i++)
        {
            var currentIndex = WorktreeRepositories.IndexOf(desired[i]);
            if (currentIndex != i)
            {
                WorktreeRepositories.Move(currentIndex, i);
            }
        }
    }

    private void ApplyWorktreeFilter()
    {
        var terms = TokenizeFilterTerms(WorktreeSearchText);
        var matching = _allWorktrees
            .Where(MatchesWorktreeFilter)
            .Where(item => terms.Count == 0 || terms.All(term =>
                item.Path.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.Branch.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.RepositoryName.Contains(term, StringComparison.OrdinalIgnoreCase)));

        var visible = SortWorktrees(matching).ToList();

        if (!Worktrees.SequenceEqual(visible))
        {
            ReplaceAllItems(Worktrees, visible);
        }

        RaisePropertyChanged(nameof(VisibleWorktreesText));
    }

    /// <summary>
    /// Orders worktrees by the chosen sort. A folder still being measured has no size yet, so it sits last
    /// in both size orders rather than jumping to the top while the scan runs.
    /// </summary>
    private IOrderedEnumerable<WorktreeItem> SortWorktrees(IEnumerable<WorktreeItem> items)
    {
        return WorktreeSort switch
        {
            "Space used (smallest first)" => items
                .OrderBy(item => item.SizeBytes ?? long.MaxValue)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            "Safe to remove first" => items
                .OrderBy(item => item.SortRank)
                .ThenByDescending(item => item.SizeBytes ?? -1)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            "Repository, then branch" => items
                .OrderBy(item => item.RepositoryName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Branch, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase),
            _ => items
                .OrderByDescending(item => item.SizeBytes ?? -1)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
        };
    }

    private bool MatchesWorktreeFilter(WorktreeItem item)
    {
        if (_selectedWorktreeRepositoryPath.Length > 0 &&
            !string.Equals(item.RepositoryPath, _selectedWorktreeRepositoryPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return WorktreeFilter switch
        {
            "Safe to remove" => item.IsSafeToRemove,
            "Merged" => item.State == WorktreeState.Merged && !item.IsAnalyzing,
            "Open PR" => item.State == WorktreeState.OpenPullRequest,
            "Not merged" => item.State is WorktreeState.Unmerged or WorktreeState.MergedPullRequestLocalDiffers or WorktreeState.ClosedPullRequest,
            "Detached" => item.IsDetached,
            "Folder missing" => item.IsMissing,
            "Uncommitted files" => item.IsDirty,
            _ => true
        };
    }

    private void UpdateWorktreeHeadline()
    {
        var totalBytes = _allWorktrees.Sum(item => item.SizeBytes ?? 0);
        var safe = _allWorktrees.Where(item => item.IsSafeToRemove).ToList();
        var repositoryCount = _allWorktrees.Select(item => item.RepositoryPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var headline = _allWorktrees.Count == 0
            ? "No worktrees found."
            : $"{_allWorktrees.Count} worktrees across {repositoryCount} repositories use {WorktreeItem.FormatBytes(totalBytes)}. " +
              $"{safe.Count} are safe to remove ({WorktreeItem.FormatBytes(safe.Sum(item => item.SizeBytes ?? 0))}).";

        if (_worktreeHeadline != headline)
        {
            _worktreeHeadline = headline;
            RaisePropertyChanged(nameof(WorktreeHeadline));
        }

        SyncWorktreeRepositories(reorder: false);
    }

    private void SetWorktreeStatus(string statusText)
    {
        if (_worktreeStatusText == statusText)
        {
            return;
        }

        _worktreeStatusText = statusText;
        RaisePropertyChanged(nameof(WorktreeStatusText));
    }

    private static void ReplaceAllItems<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}