using System.Collections.ObjectModel;
using DevTools.Infrastructure;
using DevTools.Models;
using DevTools.Services;

namespace DevTools.ViewModels;

/// <summary>
/// The Projects tab: scans developer project directories (C# .NET .sln/.csproj, Node.js, Rust,
/// Python, Java/Gradle, CMake, Unity) and cleans build output and optional dependency folders.
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly IReadOnlyList<string> ProjectEcosystemFilterChoices =
        ["All", "C# / .NET", "Node.js", "Rust", "Python", "Java / Gradle", "C++ / CMake", "Unity"];

    private static readonly IReadOnlyList<string> ProjectSortChoices =
        ["Reclaimable size (largest first)", "Build output (largest first)", "Name (A-Z)"];

    private readonly SemaphoreSlim _projectsScanGate = new(1, 1);
    private readonly List<ProjectCleanItem> _allProjects = [];
    private string _projectsScanRoot = RepositoryRootLocator.Resolve();
    private string _projectsSearchText = string.Empty;
    private string _projectsEcosystemFilter = "All";
    private string _projectsSort = "Reclaimable size (largest first)";
    private bool _projectsIncludeDependencies;
    private string _projectsHeadline = "Scan your dev folder to find cleanable C#, Node, Rust, Python, and Gradle build folders.";
    private string _projectsStatusText = "No project scan run yet.";
    private bool _isProjectsScanning;
    private bool _isProjectsCleaning;
    private bool _hasProjectsSnapshot;
    private CancellationTokenSource? _projectsCts;

    public ObservableCollection<ProjectCleanItem> CleanableProjects { get; } = [];

    public bool IsProjectsVisible => _activeTab == MainContentTab.Projects;

    public IReadOnlyList<string> ProjectEcosystemFilterOptions => ProjectEcosystemFilterChoices;

    public IReadOnlyList<string> ProjectSortOptions => ProjectSortChoices;

    public string ProjectsScanRoot
    {
        get => _projectsScanRoot;
        set => SetProperty(ref _projectsScanRoot, value ?? string.Empty);
    }

    public string ProjectsSearchText
    {
        get => _projectsSearchText;
        set
        {
            if (SetProperty(ref _projectsSearchText, value ?? string.Empty))
            {
                ApplyProjectsFilter();
            }
        }
    }

    public string ProjectsEcosystemFilter
    {
        get => _projectsEcosystemFilter;
        set
        {
            if (SetProperty(ref _projectsEcosystemFilter, ProjectEcosystemFilterChoices.Contains(value) ? value : "All"))
            {
                ApplyProjectsFilter();
            }
        }
    }

    public string ProjectsSort
    {
        get => _projectsSort;
        set
        {
            if (SetProperty(ref _projectsSort, ProjectSortChoices.Contains(value) ? value : ProjectSortChoices[0]))
            {
                ApplyProjectsFilter();
            }
        }
    }

    public bool ProjectsIncludeDependencies
    {
        get => _projectsIncludeDependencies;
        set
        {
            if (SetProperty(ref _projectsIncludeDependencies, value))
            {
                foreach (var item in _allProjects)
                {
                    item.SetIncludeDependencies(value);
                }

                ApplyProjectsFilter();
                UpdateProjectsHeadline();
                RaiseProjectsSelectionProperties();
            }
        }
    }

    public string ProjectsHeadline => _projectsHeadline;

    public string ProjectsStatusText => _projectsStatusText;

    public bool IsProjectsScanning
    {
        get => _isProjectsScanning;
        private set
        {
            if (SetProperty(ref _isProjectsScanning, value))
            {
                RaiseProjectsSelectionProperties();
            }
        }
    }

    public bool IsProjectsCleaning
    {
        get => _isProjectsCleaning;
        private set
        {
            if (SetProperty(ref _isProjectsCleaning, value))
            {
                RaiseProjectsSelectionProperties();
            }
        }
    }

    public bool HasProjectsSnapshot => _hasProjectsSnapshot;

    public string VisibleProjectsText =>
        _allProjects.Count == CleanableProjects.Count
            ? $"{CleanableProjects.Count} project(s)"
            : $"{CleanableProjects.Count} of {_allProjects.Count} project(s)";

    public int SelectedProjectCount => _allProjects.Count(p => p.IsSelected);

    public long SelectedProjectBytes => _allProjects.Where(p => p.IsSelected).Sum(p => p.ReclaimableBytes);

    public bool HasSelectedProjects => SelectedProjectCount > 0;

    public bool CanCleanSelectedProjects => HasSelectedProjects && !_isProjectsCleaning && !_isProjectsScanning;

    public string CleanSelectedProjectsButtonText => SelectedProjectCount == 0
        ? "Clean Selected"
        : $"Clean Selected ({SelectedProjectCount} - {CleanupRules.FormatBytes(SelectedProjectBytes)})";

    public string SelectedProjectsSummaryText
    {
        get
        {
            if (SelectedProjectCount == 0)
            {
                return _projectsIncludeDependencies
                    ? "Mode: Build outputs + dependencies (node_modules, .venv, packages). Select projects to clean."
                    : "Mode: Safe build outputs only (bin, obj, target, .next, dist, __pycache__).";
            }

            var mode = _projectsIncludeDependencies ? "build outputs + dependencies" : "build outputs only";
            return $"Selected {SelectedProjectCount} project(s) ({CleanupRules.FormatBytes(SelectedProjectBytes)}, {mode}).";
        }
    }

    public void ShowProjects()
    {
        SetActiveTab(MainContentTab.Projects);
    }

    public async Task EnsureProjectsLoadedAsync(CancellationToken cancellationToken)
    {
        if (_hasProjectsSnapshot || _isProjectsScanning)
        {
            return;
        }

        await RefreshProjectsAsync(cancellationToken);
    }

    public async Task RefreshProjectsAsync(CancellationToken cancellationToken)
    {
        if (!await _projectsScanGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        _projectsCts?.Dispose();
        _projectsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _projectsCts.Token;

        try
        {
            IsProjectsScanning = true;
            var root = string.IsNullOrWhiteSpace(_projectsScanRoot) ? RepositoryRootLocator.Resolve() : _projectsScanRoot.Trim();
            _projectsScanRoot = root;
            RaisePropertyChanged(nameof(ProjectsScanRoot));

            SetProjectsStatus($"Scanning {root} for projects...");
            StatusMessage = $"Scanning projects under {root}...";

            var progress = new Progress<ProjectScanProgress>(update => SetProjectsStatus(update.StatusText));
            var snapshot = await ProjectCleanerService.ScanAsync(
                root,
                (path, ct) => _diskUsageAnalyzer.MeasureDirectoryBytesAsync(path, ct),
                progress,
                token);

            foreach (var old in _allProjects)
            {
                old.SelectionChanged -= ProjectItem_SelectionChanged;
            }

            _allProjects.Clear();
            foreach (var project in snapshot.Projects)
            {
                var item = new ProjectCleanItem(project, _projectsIncludeDependencies);
                item.SelectionChanged += ProjectItem_SelectionChanged;
                _allProjects.Add(item);
            }

            _hasProjectsSnapshot = true;
            RaisePropertyChanged(nameof(HasProjectsSnapshot));
            ApplyProjectsFilter();
            UpdateProjectsHeadline();
            SetProjectsStatus($"Scanned {root} in {snapshot.Elapsed.TotalSeconds:F1}s. Found {_allProjects.Count} cleanable project(s).");
            StatusMessage = $"Found {_allProjects.Count} cleanable project(s).";
        }
        catch (OperationCanceledException)
        {
            SetProjectsStatus("Project scan canceled.");
        }
        catch (Exception ex)
        {
            AppLog.Error("RefreshProjectsAsync failed.", ex);
            SetProjectsStatus($"Project scan failed: {ex.Message}");
            StatusMessage = ex.Message;
        }
        finally
        {
            IsProjectsScanning = false;
            _projectsScanGate.Release();
        }
    }

    public IReadOnlyList<ProjectCleanItem> GetSelectedProjects() =>
        _allProjects.Where(p => p.IsSelected && p.CanClean).ToList();

    public void SelectAllVisibleProjects()
    {
        foreach (var item in CleanableProjects)
        {
            if (item.CanClean)
            {
                item.IsSelected = true;
            }
        }

        RaiseProjectsSelectionProperties();
    }

    public void SelectBuildOutputProjectsOnly()
    {
        foreach (var item in _allProjects)
        {
            item.IsSelected = false;
        }

        foreach (var item in CleanableProjects)
        {
            if (item.HasBuildOutput && item.CanClean)
            {
                item.IsSelected = true;
            }
        }

        RaiseProjectsSelectionProperties();
    }

    public void ClearProjectSelection()
    {
        foreach (var item in _allProjects)
        {
            item.IsSelected = false;
        }

        RaiseProjectsSelectionProperties();
    }

    public void CancelProjectsOperation()
    {
        _projectsCts?.Cancel();
    }

    public async Task<ProjectCleanResult?> CleanProjectsAsync(IReadOnlyList<ProjectCleanItem> targets, bool includeDependencies)
    {
        if (targets.Count == 0 || _isProjectsCleaning)
        {
            return null;
        }

        _projectsCts?.Dispose();
        _projectsCts = new CancellationTokenSource();
        var token = _projectsCts.Token;

        IsProjectsCleaning = true;
        foreach (var item in targets)
        {
            item.IsCleaning = true;
        }

        try
        {
            var progress = new Progress<ProjectCleanProgress>(update =>
            {
                SetProjectsStatus($"Cleaned {update.CompletedFolders} of {update.TotalFolders} folder(s) ({CleanupRules.FormatBytes(update.BytesFreed)} freed) - {update.CurrentPath}");
            });

            var result = await ProjectCleanerService.CleanAsync(
                targets.Select(t => t.Project).ToList(),
                includeDependencies,
                progress,
                token);

            foreach (var item in targets)
            {
                item.IsCleaning = false;
                item.IsSelected = false;
                item.ResultText = $"Cleaned at {DateTime.Now:HH:mm}";
            }

            var failedNote = result.Failed.Count == 0 ? string.Empty : $" {result.Failed.Count} folder(s) had locked files.";
            var summary = $"Cleaned {result.FoldersDeleted} folder(s) across {result.ProjectsCleaned} project(s) ({result.FilesDeleted:N0} files, {CleanupRules.FormatBytes(result.BytesFreed)}) in {result.Elapsed.TotalSeconds:F2}s.{failedNote}";
            SetProjectsStatus(summary);
            StatusMessage = summary;

            // Rescan cleanly in background or immediately so sizes reflect disk reality.
            await RefreshProjectsAsync(CancellationToken.None);
            SetProjectsStatus(summary);
            return result;
        }
        catch (OperationCanceledException)
        {
            SetProjectsStatus("Project cleanup canceled.");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("CleanProjectsAsync failed.", ex);
            SetProjectsStatus($"Project cleanup failed: {ex.Message}");
            StatusMessage = ex.Message;
            return null;
        }
        finally
        {
            foreach (var item in targets)
            {
                item.IsCleaning = false;
            }

            IsProjectsCleaning = false;
            RaiseProjectsSelectionProperties();
        }
    }

    private void ApplyProjectsFilter()
    {
        var terms = TokenizeFilterTerms(_projectsSearchText);
        var filtered = _allProjects
            .Where(item => item.ReclaimableBytes > 0)
            .Where(item => _projectsEcosystemFilter == "All" ||
                           string.Equals(item.EcosystemLabel, _projectsEcosystemFilter, StringComparison.OrdinalIgnoreCase))
            .Where(item => terms.Count == 0 || terms.All(term =>
                item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.RootPath.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                item.EcosystemLabel.Contains(term, StringComparison.OrdinalIgnoreCase)));

        var ordered = _projectsSort switch
        {
            "Build output (largest first)" => filtered
                .OrderByDescending(item => item.BuildOutputBytes)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            "Name (A-Z)" => filtered
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered
                .OrderByDescending(item => item.ReclaimableBytes)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        };

        var visible = ordered.ToList();
        if (!CleanableProjects.SequenceEqual(visible))
        {
            ReplaceAllItems(CleanableProjects, visible);
        }

        RaisePropertyChanged(nameof(VisibleProjectsText));
        RaiseProjectsSelectionProperties();
    }

    private void UpdateProjectsHeadline()
    {
        var buildBytes = _allProjects.Sum(p => p.BuildOutputBytes);
        var depBytes = _allProjects.Sum(p => p.DependencyBytes);
        _projectsHeadline = _allProjects.Count == 0
            ? $"No cleanable project folders found under {_projectsScanRoot}."
            : $"{_allProjects.Count} project(s) have {CleanupRules.FormatBytes(buildBytes)} of safe build outputs (bin/obj/target/.next/dist) and {CleanupRules.FormatBytes(depBytes)} of dependencies (node_modules/.venv/packages).";
        RaisePropertyChanged(nameof(ProjectsHeadline));
    }

    private void SetProjectsStatus(string status)
    {
        SetProperty(ref _projectsStatusText, status, nameof(ProjectsStatusText));
    }

    private void ProjectItem_SelectionChanged(object? sender, EventArgs e)
    {
        RaiseProjectsSelectionProperties();
    }

    private void RaiseProjectsSelectionProperties()
    {
        RaisePropertyChanged(nameof(SelectedProjectCount));
        RaisePropertyChanged(nameof(SelectedProjectBytes));
        RaisePropertyChanged(nameof(HasSelectedProjects));
        RaisePropertyChanged(nameof(CanCleanSelectedProjects));
        RaisePropertyChanged(nameof(CleanSelectedProjectsButtonText));
        RaisePropertyChanged(nameof(SelectedProjectsSummaryText));
    }
}
