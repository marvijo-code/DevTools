using DevTools.Infrastructure;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace DevTools.Models;

/// <summary>How confident the advisor is that an option can be run without thinking twice.</summary>
public enum CleanupVerdict
{
    /// <summary>Only removes things that rebuild or re-download by themselves.</summary>
    Safe,

    /// <summary>Removes things you may need again, or that hold data; read the note first.</summary>
    Careful,

    /// <summary>Disruptive or personal; the advisor will not recommend it either way.</summary>
    YourCall
}

/// <summary>What happens when the option's button is pressed.</summary>
public enum CleanupActionKind
{
    /// <summary>Deletes or prunes in place and reports the space freed.</summary>
    Clean,

    /// <summary>Starts an elevated script in its own window; the space is freed when that window finishes.</summary>
    LaunchElevated,

    /// <summary>Opens a folder so the person can decide file by file.</summary>
    OpenFolder,

    /// <summary>Switches to another DevTools tab that already handles this cleanup.</summary>
    Navigate
}

/// <summary>One cleanup choice on the Storage tab, with the advisor's verdict and the space it should free.</summary>
public sealed class CleanupOption : ObservableObject
{
    // Same palette as the worktree cards, so green, amber, and blue mean the same thing on both tabs.
    private static readonly Brush GreenBackgroundBrush = CreateBrush(0x1c, 0x1f, 0x8b, 0x4c);
    private static readonly Brush GreenBorderBrush = CreateBrush(0x70, 0x1f, 0x8b, 0x4c);
    private static readonly Brush GreenAccentBrush = CreateBrush(0xff, 0x1f, 0x8b, 0x4c);
    private static readonly Brush AmberBackgroundBrush = CreateBrush(0x1c, 0xb7, 0x79, 0x1f);
    private static readonly Brush AmberBorderBrush = CreateBrush(0x70, 0xb7, 0x79, 0x1f);
    private static readonly Brush AmberAccentBrush = CreateBrush(0xff, 0xb7, 0x79, 0x1f);
    private static readonly Brush BlueBackgroundBrush = CreateBrush(0x1c, 0x2f, 0x6f, 0xd0);
    private static readonly Brush BlueBorderBrush = CreateBrush(0x70, 0x2f, 0x6f, 0xd0);
    private static readonly Brush BlueAccentBrush = CreateBrush(0xff, 0x2f, 0x6f, 0xd0);
    private static readonly Brush WhiteForegroundBrush = CreateBrush(0xff, 0xff, 0xff, 0xff);

    private long? _sizeBytes;
    private string _sizeText = "Measuring...";
    private string _resultText = string.Empty;
    private bool _isRunning;
    private bool _isAvailable = true;

    public CleanupOption(
        string key,
        string title,
        CleanupVerdict verdict,
        string reason,
        string whatHappens,
        string actionText,
        CleanupActionKind actionKind,
        string? targetPath = null)
    {
        Key = key;
        Title = title;
        Verdict = verdict;
        Reason = reason;
        WhatHappens = whatHappens;
        ActionText = actionText;
        ActionKind = actionKind;
        TargetPath = targetPath;
    }

    public string Key { get; }

    public string Title { get; }

    public CleanupVerdict Verdict { get; }

    public string VerdictText => Verdict switch
    {
        CleanupVerdict.Safe => "Safe",
        CleanupVerdict.Careful => "Careful",
        _ => "Your call"
    };

    /// <summary>Why the advisor gave this verdict, in plain words.</summary>
    public string Reason { get; private set; }

    /// <summary>Exactly what the button does, shown again in the confirmation dialog.</summary>
    public string WhatHappens { get; private set; }

    public string ActionText { get; }

    public CleanupActionKind ActionKind { get; }

    /// <summary>The folder the option works on, for options that open or clear one folder.</summary>
    public string? TargetPath { get; }

    public Brush VerdictBackgroundBrush => GetBrushSet().Background;

    public Brush VerdictBorderBrush => GetBrushSet().Border;

    public Brush VerdictAccentBrush => GetBrushSet().Accent;

    public Brush VerdictBadgeForegroundBrush => WhiteForegroundBrush;

    public long? SizeBytes
    {
        get => _sizeBytes;
        private set => SetProperty(ref _sizeBytes, value);
    }

    public string SizeText
    {
        get => _sizeText;
        private set => SetProperty(ref _sizeText, value);
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

    public bool HasResult => !string.IsNullOrWhiteSpace(ResultText);

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RaisePropertyChanged(nameof(CanRun));
            }
        }
    }

    /// <summary>False when the option has nothing to do here, for example Docker is not running.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (SetProperty(ref _isAvailable, value))
            {
                RaisePropertyChanged(nameof(CanRun));
            }
        }
    }

    public bool CanRun => IsAvailable && !IsRunning;

    public void SetEstimate(long? sizeBytes, string sizeText)
    {
        SizeBytes = sizeBytes;
        SizeText = sizeText;
    }

    public void SetUnavailable(string sizeText, string reason)
    {
        SizeBytes = null;
        SizeText = sizeText;
        UpdateReason(reason);
        IsAvailable = false;
    }

    public void UpdateReason(string reason)
    {
        Reason = reason;
        RaisePropertyChanged(nameof(Reason));
    }

    public void UpdateWhatHappens(string whatHappens)
    {
        WhatHappens = whatHappens;
        RaisePropertyChanged(nameof(WhatHappens));
    }

    private (Brush Background, Brush Border, Brush Accent) GetBrushSet()
    {
        return Verdict switch
        {
            CleanupVerdict.Safe => (GreenBackgroundBrush, GreenBorderBrush, GreenAccentBrush),
            CleanupVerdict.Careful => (AmberBackgroundBrush, AmberBorderBrush, AmberAccentBrush),
            _ => (BlueBackgroundBrush, BlueBorderBrush, BlueAccentBrush)
        };
    }

    private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
    {
        return new SolidColorBrush(ColorHelper.FromArgb(alpha, red, green, blue));
    }
}
