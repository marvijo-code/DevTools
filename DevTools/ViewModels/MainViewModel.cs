using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DevTools.Infrastructure;
using DevTools.Models;
using DevTools.Services;
using DevTools.Services.BiggestFiles;
using DevTools.Services.DiskCleanup;

namespace DevTools.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DevTools");
    private static readonly string RepositoriesFilePath = Path.Combine(SettingsDirectory, "repositories.txt");
    private static readonly string SettingsFilePath = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly IReadOnlyList<string> DiskUsageAnimationFrames = ["Measuring", "Measuring.", "Measuring..", "Measuring..."];
    private static readonly IReadOnlyList<int> RefreshIntervalChoices = [5, 10, 15, 30, 60];
    private static readonly IReadOnlyList<int> BiggestFilesTopCountChoices = [50, 100, 250, 500];
    private static readonly IReadOnlyList<string> StatusChoices = ["All", "Success", "In Progress", "Failed", "Cancelled", "Other"];
    private static readonly HashSet<string> FilterSensitiveRunProperties =
    [
        nameof(WorkflowRunItem.ActualBranch),
        nameof(WorkflowRunItem.ActualSha),
        nameof(WorkflowRunItem.PullRequestUrl),
        nameof(WorkflowRunItem.PullRequestTitle),
        nameof(WorkflowRunItem.PullRequestDescriptionRaw),
        nameof(WorkflowRunItem.CommitDescriptionRaw),
        nameof(WorkflowRunItem.CommitAuthorDisplay),
        nameof(WorkflowRunItem.PullRequestAuthor),
        nameof(WorkflowRunItem.UpstreamRunUrl),
        nameof(WorkflowRunItem.DetectJobUrl),
        nameof(WorkflowRunItem.SetBranchJobUrl),
        nameof(WorkflowRunItem.ResolutionError),
        nameof(WorkflowRunItem.IsResolving),
        nameof(WorkflowRunItem.RunDescriptionRaw),
        nameof(WorkflowRunItem.PullRequestSummary),
        nameof(WorkflowRunItem.DisplayAuthor),
        nameof(WorkflowRunItem.WorkflowMetaSummary),
        nameof(WorkflowRunItem.StatusCategory)
    ];
    private readonly DiskUsageAnalyzer _diskUsageAnalyzer = new();
    private int _refreshFailureCount;
    private DateTimeOffset _nextTimedRefreshAt = DateTimeOffset.MinValue;
    private readonly ProcessKillCatalog _processKillCatalog = new();
    private readonly ProcessKiller _processKiller = new();
    private readonly GitHubApiClient _gitHubApiClient = new();
    private readonly SemaphoreSlim _diskUsageRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _killProcessesRefreshGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Dictionary<string, bool> _diskUsageSectionExpansionStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _diskUsageExpansionInProgress = new(StringComparer.OrdinalIgnoreCase);
    private ProcessViewState _processViewState = new();
    private Dictionary<string, bool> _killProcessSectionExpansionStates => _processViewState.ExpandedSections;
    private readonly List<WorkflowRunItem> _allRuns = [];
    private readonly ObservableCollection<DiskUsageSection> _diskUsageSections = [];
    private readonly ObservableCollection<ProcessKillSection> _killProcessSections = [];
    private readonly BiggestFilesEngine _biggestFilesEngine = new();
    private readonly SemaphoreSlim _biggestFilesGate = new(1, 1);
    private readonly ObservableCollection<BiggestFileEntry> _biggestFiles = [];
    private readonly List<BiggestFileEntry> _allBiggestFiles = [];
    private readonly DiskCleanupService _diskCleanupService = new();
    private readonly SemaphoreSlim _bulkCleanupGate = new(1, 1);
    private CancellationTokenSource? _bulkCleanupCts;

    private string _ownerInput = string.Empty;
    private string _repoInput = string.Empty;
    private string _runFilterText = string.Empty;
    private string _statusMessage = "Ready.";
    private string _authMessage = "Checking GitHub auth...";
    private string _diskUsageHeadline = "Disk usage has not been refreshed yet.";
    private string _diskUsageAnalysis = "Refresh the space analyzer to capture the current machine footprint.";
    private string _diskUsageStatusText = "No disk snapshot loaded.";
    private string _diskUsageActivityText = "Idle";
    private readonly List<ProcessKillSection> _allKillProcessSections = [];
    private string _processFilterText = string.Empty;
    private string _killProcessesHeadline = "Process list has not been refreshed yet.";
    private string _killProcessesSummary = "Refresh to load running processes with descriptive names.";
    private string _killProcessesStatusText = "No process snapshot loaded.";
    private string _lastRunsHash = string.Empty;
    private DateTimeOffset? _lastRefreshAt;
    private WorkflowRunItem? _selectedRun;
    private ProcessKillEntry? _selectedProcess;
    private bool _isRefreshing;
    private bool _isDiskUsageRefreshing;
    private bool _isKillProcessesRefreshing;
    private bool _hasDiskUsageSnapshot;
    private bool _hasKillProcessSnapshot;
    private bool _repositoriesLoaded;
    private bool _autoRefreshEnabled = true;
    private int _refreshIntervalSeconds = 10;
    private MainContentTab _activeTab = MainContentTab.KillProcesses;
    private CancellationTokenSource? _biggestFilesCts;
    private string _biggestFilesFilterText = string.Empty;
    private string _biggestFilesScanRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
    private int _biggestFilesTopCount = 100;
    private bool _isBiggestFilesScanning;
    private bool _hasBiggestFilesSnapshot;
    private string _biggestFilesHeadline = "Scan a drive or folder to find the largest files.";
    private string _biggestFilesStatusText = "No scan yet.";
    private bool _runAsAdministrator = true;
    private bool _isBulkCleanupRunning;
    private string _bulkCleanupStatusText = "Not run yet.";
    private string _bulkCleanupHeadline = "One button cleanup: package caches, stale build output, the intermediates of already-published video episodes, and crashed-git debris with expired agent checkpoints.";
    private string _bulkCleanupReportText = string.Empty;
    private bool _isLoadingSettings;
    private string _statusFilter = "All";
    private string? _encryptedPersonalAccessToken;
    private int _diskUsageAnimationFrame;

    public MainViewModel()
    {
        _worktreeAnalyzer = new WorktreeAnalyzer(_gitHubApiClient, _diskUsageAnalyzer);
        _cleanupAdvisor = new CleanupAdvisor(_diskUsageAnalyzer);
        Repositories = [];
        Runs = [];
        LoadSettings();
    }

    public ObservableCollection<RepositoryDefinition> Repositories { get; }

    public ObservableCollection<WorkflowRunItem> Runs { get; }

    public ObservableCollection<DiskUsageSection> DiskUsageSections => _diskUsageSections;

    public ObservableCollection<ProcessKillSection> KillProcessSections => _killProcessSections;

    public IReadOnlyList<int> RefreshIntervalOptions => RefreshIntervalChoices;

    public IReadOnlyList<string> StatusFilterOptions => StatusChoices;

    public string DiskUsageHeadline => _diskUsageHeadline;

    public string DiskUsageAnalysis => _diskUsageAnalysis;

    public string DiskUsageStatusText => _diskUsageStatusText;

    public bool IsDiskUsageRefreshing => _isDiskUsageRefreshing;

    public string DiskUsageActivityText => _diskUsageActivityText;

    public string KillProcessesHeadline => _killProcessesHeadline;

    public string KillProcessesSummary => _killProcessesSummary;

    public string KillProcessesStatusText => _killProcessesStatusText;

    public string ProcessFilterText
    {
        get => _processFilterText;
        set
        {
            if (SetProperty(ref _processFilterText, value))
            {
                ApplyProcessFilter();
                SaveSettings();
            }
        }
    }

    public string VisibleProcessesText
    {
        get
        {
            var visibleCount = _killProcessSections.Sum(section => section.Entries.Count);
            var totalCount = _allKillProcessSections.Sum(section => section.Entries.Count);
            return visibleCount == totalCount
                ? $"{visibleCount} processes"
                : $"{visibleCount} of {totalCount} processes";
        }
    }

    public bool IsKillProcessesRefreshing => _isKillProcessesRefreshing;

    public bool IsKillProcessesVisible => _activeTab == MainContentTab.KillProcesses;

    public bool IsWorkflowExplorerVisible => _activeTab == MainContentTab.WorkflowExplorer;

    public bool IsDiskUsageVisible => _activeTab == MainContentTab.DiskUsage;

    public bool IsResourcesVisible => _activeTab == MainContentTab.Resources;

    public MainContentTab ActiveTab => _activeTab;

    public ObservableCollection<BiggestFileEntry> BiggestFiles => _biggestFiles;

    public IReadOnlyList<int> BiggestFilesTopCountOptions => BiggestFilesTopCountChoices;

    public string BiggestFilesHeadline => _biggestFilesHeadline;

    public string BiggestFilesStatusText => _biggestFilesStatusText;

    public bool IsBiggestFilesScanning => _isBiggestFilesScanning;

    public bool HasBiggestFilesSnapshot => _hasBiggestFilesSnapshot;

    public bool ShowElevationHint => !ElevationService.IsProcessElevated();

    /// <summary>
    /// Start DevTools with administrator rights. DevTools is elevated by default because the fast
    /// NTFS Master File Table folder scan needs the raw-volume handle. The launcher reads this from
    /// settings.json, so the choice applies to the next launch.
    /// </summary>
    public bool RunAsAdministrator
    {
        get => _runAsAdministrator;
        set
        {
            if (_runAsAdministrator == value)
            {
                return;
            }

            _runAsAdministrator = value;
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(ElevationPreferenceText));
            SaveSettings();
            StatusMessage = value
                ? "DevTools will start as administrator from the next launch."
                : "DevTools will start without administrator rights from the next launch. Folder scans will be slower.";
        }
    }

    public bool IsRunningAsAdministrator => ElevationService.IsProcessElevated();

    public string ElevationPreferenceText => _runAsAdministrator
        ? "Always start with administrator rights. Folder scans use the fast NTFS engine."
        : "Start without administrator rights. Folder scans fall back to the slower engine.";

    public bool IsBulkCleanupRunning => _isBulkCleanupRunning;

    public string BulkCleanupStatusText => _bulkCleanupStatusText;

    public string BulkCleanupHeadline => _bulkCleanupHeadline;

    public string BulkCleanupReportText => _bulkCleanupReportText;

    public bool HasBulkCleanupReport => !string.IsNullOrWhiteSpace(_bulkCleanupReportText);

    /// <summary>True when pwsh 7 and the cleanup script are both on this machine.</summary>
    public bool IsBulkCleanupAvailable => _diskCleanupService.IsAvailable;

    public string BulkCleanupAvailabilityText =>
        _diskCleanupService.GetUnavailableReason() ??
        $"Runs {_diskCleanupService.ScriptPath} with the Caches, BuildArtifacts and PublishedVideos lanes.";

    public string BulkCleanupButtonText => IsBulkCleanupRunning ? "Cleaning up..." : "Bulk Cleanup PC";

    public string VisibleBiggestFilesText =>
        _allBiggestFiles.Count == _biggestFiles.Count
            ? $"{_biggestFiles.Count} file(s)"
            : $"{_biggestFiles.Count} of {_allBiggestFiles.Count} file(s)";

    public string BiggestFilesScanRoot
    {
        get => _biggestFilesScanRoot;
        set
        {
            if (SetProperty(ref _biggestFilesScanRoot, value))
            {
                SaveSettings();
            }
        }
    }

    public int BiggestFilesTopCount
    {
        get => _biggestFilesTopCount;
        set
        {
            if (SetProperty(ref _biggestFilesTopCount, NormalizeTopCount(value)))
            {
                SaveSettings();
            }
        }
    }

    public string BiggestFilesFilterText
    {
        get => _biggestFilesFilterText;
        set
        {
            if (SetProperty(ref _biggestFilesFilterText, value))
            {
                ApplyBiggestFilesFilter();
            }
        }
    }

    public string OwnerInput
    {
        get => _ownerInput;
        set => SetProperty(ref _ownerInput, value);
    }

    public string RepoInput
    {
        get => _repoInput;
        set => SetProperty(ref _repoInput, value);
    }

    public string RunFilterText
    {
        get => _runFilterText;
        set
        {
            if (SetProperty(ref _runFilterText, value))
            {
                ApplyRunFilter();
            }
        }
    }

    public bool AutoRefreshEnabled
    {
        get => _autoRefreshEnabled;
        set
        {
            if (SetProperty(ref _autoRefreshEnabled, value))
            {
                SaveSettings();
                RaisePropertyChanged(nameof(RefreshIntervalSummary));
            }
        }
    }

    public int RefreshIntervalSeconds
    {
        get => _refreshIntervalSeconds;
        set
        {
            if (SetProperty(ref _refreshIntervalSeconds, NormalizeRefreshInterval(value)))
            {
                SaveSettings();
                RaisePropertyChanged(nameof(RefreshIntervalSummary));
            }
        }
    }

    public string StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (SetProperty(ref _statusFilter, StatusChoices.Contains(value) ? value : "All"))
            {
                ApplyRunFilter();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                AppLog.Info($"StatusMessage={value}");
            }
        }
    }

    public string AuthMessage
    {
        get => _authMessage;
        set
        {
            if (SetProperty(ref _authMessage, value))
            {
                AppLog.Info($"AuthMessage={value}");
            }
        }
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    public bool HasDiskUsageSnapshot => _hasDiskUsageSnapshot;

    public string LastRefreshText =>
        _lastRefreshAt is null
            ? "Never refreshed"
            : $"Last refresh: {_lastRefreshAt.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss}";

    public string VisibleRunsText =>
        _allRuns.Count == Runs.Count
            ? $"{Runs.Count} runs"
            : $"{Runs.Count} of {_allRuns.Count} runs";

    public string RepositorySummaryText => $"{Repositories.Count} repo(s)";

    public string RefreshIntervalSummary =>
        AutoRefreshEnabled
            ? $"Auto refresh every {RefreshIntervalSeconds}s"
            : "Auto refresh paused";

    public WorkflowRunItem? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (SetProperty(ref _selectedRun, value))
            {
                RaisePropertyChanged(nameof(ContextPaneSubtitle));
                RaisePropertyChanged(nameof(HasSelectedRun));
            }
        }
    }

    public bool HasSelectedRun => SelectedRun is not null;
    private AppActivityEntry? _selectedActivity;
    public AppActivityEntry? SelectedActivity
    {
        get => _selectedActivity;
        set
        {
            if (ReferenceEquals(_selectedActivity, value)) return;
            if (_selectedActivity is not null) _selectedActivity.IsSelected = false;
            if (value is not null) { SelectedProcess = null; value.IsSelected = true; }
            _selectedActivity = value;
            RaisePropertyChanged(nameof(SelectedActivity));
            RaisePropertyChanged(nameof(HasSelectedActivity));
            RaisePropertyChanged(nameof(ShowProcessOverview));
            RaisePropertyChanged(nameof(ContextPaneTitle));
            RaisePropertyChanged(nameof(ContextPaneSubtitle));
        }
    }
    public bool HasSelectedActivity => SelectedActivity is not null;
    public bool ShowProcessOverview => SelectedProcess is null && SelectedActivity is null;
    public IEnumerable<ProcessKillEntry> LargestProcesses => _allKillProcessSections.SelectMany(section => section.Entries)
        .OrderByDescending(entry => entry.WorkingSetBytes).Take(5);
    public string ProcessOverviewSummary => $"{_allKillProcessSections.Sum(section => section.Entries.Count)} processes · " +
        $"{ByteFormatter.Format(_allKillProcessSections.SelectMany(section => section.Entries).Sum(entry => entry.WorkingSetBytes))} combined working set";
    public bool HasSelectedProcess => SelectedProcess is not null;
    public ProcessKillEntry? SelectedProcess
    {
        get => _selectedProcess;
        set
        {
            if (ReferenceEquals(_selectedProcess, value)) return;
            if (_selectedProcess is not null) _selectedProcess.IsSelected = false;
            if (value is not null) { SelectedActivity = null; value.IsSelected = true; }
            if (SetProperty(ref _selectedProcess, value))
            {
                RaisePropertyChanged(nameof(HasSelectedProcess));
                RaisePropertyChanged(nameof(ShowProcessOverview));
                RaisePropertyChanged(nameof(ContextPaneTitle));
                RaisePropertyChanged(nameof(ContextPaneSubtitle));
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        AppLog.Info("InitializeAsync started.");
        EnsureRepositoriesLoaded();

        AuthMessage = await _gitHubApiClient.GetAuthSummaryAsync(cancellationToken);
        await RefreshAsync(cancellationToken);
        AppLog.Info("InitializeAsync completed.");
    }

    public async Task ReloadAuthAsync(CancellationToken cancellationToken)
    {
        AppLog.Info("ReloadAuthAsync started.");
        EnsureRepositoriesLoaded();
        AuthMessage = "Checking GitHub auth...";
        AuthMessage = await _gitHubApiClient.ReloadAuthAsync(cancellationToken);
        AppLog.Info("ReloadAuthAsync completed.");
    }

    public bool LaunchGhLogin()
    {
        AppLog.Info("LaunchGhLogin requested.");
        var launched = _gitHubApiClient.TryLaunchGhLogin();
        StatusMessage = launched
            ? "Opened gh auth login in a terminal window. Complete the browser sign-in, then click Reload Auth."
            : "GitHub CLI was not found. Install GitHub CLI first.";
        return launched;
    }

    public bool ApplyPersonalAccessToken(string? personalAccessToken)
    {
        var trimmed = personalAccessToken?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            StatusMessage = "Enter a personal access token first.";
            return false;
        }

        _gitHubApiClient.SetPersonalAccessToken(trimmed);
        _encryptedPersonalAccessToken = ProtectSecret(trimmed);
        SaveSettings();
        AuthMessage = "Using manual PAT (default).";
        StatusMessage = "Manual PAT saved and set as the default auth for this app.";
        return true;
    }

    public bool ClearPersonalAccessToken()
    {
        _encryptedPersonalAccessToken = null;
        _gitHubApiClient.ClearPersonalAccessToken();
        SaveSettings();
        AuthMessage = "No token configured. Paste a PAT and click Use PAT, or click Use gh Token for a temporary session token.";
        StatusMessage = "Manual PAT cleared.";
        return true;
    }

    public async Task UseGhTokenAsync(CancellationToken cancellationToken)
    {
        AuthMessage = await _gitHubApiClient.UseGhTokenAsync(cancellationToken);
        StatusMessage = "Using gh token for this session. Manual PAT remains preferred when set.";
    }

    public async Task EnsureDiskUsageLoadedAsync(CancellationToken cancellationToken)
    {
        if (_hasDiskUsageSnapshot || IsDiskUsageRefreshing)
        {
            return;
        }

        await RefreshDiskUsageAsync(cancellationToken);
    }

    public async Task RefreshDiskUsageAsync(CancellationToken cancellationToken)
    {
        AppLog.Info("RefreshDiskUsageAsync started.");

        if (!await _diskUsageRefreshGate.WaitAsync(0, cancellationToken))
        {
            AppLog.Info("RefreshDiskUsageAsync skipped because a disk refresh is already in progress.");
            return;
        }

        try
        {
            SetDiskUsageRefreshing(true);
            _hasDiskUsageSnapshot = false;
            SetDiskUsageSections([]);
            _diskUsageAnalysis = "Scanning disk usage and streaming results as sections complete.";
            SetDiskUsageStatus("Starting disk usage refresh...");
            SetDiskUsageActivityText(DiskUsageAnimationFrames[0]);
            RaiseDiskUsageProperties();
            StatusMessage = "Refreshing disk usage snapshot...";

            // The cleanup options are quick to check, so they fill in while the slower full scan runs.
            var cleanupTask = RefreshCleanupOptionsAsync(cancellationToken);

            // Show the drives and open the system drive at once; sizing it on demand beats waiting for the scan.
            RefreshFolderBrowserDrives();

            var progress = new Progress<DiskUsageProgress>(update =>
            {
                _diskUsageHeadline = update.Headline;
                _diskUsageAnalysis = update.Analysis;
                SetDiskUsageSections(update.Sections);
                _hasDiskUsageSnapshot = update.Sections.Count > 0 || update.IsCompleted;
                SetDiskUsageStatus(update.StatusText);
                RaiseDiskUsageProperties();
                if (_diskUsageAnalyzer.Trees.Count != _folderBrowserTreeCount)
                {
                    RefreshFolderBrowserDrives();
                }
            });

            var snapshot = await _diskUsageAnalyzer.CaptureAsync(progress, cancellationToken);
            SetDiskUsageSections(snapshot.Sections);
            _diskUsageHeadline = snapshot.Headline;
            _diskUsageAnalysis = snapshot.Analysis;
            _hasDiskUsageSnapshot = true;
            SetDiskUsageStatus($"Last disk refresh: {snapshot.CapturedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            SetDiskUsageActivityText("Snapshot ready");
            RaiseDiskUsageProperties();
            RefreshFolderBrowserDrives();
            await cleanupTask;

            StatusMessage = "Disk usage snapshot refreshed.";
            AppLog.Info("RefreshDiskUsageAsync completed.");
        }
        catch (OperationCanceledException)
        {
            SetDiskUsageStatus("Disk usage refresh canceled.");
            SetDiskUsageActivityText("Canceled");
            StatusMessage = "Disk usage refresh canceled.";
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("RefreshDiskUsageAsync failed.", ex);
            SetDiskUsageStatus($"Disk usage refresh failed: {ex.Message}");
            SetDiskUsageActivityText("Scan failed");
            StatusMessage = ex.Message;
        }
        finally
        {
            SetDiskUsageRefreshing(false);
            _diskUsageRefreshGate.Release();
            AppLog.Info("RefreshDiskUsageAsync finished.");
        }
    }

    /// <summary>
    /// Loads the direct children of a folder entry the first time the user expands it with "+".
    /// </summary>
    public async Task EnsureDiskUsageChildrenLoadedAsync(DiskUsageEntry entry, CancellationToken cancellationToken)
    {
        if (!entry.CanExpand || entry.HasLoadedChildren || entry.IsMeasuring)
        {
            return;
        }

        if (!_diskUsageExpansionInProgress.Add(entry.Path))
        {
            return;
        }

        try
        {
            var children = await _diskUsageAnalyzer.ExpandEntryAsync(entry, cancellationToken);
            entry.SetChildren(children);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            AppLog.Error($"Expanding disk usage folder '{entry.Path}' failed.", ex);
            entry.SetChildren([]);
        }
        finally
        {
            _diskUsageExpansionInProgress.Remove(entry.Path);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        AppLog.Info("RefreshAsync started.");
        EnsureRepositoriesLoaded();

        if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            AppLog.Info("RefreshAsync skipped because a refresh is already in progress.");
            return;
        }

        try
        {
            IsRefreshing = true;
            StatusMessage = "Refreshing workflow runs...";

            var fetchTasks = Repositories
                .Select(repository => _gitHubApiClient.GetWorkflowRunsAsync(repository, 25, cancellationToken))
                .ToList();
            AppLog.Info($"Refreshing {Repositories.Count} repositories.");

            var results = await Task.WhenAll(fetchTasks);
            _refreshFailureCount = 0;
            _nextTimedRefreshAt = DateTimeOffset.MinValue;
            var combined = results
                .SelectMany(item => item)
                .OrderByDescending(item => item.CreatedAt)
                .ToList();
            var currentHash = ComputeRunsHash(combined);

            _lastRefreshAt = DateTimeOffset.Now;
            RaisePropertyChanged(nameof(LastRefreshText));

            if (string.Equals(_lastRunsHash, currentHash, StringComparison.Ordinal))
            {
                StatusMessage = $"No workflow changes across {Repositories.Count} repo(s).";
                AppLog.Info("RefreshAsync skipped UI update because workflow data hash is unchanged.");
                return;
            }

            _lastRunsHash = currentHash;

            foreach (var existingRun in _allRuns)
            {
                existingRun.PropertyChanged -= WorkflowRunItem_PropertyChanged;
            }

            _allRuns.Clear();
            _allRuns.AddRange(combined);

            foreach (var run in _allRuns)
            {
                run.PropertyChanged += WorkflowRunItem_PropertyChanged;
            }

            ApplyRunFilter();

            if (SelectedRun is not null)
            {
                await ResolveSelectedRunAsync(cancellationToken);
            }

            StatusMessage = $"Loaded {Runs.Count} runs across {Repositories.Count} repo(s).";
            AppLog.Info($"RefreshAsync loaded {Runs.Count} runs.");
        }
        catch (Exception ex)
        {
            // Back off instead of failing the same call (and logging a stack trace) every
            // 10 seconds: 20 s, 40 s, 80 s ... up to 10 minutes. Refresh Now still runs at once.
            _refreshFailureCount++;
            var delay = TimeSpan.FromSeconds(Math.Min(600, 10 * Math.Pow(2, Math.Min(_refreshFailureCount, 6))));
            _nextTimedRefreshAt = DateTimeOffset.Now + delay;
            if (_refreshFailureCount == 1)
            {
                AppLog.Error("RefreshAsync failed.", ex);
            }
            else
            {
                AppLog.Info($"RefreshAsync failed again ({_refreshFailureCount}x): {ex.Message}");
            }

            StatusMessage = $"{ex.Message} Retrying in {delay.TotalSeconds:0} s.";
            return;
        }
        finally
        {
            IsRefreshing = false;
            _refreshGate.Release();
            AppLog.Info("RefreshAsync finished.");
        }
    }

    /// <summary>Timer refresh: skipped while a failed GitHub call is backing off.</summary>
    public Task RefreshOnTimerAsync(CancellationToken cancellationToken)
    {
        return DateTimeOffset.Now < _nextTimedRefreshAt
            ? Task.CompletedTask
            : RefreshAsync(cancellationToken);
    }

    public async Task ResolveSelectedRunAsync(CancellationToken cancellationToken)
    {
        var run = SelectedRun;
        if (run is null || run.IsResolving)
        {
            return;
        }

        if (run.HasActualCommitUrl && string.IsNullOrWhiteSpace(run.ResolutionError))
        {
            return;
        }

        AppLog.Info($"ResolveSelectedRunAsync started for run {run.Id}.");
        await _gitHubApiClient.ResolveRunDetailsAsync(run, cancellationToken);
        if (ReferenceEquals(SelectedRun, run) && !string.IsNullOrWhiteSpace(RunFilterText))
        {
            ApplyRunFilter();
        }
        AppLog.Info($"ResolveSelectedRunAsync completed for run {run.Id}.");
    }

    public void RefreshLiveRunMetrics()
    {
        foreach (var run in _allRuns)
        {
            run.RefreshLiveTiming();
        }
    }

    public void RefreshDiskUsageAnimation()
    {
        var now = DateTimeOffset.Now;

        foreach (var entry in _diskUsageSections.SelectMany(section => section.Entries))
        {
            entry.RefreshLiveState(now);
        }

        if (!IsDiskUsageRefreshing)
        {
            return;
        }

        _diskUsageAnimationFrame = (_diskUsageAnimationFrame + 1) % DiskUsageAnimationFrames.Count;
        SetDiskUsageActivityText(DiskUsageAnimationFrames[_diskUsageAnimationFrame]);
    }

    public bool AddRepository()
    {
        EnsureRepositoriesLoaded();

        if (!TryParseRepository(OwnerInput, RepoInput, out var repository))
        {
            StatusMessage = "Enter owner and repo, or paste owner/repo into the repo field.";
            return false;
        }

        if (Repositories.Any(existing =>
                string.Equals(existing.Owner, repository.Owner, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.Name, repository.Name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"{repository.FullName} is already in the list.";
            return false;
        }

        Repositories.Add(repository);
        SaveRepositories();
        RaisePropertyChanged(nameof(RepositorySummaryText));
        OwnerInput = repository.Owner;
        RepoInput = repository.Name;
        StatusMessage = $"Added {repository.FullName}.";
        AppLog.Info($"Repository added: {repository.FullName}");
        return true;
    }

    public bool RemoveRepository(RepositoryDefinition? repository)
    {
        EnsureRepositoriesLoaded();

        if (repository is null)
        {
            return false;
        }

        var removed = Repositories.Remove(repository);
        if (removed)
        {
            SaveRepositories();
            RaisePropertyChanged(nameof(RepositorySummaryText));
            StatusMessage = $"Removed {repository.FullName}.";
            AppLog.Info($"Repository removed: {repository.FullName}");
        }

        return removed;
    }

    public void ShowKillProcesses()
    {
        SetActiveTab(MainContentTab.KillProcesses);
    }

    public void ShowWorkflowExplorer()
    {
        SetActiveTab(MainContentTab.WorkflowExplorer);
    }

    public void ShowDiskUsage()
    {
        SetActiveTab(MainContentTab.DiskUsage);
    }

    public void ShowResources()
    {
        SetActiveTab(MainContentTab.Resources);
    }

    public async Task StartBiggestFilesScanAsync()
    {
        if (!await _biggestFilesGate.WaitAsync(0))
        {
            StatusMessage = "A biggest-files scan is already running.";
            return;
        }

        var cts = new CancellationTokenSource();
        _biggestFilesCts = cts;

        try
        {
            SetBiggestFilesScanning(true);
            _hasBiggestFilesSnapshot = false;
            RaisePropertyChanged(nameof(HasBiggestFilesSnapshot));

            var request = new BiggestFilesRequest(BiggestFilesScanRoot, BiggestFilesTopCount);
            var selection = BiggestFilesEngine.ResolveEngine(request.ScanRoot, request.AllowMft);
            _biggestFilesHeadline = $"Scanning {request.ScanRoot} for the {request.TopCount} biggest files...";
            SetBiggestFilesStatus(selection.Reason);
            RaisePropertyChanged(nameof(BiggestFilesHeadline));
            StatusMessage = $"Scanning {request.ScanRoot} with {selection.EngineName}...";

            var progress = new Progress<BiggestFilesProgress>(update =>
            {
                SetBiggestFilesResults(update.CurrentTop);
                SetBiggestFilesStatus(
                    $"{update.FilesSeen:N0} files · {update.FilesPerSecond:N0}/s · {FormatElapsed(update.Elapsed)} · {selection.EngineName}");
            });

            // Task.Run: the MFT engine's volume reads are synchronous before its first await and
            // would otherwise freeze the dispatcher. Progress<T> still marshals back to the UI thread.
            var result = await Task.Run(() => _biggestFilesEngine.ScanAsync(request, progress, cts.Token), cts.Token);

            SetBiggestFilesResults(result.Top);
            _hasBiggestFilesSnapshot = true;
            RaisePropertyChanged(nameof(HasBiggestFilesSnapshot));
            _biggestFilesHeadline =
                $"Top {result.Top.Count} files under {request.ScanRoot} - scanned {result.FilesSeen:N0} files in {FormatElapsed(result.Elapsed)} ({result.EngineName}).";
            RaisePropertyChanged(nameof(BiggestFilesHeadline));
            SetBiggestFilesStatus(result.Warning ??
                $"Done. {result.FilesSeen:N0} files, {ByteFormatter.Format(result.BytesSeen)} scanned in {FormatElapsed(result.Elapsed)}.");
            StatusMessage = $"Biggest-files scan complete via {result.EngineName}.";
        }
        catch (OperationCanceledException)
        {
            SetBiggestFilesStatus("Biggest-files scan canceled.");
            StatusMessage = "Biggest-files scan canceled.";
        }
        catch (Exception ex)
        {
            AppLog.Error("StartBiggestFilesScanAsync failed.", ex);
            SetBiggestFilesStatus($"Scan failed: {ex.Message}");
            StatusMessage = ex.Message;
        }
        finally
        {
            SetBiggestFilesScanning(false);
            _biggestFilesCts = null;
            cts.Dispose();
            _biggestFilesGate.Release();
        }
    }

    public void CancelBiggestFilesScan()
    {
        _biggestFilesCts?.Cancel();
    }

    /// <summary>Dry run of the bulk cleanup. Reports what would be deleted and deletes nothing.</summary>
    public Task<DiskCleanupResult> PreviewBulkCleanupAsync()
    {
        return RunBulkCleanupCoreAsync(execute: false);
    }

    /// <summary>Real run of the bulk cleanup. Deletes the three reviewed lanes.</summary>
    public Task<DiskCleanupResult> RunBulkCleanupAsync()
    {
        return RunBulkCleanupCoreAsync(execute: true);
    }

    public void CancelBulkCleanup()
    {
        _bulkCleanupCts?.Cancel();
    }

    private async Task<DiskCleanupResult> RunBulkCleanupCoreAsync(bool execute)
    {
        if (!await _bulkCleanupGate.WaitAsync(0))
        {
            StatusMessage = "A bulk cleanup is already running.";
            return new DiskCleanupResult(false, false, false, -1, "A bulk cleanup is already running.", DiskCleanupReport.Empty, []);
        }

        var cts = new CancellationTokenSource();
        _bulkCleanupCts = cts;
        var lines = new Queue<string>();

        try
        {
            _bulkCleanupReportText = string.Empty;
            RaisePropertyChanged(nameof(BulkCleanupReportText));
            RaisePropertyChanged(nameof(HasBulkCleanupReport));

            SetBulkCleanupRunning(true);
            SetBulkCleanupStatus(execute ? "Cleaning up..." : "Measuring what a cleanup would reclaim...");
            RaisePropertyChanged(nameof(BulkCleanupButtonText));
            StatusMessage = execute ? "Running the bulk cleanup..." : "Previewing the bulk cleanup...";

            var progress = new Progress<string>(line =>
            {
                lines.Enqueue(line);
                while (lines.Count > 400)
                {
                    lines.Dequeue();
                }

                _bulkCleanupReportText = string.Join(Environment.NewLine, lines);
                RaisePropertyChanged(nameof(BulkCleanupReportText));
                RaisePropertyChanged(nameof(HasBulkCleanupReport));
            });

            var result = await _diskCleanupService.RunAsync(execute, progress, cts.Token);

            SetBulkCleanupStatus(result.Message);
            StatusMessage = result.Message;

            if (result.Succeeded && execute && IsDiskUsageVisible)
            {
                await RefreshDiskUsageAsync(CancellationToken.None);
            }

            return result;
        }
        catch (Exception ex)
        {
            AppLog.Error("RunBulkCleanupCoreAsync failed.", ex);
            SetBulkCleanupStatus($"Bulk cleanup failed: {ex.Message}");
            StatusMessage = ex.Message;
            return new DiskCleanupResult(false, false, false, -1, ex.Message, DiskCleanupReport.Empty, []);
        }
        finally
        {
            SetBulkCleanupRunning(false);
            RaisePropertyChanged(nameof(BulkCleanupButtonText));
            _bulkCleanupCts = null;
            cts.Dispose();
            _bulkCleanupGate.Release();
        }
    }

    private void SetBulkCleanupRunning(bool isRunning)
    {
        SetProperty(ref _isBulkCleanupRunning, isRunning, nameof(IsBulkCleanupRunning));
    }

    private void SetBulkCleanupStatus(string statusText)
    {
        if (string.Equals(_bulkCleanupStatusText, statusText, StringComparison.Ordinal))
        {
            return;
        }

        _bulkCleanupStatusText = statusText;
        RaisePropertyChanged(nameof(BulkCleanupStatusText));
    }

    public async Task<bool> DeleteBiggestFileAsync(BiggestFileEntry? entry, CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            return false;
        }

        try
        {
            await Task.Run(() =>
            {
                if (!File.Exists(entry.Path))
                {
                    return;
                }

                try
                {
                    File.SetAttributes(entry.Path, System.IO.FileAttributes.Normal);
                }
                catch
                {
                    // Best-effort attribute reset before delete.
                }

                File.Delete(entry.Path);
            }, cancellationToken);

            _allBiggestFiles.Remove(entry);
            _biggestFiles.Remove(entry);
            RaisePropertyChanged(nameof(VisibleBiggestFilesText));
            StatusMessage = $"Deleted {entry.Path}.";
            return true;
        }
        catch (FileNotFoundException)
        {
            _allBiggestFiles.Remove(entry);
            _biggestFiles.Remove(entry);
            RaisePropertyChanged(nameof(VisibleBiggestFilesText));
            StatusMessage = $"File was already gone: {entry.Path}.";
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"DeleteBiggestFileAsync failed for {entry.Path}.", ex);
            StatusMessage = $"Delete failed: {ex.Message}";
            return false;
        }
    }

    public bool RequestRestartAsAdmin()
    {
        SaveSettings();

        if (ElevationService.TryRestartAsAdmin(out var error))
        {
            StatusMessage = "Relaunching DevTools as administrator...";
            return true;
        }

        StatusMessage = error ?? "Could not relaunch as administrator.";
        return false;
    }

    public async Task EnsureKillProcessesLoadedAsync(CancellationToken cancellationToken)
    {
        if (_hasKillProcessSnapshot || IsKillProcessesRefreshing)
        {
            return;
        }

        await RefreshKillProcessesAsync(cancellationToken);
    }

    public async Task RefreshKillProcessesAsync(CancellationToken cancellationToken)
    {
        AppLog.Info("RefreshKillProcessesAsync started.");

        if (!await _killProcessesRefreshGate.WaitAsync(0, cancellationToken))
        {
            AppLog.Info("RefreshKillProcessesAsync skipped because a refresh is already in progress.");
            return;
        }

        try
        {
            SetKillProcessesRefreshing(true);
            _hasKillProcessSnapshot = false;
            _killProcessesHeadline = "Scanning running processes...";
            _killProcessesSummary = "Loading process names and memory usage.";
            SetKillProcessesStatus("Starting process refresh...");
            RaiseKillProcessesProperties();
            StatusMessage = "Refreshing process list...";

            var sections = await Task.Run(() => _processKillCatalog.CaptureSections(), cancellationToken);
            _allKillProcessSections.Clear();
            _allKillProcessSections.AddRange(sections);
            ApplyProcessFilter();
            var totalCount = _allKillProcessSections.Sum(section => section.Entries.Count);
            _killProcessesSummary = "Expand groups with + on the left. Copy includes the process name, PID, and start path.";
            _hasKillProcessSnapshot = true;
            SetKillProcessesStatus($"Last refresh: {DateTimeOffset.Now.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
            RaiseKillProcessesProperties();

            StatusMessage = totalCount == 0
                ? "No matching processes found."
                : "Process list refreshed.";
            AppLog.Info("RefreshKillProcessesAsync completed.");
        }
        catch (OperationCanceledException)
        {
            SetKillProcessesStatus("Process refresh canceled.");
            StatusMessage = "Process refresh canceled.";
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("RefreshKillProcessesAsync failed.", ex);
            SetKillProcessesStatus($"Process refresh failed: {ex.Message}");
            StatusMessage = ex.Message;
        }
        finally
        {
            SetKillProcessesRefreshing(false);
            _killProcessesRefreshGate.Release();
            AppLog.Info("RefreshKillProcessesAsync finished.");
        }
    }

    public async Task<bool> KillProcessAsync(int processId, CancellationToken cancellationToken)
    {
        if (processId <= 0)
        {
            StatusMessage = "Invalid process id.";
            return false;
        }

        AppLog.Info($"KillProcessAsync requested for PID {processId}.");

        string? errorMessage = null;
        var killed = await Task.Run(
            () => _processKiller.TryKillProcessTree(processId, out errorMessage),
            cancellationToken);

        if (!killed)
        {
            StatusMessage = string.IsNullOrWhiteSpace(errorMessage)
                ? $"Failed to kill PID {processId}."
                : errorMessage;
            return false;
        }

        StatusMessage = $"Killed process tree for PID {processId}.";
        await RefreshKillProcessesAsync(cancellationToken);
        return true;
    }

    public async Task<int> KillProcessSectionAsync(ProcessKillSection section, CancellationToken cancellationToken)
    {
        if (section is null)
        {
            return 0;
        }

        var processIds = section.Entries
            .Where(entry => entry.CanKill)
            .Select(entry => entry.ProcessId)
            .ToList();

        if (processIds.Count == 0)
        {
            StatusMessage = $"No killable processes in {section.Title}.";
            return 0;
        }

        AppLog.Info($"KillProcessSectionAsync requested for {section.Title} ({processIds.Count} process(es)).");

        string? errorMessage = null;
        var killedCount = await Task.Run(
            () => _processKiller.TryKillProcesses(processIds, out errorMessage),
            cancellationToken);

        StatusMessage = killedCount == processIds.Count
            ? $"Killed {killedCount} process(es) in {section.Title}."
            : string.IsNullOrWhiteSpace(errorMessage)
                ? $"Killed {killedCount} of {processIds.Count} process(es) in {section.Title}."
                : $"Killed {killedCount} of {processIds.Count} process(es) in {section.Title}: {errorMessage}";

        await RefreshKillProcessesAsync(cancellationToken);
        return killedCount;
    }

    private void WorkflowRunItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not WorkflowRunItem run)
        {
            return;
        }

        if (!HasActiveRunFilter() || !ShouldReapplyRunFilter(e.PropertyName))
        {
            return;
        }

        ApplyRunFilter();
    }

    private void ApplyRunFilter(long? preferredRunId = null)
    {
        var targetRunId = preferredRunId ?? SelectedRun?.Id;
        var filteredRuns = !HasActiveRunFilter()
            ? _allRuns
            : _allRuns.Where(MatchesFilter).ToList();

        SelectionRetention.Synchronize(Runs, filteredRuns);

        RaisePropertyChanged(nameof(VisibleRunsText));

        SelectedRun = SelectionRetention.Find(Runs, targetRunId, run => run.Id);
    }

    private bool HasActiveRunFilter()
    {
        return !string.IsNullOrWhiteSpace(RunFilterText) ||
               !string.Equals(StatusFilter, "All", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldReapplyRunFilter(string? propertyName)
    {
        return string.IsNullOrWhiteSpace(propertyName) || FilterSensitiveRunProperties.Contains(propertyName);
    }

    private bool MatchesFilter(WorkflowRunItem run)
    {
        if (!string.Equals(StatusFilter, "All", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(run.StatusCategory, StatusFilter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var terms = TokenizeFilterTerms(RunFilterText);
        if (terms.Count == 0)
        {
            return true;
        }

        var searchableValues = SearchableFields(run)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();

        return terms.Any(term =>
            searchableValues.Any(value => value.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    private static IEnumerable<string?> SearchableFields(WorkflowRunItem run)
    {
        yield return run.Id.ToString();
        yield return run.DisplayAuthor;
        yield return run.Repository.FullName;
        yield return run.WorkflowName;
        yield return run.DisplayTitle;
        yield return run.WorkflowMetaSummary;
        yield return run.EventName;
        yield return run.UiBranch;
        yield return run.ActualBranch;
        yield return run.UiSha;
        yield return run.ActualSha;
        yield return run.Status;
        yield return run.Conclusion;
        yield return run.ActorLogin;
        yield return run.TriggeringActorLogin;
        yield return run.PullRequestTitle;
        yield return run.PullRequestSummary;
        yield return run.PullRequestDescriptionRaw;
        yield return run.RunDescriptionRaw;
        yield return run.PullRequestAuthor;
        yield return run.PullRequestUrl;
        yield return run.RunUrl;

        foreach (var pullRequestNumber in run.AssociatedPullRequestNumbers)
        {
            yield return pullRequestNumber.ToString();
            yield return $"pr {pullRequestNumber}";
            yield return $"pr #{pullRequestNumber}";
            yield return $"#{pullRequestNumber}";
        }
    }

    private static IReadOnlyList<string> TokenizeFilterTerms(string? filterText)
    {
        if (string.IsNullOrWhiteSpace(filterText))
        {
            return [];
        }

        return filterText
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.Trim())
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ApplyProcessFilter()
    {
        var selectedActivityId = SelectedActivity?.Id;
        var filteredSections = ProcessKillFilter.Apply(_allKillProcessSections, _processFilterText,
            _processViewState.SortColumn, _processViewState.SortDescending);
        SetKillProcessSections(filteredSections);
        SelectedProcess = SelectionRetention.Find(_killProcessSections.SelectMany(section => section.Entries),
            SelectedProcess?.ProcessId, entry => entry.ProcessId);
        SelectedActivity = _killProcessSections.SelectMany(section => section.Activities).FirstOrDefault(activity => activity.Id == selectedActivityId);

        var visibleCount = filteredSections.Sum(section => section.Entries.Count);
        var groupCount = filteredSections.Count;
        var activityCount = filteredSections.Sum(section => section.Activities.Count);
        _killProcessesHeadline = visibleCount == 0 && activityCount == 0
            ? string.IsNullOrWhiteSpace(_processFilterText)
                ? "No matching processes found."
                : "No processes match the current search."
            : groupCount == 1
                ? $"{visibleCount} processes · {activityCount} threads / tabs. {ProcessSortSummary}"
                : $"{visibleCount} processes in {groupCount} groups · {activityCount} threads / tabs. {ProcessSortSummary}";

        RaisePropertyChanged(nameof(VisibleProcessesText));
        RaisePropertyChanged(nameof(KillProcessesHeadline));
        RaisePropertyChanged(nameof(LargestProcesses));
        RaisePropertyChanged(nameof(ProcessOverviewSummary));
        RaisePropertyChanged(nameof(ContextPaneSubtitle));
    }

    public string ProcessSortSummary => $"Sorted by {(_processViewState.SortColumn == ProcessSortColumn.StartPath ? "start path" : _processViewState.SortColumn.ToString().ToLowerInvariant())}, {(_processViewState.SortDescending ? "descending" : "ascending")}.";
    public void SortProcesses(ProcessSortColumn column)
    {
        _processViewState.ToggleSort(column);
        ApplyProcessFilter();
        SaveSettings();
        RaisePropertyChanged(nameof(ProcessSortSummary));
    }

    public void ToggleProcessSection(ProcessKillSection section)
    {
        section.ToggleExpanded();
        RememberKillProcessSectionExpansionStates();
        SaveSettings();
    }

    private void SetBiggestFilesResults(IReadOnlyList<FileSizeHit> hits)
    {
        _allBiggestFiles.Clear();
        foreach (var hit in hits)
        {
            _allBiggestFiles.Add(BiggestFileEntry.FromHit(hit));
        }

        ApplyBiggestFilesFilter();
    }

    private void ApplyBiggestFilesFilter()
    {
        var terms = TokenizeFilterTerms(_biggestFilesFilterText);
        IEnumerable<BiggestFileEntry> filtered = _allBiggestFiles;
        if (terms.Count > 0)
        {
            filtered = _allBiggestFiles.Where(entry => terms.Any(term =>
                entry.FileName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                entry.DirectoryPath.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                entry.Path.Contains(term, StringComparison.OrdinalIgnoreCase)));
        }

        var desired = filtered.ToList();
        if (SameBiggestFiles(desired))
        {
            return;
        }

        _biggestFiles.Clear();
        foreach (var entry in desired)
        {
            _biggestFiles.Add(entry);
        }

        RaisePropertyChanged(nameof(VisibleBiggestFilesText));
    }

    private bool SameBiggestFiles(IReadOnlyList<BiggestFileEntry> desired)
    {
        if (desired.Count != _biggestFiles.Count)
        {
            return false;
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (!ReferenceEquals(desired[i], _biggestFiles[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void SetBiggestFilesScanning(bool isScanning)
    {
        if (SetProperty(ref _isBiggestFilesScanning, isScanning, nameof(IsBiggestFilesScanning)))
        {
            RaisePropertyChanged(nameof(ShowElevationHint));
        }
    }

    private void SetBiggestFilesStatus(string statusText)
    {
        if (_biggestFilesStatusText == statusText)
        {
            return;
        }

        _biggestFilesStatusText = statusText;
        RaisePropertyChanged(nameof(BiggestFilesStatusText));
    }

    private static int NormalizeTopCount(int value)
    {
        return BiggestFilesTopCountChoices.Contains(value) ? value : 100;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return elapsed.TotalHours >= 1
            ? elapsed.ToString(@"hh\:mm\:ss")
            : elapsed.ToString(@"mm\:ss");
    }

    private void SetActiveTab(MainContentTab tab)
    {
        if (SetProperty(ref _activeTab, tab, nameof(ActiveTab)))
        {
            SaveSettings();
            RaisePropertyChanged(nameof(IsKillProcessesVisible));
            RaisePropertyChanged(nameof(IsWorkflowExplorerVisible));
            RaisePropertyChanged(nameof(IsDiskUsageVisible));
            RaisePropertyChanged(nameof(IsResourcesVisible));
            RaisePropertyChanged(nameof(IsOpenWorkVisible));
            RaisePropertyChanged(nameof(IsWorktreesVisible));
            RaisePropertyChanged(nameof(ContextPaneTitle));
            RaisePropertyChanged(nameof(ContextPaneSubtitle));
        }
    }

    private void SetKillProcessesRefreshing(bool isRefreshing)
    {
        if (SetProperty(ref _isKillProcessesRefreshing, isRefreshing, nameof(IsKillProcessesRefreshing)))
        {
            RaisePropertyChanged(nameof(KillProcessesStatusText));
        }
    }

    private void SetKillProcessesStatus(string statusText)
    {
        if (_killProcessesStatusText == statusText)
        {
            return;
        }

        _killProcessesStatusText = statusText;
        RaisePropertyChanged(nameof(KillProcessesStatusText));
    }

    private void RaiseKillProcessesProperties()
    {
        RaisePropertyChanged(nameof(KillProcessesHeadline));
        RaisePropertyChanged(nameof(KillProcessesSummary));
        RaisePropertyChanged(nameof(KillProcessesStatusText));
        RaisePropertyChanged(nameof(VisibleProcessesText));
    }

    private void SetKillProcessSections(IReadOnlyList<ProcessKillSection> sections)
    {
        RememberKillProcessSectionExpansionStates();
        var incomingTitles = new HashSet<string>(sections.Select(section => section.Title), StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sections.Count; i++)
        {
            var incomingSection = sections[i];
            var existingIndex = FindKillProcessSectionIndex(incomingSection.Title);
            if (existingIndex < 0)
            {
                if (_killProcessSectionExpansionStates.TryGetValue(incomingSection.Title, out var isExpanded))
                {
                    incomingSection.IsExpanded = isExpanded;
                }

                _killProcessSections.Insert(i, incomingSection);
                continue;
            }

            var existingSection = _killProcessSections[existingIndex];
            existingSection.UpdateFrom(incomingSection);
            if (existingIndex != i)
            {
                _killProcessSections.Move(existingIndex, i);
            }
        }

        for (var i = _killProcessSections.Count - 1; i >= 0; i--)
        {
            if (!incomingTitles.Contains(_killProcessSections[i].Title))
            {
                _killProcessSections.RemoveAt(i);
            }
        }
    }

    private void RememberKillProcessSectionExpansionStates()
    {
        foreach (var section in _killProcessSections)
        {
            _killProcessSectionExpansionStates[section.Title] = section.IsExpanded;
        }
    }

    private int FindKillProcessSectionIndex(string title)
    {
        for (var i = 0; i < _killProcessSections.Count; i++)
        {
            if (string.Equals(_killProcessSections[i].Title, title, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private void SetDiskUsageRefreshing(bool isRefreshing)
    {
        if (SetProperty(ref _isDiskUsageRefreshing, isRefreshing, nameof(IsDiskUsageRefreshing)))
        {
            RaisePropertyChanged(nameof(DiskUsageStatusText));
        }

        if (isRefreshing)
        {
            _diskUsageAnimationFrame = 0;
        }
    }

    private void SetDiskUsageStatus(string statusText)
    {
        if (_diskUsageStatusText == statusText)
        {
            return;
        }

        _diskUsageStatusText = statusText;
        RaisePropertyChanged(nameof(DiskUsageStatusText));
    }

    private void SetDiskUsageActivityText(string activityText)
    {
        if (_diskUsageActivityText == activityText)
        {
            return;
        }

        _diskUsageActivityText = activityText;
        RaisePropertyChanged(nameof(DiskUsageActivityText));
    }

    private void RaiseDiskUsageProperties()
    {
        RaisePropertyChanged(nameof(DiskUsageHeadline));
        RaisePropertyChanged(nameof(DiskUsageAnalysis));
        RaisePropertyChanged(nameof(DiskUsageActivityText));
        RaisePropertyChanged(nameof(DiskUsageStatusText));
        RaisePropertyChanged(nameof(HasDiskUsageSnapshot));
    }

    private void SetDiskUsageSections(IReadOnlyList<DiskUsageSection> sections)
    {
        RememberDiskUsageSectionExpansionStates();
        var incomingTitles = new HashSet<string>(sections.Select(section => section.Title), StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < sections.Count; i++)
        {
            var incomingSection = sections[i];
            var existingIndex = FindDiskUsageSectionIndex(incomingSection.Title);
            if (existingIndex < 0)
            {
                if (_diskUsageSectionExpansionStates.TryGetValue(incomingSection.Title, out var isExpanded))
                {
                    incomingSection.IsExpanded = isExpanded;
                }
                else if (incomingSection.Title.StartsWith(DiskUsageAnalyzer.LargestFoldersSectionTitle, StringComparison.OrdinalIgnoreCase))
                {
                    incomingSection.IsExpanded = true;
                }

                _diskUsageSections.Insert(i, incomingSection);
                continue;
            }

            var existingSection = _diskUsageSections[existingIndex];
            existingSection.UpdateFrom(incomingSection);
            if (existingIndex != i)
            {
                _diskUsageSections.Move(existingIndex, i);
            }
        }

        for (var i = _diskUsageSections.Count - 1; i >= 0; i--)
        {
            if (!incomingTitles.Contains(_diskUsageSections[i].Title))
            {
                _diskUsageSections.RemoveAt(i);
            }
        }
    }

    private void RememberDiskUsageSectionExpansionStates()
    {
        foreach (var section in _diskUsageSections)
        {
            _diskUsageSectionExpansionStates[section.Title] = section.IsExpanded;
        }
    }

    private int FindDiskUsageSectionIndex(string title)
    {
        for (var i = 0; i < _diskUsageSections.Count; i++)
        {
            if (string.Equals(_diskUsageSections[i].Title, title, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private void LoadSettings()
    {
        _isLoadingSettings = true;

        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return;
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(SettingsFilePath),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (settings is null)
            {
                return;
            }

            _autoRefreshEnabled = settings.AutoRefreshEnabled;
            _refreshIntervalSeconds = NormalizeRefreshInterval(settings.RefreshIntervalSeconds);
            _activeTab = ResolveActiveTab(settings);
            _encryptedPersonalAccessToken = settings.EncryptedPersonalAccessToken;
            _processViewState = settings.ProcessView ?? new();
            _processViewState.Normalize();
            _processFilterText = _processViewState.FilterText;

            if (!string.IsNullOrWhiteSpace(settings.BiggestFilesScanRoot))
            {
                _biggestFilesScanRoot = settings.BiggestFilesScanRoot;
            }

            _biggestFilesTopCount = NormalizeTopCount(settings.BiggestFilesTopCount);
            _resourceMonitoringEnabled = settings.ResourceMonitoringEnabled;
            _resourceIntervalSeconds = ResourceIntervalOptions.Contains(settings.ResourceIntervalSeconds)
                ? settings.ResourceIntervalSeconds
                : 1;
            _runAsAdministrator = settings.RunAsAdministrator;

            var persistedPat = UnprotectSecret(_encryptedPersonalAccessToken);
            if (!string.IsNullOrWhiteSpace(persistedPat))
            {
                _gitHubApiClient.SetPersonalAccessToken(persistedPat);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Loading settings failed.", ex);
        }
        finally
        {
            _isLoadingSettings = false;
        }
    }

    private void SaveSettings()
    {
        if (_isLoadingSettings)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            RememberKillProcessSectionExpansionStates();
            _processViewState.FilterText = _processFilterText;
            File.WriteAllText(
                SettingsFilePath + ".tmp",
                JsonSerializer.Serialize(
                    new AppSettings
                    {
                        AutoRefreshEnabled = AutoRefreshEnabled,
                        RefreshIntervalSeconds = RefreshIntervalSeconds,
                        ActiveTab = _activeTab.ToString(),
                        ProcessView = _processViewState,
                        EncryptedPersonalAccessToken = _encryptedPersonalAccessToken,
                        BiggestFilesScanRoot = _biggestFilesScanRoot,
                        BiggestFilesTopCount = _biggestFilesTopCount,
                        ResourceMonitoringEnabled = _resourceMonitoringEnabled,
                        ResourceIntervalSeconds = _resourceIntervalSeconds,
                        RunAsAdministrator = _runAsAdministrator
                    },
                    new JsonSerializerOptions
                    {
                        WriteIndented = true
                    }));
            File.Move(SettingsFilePath + ".tmp", SettingsFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Saving settings failed.", ex);
        }
    }

    private static int NormalizeRefreshInterval(int value)
    {
        return RefreshIntervalChoices.Contains(value) ? value : 10;
    }

    private static string? ProtectSecret(string? plainText)
    {
        if (string.IsNullOrWhiteSpace(plainText))
        {
            return null;
        }

        try
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));
        }
        catch (Exception ex)
        {
            AppLog.Error("ProtectSecret failed.", ex);
            return null;
        }
    }

    private static string? UnprotectSecret(string? protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(protectedValue));
        }
        catch (Exception ex)
        {
            AppLog.Error("UnprotectSecret failed.", ex);
            return null;
        }
    }

    private static string ComputeRunsHash(IEnumerable<WorkflowRunItem> runs)
    {
        var payload = new StringBuilder();

        foreach (var run in runs)
        {
            payload.Append(run.Repository.FullName).Append('|')
                .Append(run.Id).Append('|')
                .Append(run.WorkflowName).Append('|')
                .Append(run.DisplayTitle).Append('|')
                .Append(run.EventName).Append('|')
                .Append(run.UiBranch).Append('|')
                .Append(run.UiSha).Append('|')
                .Append(run.Status).Append('|')
                .Append(run.Conclusion).Append('|')
                .Append(run.CreatedAt.UtcDateTime.Ticks).Append('|')
                .Append(run.UpdatedAt.UtcDateTime.Ticks).Append('|')
                .Append(run.ActorLogin).AppendLine();
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(payload.ToString()));
        return Convert.ToHexString(bytes);
    }

    private void LoadRepositories()
    {
        AppLog.Info("Loading repositories from local settings.");
        Repositories.Clear();

        if (!File.Exists(RepositoriesFilePath))
        {
            RaisePropertyChanged(nameof(RepositorySummaryText));
            return;
        }

        var storedValue = File.ReadAllText(RepositoriesFilePath);

        foreach (var line in storedValue
                     .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                Repositories.Add(new RepositoryDefinition(parts[0], parts[1]));
                AppLog.Info($"Loaded repository from settings: {parts[0]}/{parts[1]}");
            }
        }

        RaisePropertyChanged(nameof(RepositorySummaryText));
    }

    private void EnsureRepositoriesLoaded()
    {
        if (!_repositoriesLoaded)
        {
            LoadRepositories();
            _repositoriesLoaded = true;
        }

        // No repository is pre-configured. The Runs and Open Work tabs stay empty until one is added, so
        // the app never assumes a particular owner or project.
        if (Repositories.Count == 0)
        {
            StatusMessage = "No repository configured yet. Add one under Auth, Refresh, and Repository Setup.";
            AppLog.Info("No repositories configured; waiting for one to be added.");
        }

        RaisePropertyChanged(nameof(RepositorySummaryText));
    }

    private void SaveRepositories()
    {
        Directory.CreateDirectory(SettingsDirectory);
        File.WriteAllText(
            RepositoriesFilePath,
            string.Join(
            Environment.NewLine,
            Repositories.Select(repository => repository.FullName)));
        AppLog.Info($"Saved repositories to {RepositoriesFilePath}.");
    }

    private static bool TryParseRepository(string ownerInput, string repoInput, out RepositoryDefinition repository)
    {
        repository = null!;

        var owner = ownerInput.Trim();
        var repo = repoInput.Trim();

        if (repo.Contains('/'))
        {
            var parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                repository = new RepositoryDefinition(parts[0], parts[1]);
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            return false;
        }

        repository = new RepositoryDefinition(owner, repo);
        return true;
    }

    private static MainContentTab ResolveActiveTab(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ActiveTab) &&
            Enum.TryParse<MainContentTab>(settings.ActiveTab, ignoreCase: true, out var parsedTab))
        {
            return parsedTab;
        }

        return settings.IsDiskUsageVisible
            ? MainContentTab.DiskUsage
            : MainContentTab.KillProcesses;
    }

    private sealed class AppSettings
    {
        public ProcessViewState? ProcessView { get; set; }
        public bool AutoRefreshEnabled { get; set; } = true;

        public int RefreshIntervalSeconds { get; set; } = 10;

        public string? ActiveTab { get; set; }

        public bool IsDiskUsageVisible { get; set; }

        public string? EncryptedPersonalAccessToken { get; set; }

        public string? BiggestFilesScanRoot { get; set; }

        public int BiggestFilesTopCount { get; set; } = 100;

        public bool ResourceMonitoringEnabled { get; set; } = true;

        public int ResourceIntervalSeconds { get; set; } = 1;

        /// <summary>Read by Start-DevTools.ps1: start DevTools with administrator rights.</summary>
        public bool RunAsAdministrator { get; set; } = true;
    }
}
