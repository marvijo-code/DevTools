using DevTools.Infrastructure;
using DevTools.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace DevTools.Models;

/// <summary>
/// One cleanable developer project row in the Projects tab.
/// </summary>
public sealed class ProjectCleanItem : ObservableObject
{
    private static readonly Brush DotNetAccentBrush = CreateBrush(0xff, 0x51, 0x2b, 0xd4);
    private static readonly Brush DotNetBackgroundBrush = CreateBrush(0x18, 0x51, 0x2b, 0xd4);
    private static readonly Brush DotNetBorderBrush = CreateBrush(0x60, 0x51, 0x2b, 0xd4);
    private static readonly Brush NodeAccentBrush = CreateBrush(0xff, 0x1f, 0x8b, 0x4c);
    private static readonly Brush NodeBackgroundBrush = CreateBrush(0x18, 0x1f, 0x8b, 0x4c);
    private static readonly Brush NodeBorderBrush = CreateBrush(0x60, 0x1f, 0x8b, 0x4c);
    private static readonly Brush RustAccentBrush = CreateBrush(0xff, 0xb7, 0x5d, 0x1f);
    private static readonly Brush RustBackgroundBrush = CreateBrush(0x18, 0xb7, 0x5d, 0x1f);
    private static readonly Brush RustBorderBrush = CreateBrush(0x60, 0xb7, 0x5d, 0x1f);
    private static readonly Brush DefaultAccentBrush = CreateBrush(0xff, 0x2f, 0x6f, 0xd0);
    private static readonly Brush DefaultBackgroundBrush = CreateBrush(0x18, 0x2f, 0x6f, 0xd0);
    private static readonly Brush DefaultBorderBrush = CreateBrush(0x60, 0x2f, 0x6f, 0xd0);
    private static readonly Brush WhiteForegroundBrush = CreateBrush(0xff, 0xff, 0xff, 0xff);

    private bool _isSelected;
    private bool _isCleaning;
    private bool _includeDependencies;
    private string _resultText = string.Empty;

    public ProjectCleanItem(CleanableProject project, bool includeDependencies)
    {
        Project = project;
        _includeDependencies = includeDependencies;
    }

    public CleanableProject Project { get; private set; }

    public string Name => Project.Name;

    public string RootPath => Project.RootPath;

    public ProjectEcosystem Ecosystem => Project.Ecosystem;

    public string EcosystemLabel => ProjectCleanerService.EcosystemDisplayName(Project.Ecosystem);

    public string MarkerFile => Project.MarkerFile;

    public long BuildOutputBytes => Project.BuildOutputBytes;

    public long DependencyBytes => Project.DependencyBytes;

    public long ReclaimableBytes => Project.GetReclaimableBytes(_includeDependencies);

    public bool HasBuildOutput => BuildOutputBytes > 0;

    public bool HasDependencies => DependencyBytes > 0;

    public bool CanClean => !_isCleaning && ReclaimableBytes > 0;

    public bool IsCleaning
    {
        get => _isCleaning;
        set
        {
            if (SetProperty(ref _isCleaning, value))
            {
                RaisePropertyChanged(nameof(CanClean));
                RaisePropertyChanged(nameof(CleanButtonText));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            var clamped = value && CanClean;
            if (SetProperty(ref _isSelected, clamped))
            {
                SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public event EventHandler? SelectionChanged;

    public string CleanButtonText => _isCleaning ? "Cleaning..." : "Clean";

    public string SizeDisplay => CleanupRules.FormatBytes(ReclaimableBytes);

    public string BreakdownText
    {
        get
        {
            var targets = Project.GetTargetArtifacts(_includeDependencies);
            var top = targets.Take(5)
                .Select(a => $"{a.RelativePath} ({CleanupRules.FormatBytes(a.SizeBytes)})")
                .ToList();
            if (targets.Count > 5)
            {
                top.Add($"+{targets.Count - 5} more folder(s)");
            }

            if (!_includeDependencies && DependencyBytes > 0)
            {
                top.Add($"[kept deps: {CleanupRules.FormatBytes(DependencyBytes)}]");
            }

            return top.Count == 0
                ? "No cleanable build artifacts (enable dependencies toggle to include node_modules/.venv)."
                : string.Join(" - ", top);
        }
    }

    public string ResultText
    {
        get => _resultText;
        set
        {
            if (SetProperty(ref _resultText, value))
            {
                RaisePropertyChanged(nameof(HasResult));
            }
        }
    }

    public bool HasResult => !string.IsNullOrWhiteSpace(_resultText);

    public Brush AccentBrush => GetBrushSet().Accent;
    public Brush BackgroundBrush => GetBrushSet().Background;
    public Brush BorderBrush => GetBrushSet().Border;
    public Brush BadgeForegroundBrush => WhiteForegroundBrush;

    public void SetIncludeDependencies(bool includeDependencies)
    {
        if (_includeDependencies == includeDependencies)
        {
            return;
        }

        _includeDependencies = includeDependencies;
        if (!CanClean && _isSelected)
        {
            _isSelected = false;
            RaisePropertyChanged(nameof(IsSelected));
        }

        RaisePropertyChanged(nameof(ReclaimableBytes));
        RaisePropertyChanged(nameof(CanClean));
        RaisePropertyChanged(nameof(SizeDisplay));
        RaisePropertyChanged(nameof(BreakdownText));
    }

    private (Brush Background, Brush Border, Brush Accent) GetBrushSet() => Project.Ecosystem switch
    {
        ProjectEcosystem.DotNet => (DotNetBackgroundBrush, DotNetBorderBrush, DotNetAccentBrush),
        ProjectEcosystem.Node => (NodeBackgroundBrush, NodeBorderBrush, NodeAccentBrush),
        ProjectEcosystem.Rust => (RustBackgroundBrush, RustBorderBrush, RustAccentBrush),
        _ => (DefaultBackgroundBrush, DefaultBorderBrush, DefaultAccentBrush)
    };

    private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue) =>
        new(ColorHelper.FromArgb(alpha, red, green, blue));
}
