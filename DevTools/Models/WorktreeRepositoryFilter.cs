using DevTools.Infrastructure;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace DevTools.Models;

/// <summary>
/// One button in the Worktrees repository rail: a repository (or "All repositories") with what its worktrees
/// add up to. Selecting it narrows the list to that repository.
/// </summary>
public sealed class WorktreeRepositoryFilter : ObservableObject
{
    private static readonly Brush SelectedBackgroundBrush = CreateBrush(0xff, 0x2f, 0x6f, 0xd0);
    private static readonly Brush SelectedBorderBrush = CreateBrush(0xff, 0x6f, 0xa3, 0xf0);
    private static readonly Brush IdleBackgroundBrush = CreateBrush(0x14, 0x4b, 0x55, 0x67);
    private static readonly Brush IdleBorderBrush = CreateBrush(0x55, 0x4b, 0x55, 0x67);

    private int _count;
    private long _sizeBytes;
    private int _safeCount;
    private bool _isSelected;

    public WorktreeRepositoryFilter(string name, string repositoryPath)
    {
        Name = name;
        RepositoryPath = repositoryPath;
    }

    public string Name { get; }

    /// <summary>The main checkout this button stands for; empty for the "All repositories" button.</summary>
    public string RepositoryPath { get; }

    public bool IsAllRepositories => RepositoryPath.Length == 0;

    public long SizeBytes => _sizeBytes;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                RaisePropertyChanged(nameof(BackgroundBrush));
                RaisePropertyChanged(nameof(BorderBrush));
            }
        }
    }

    public string Summary =>
        _safeCount == 0
            ? $"{_count} worktree(s) · {WorktreeItem.FormatBytes(_sizeBytes)}"
            : $"{_count} worktree(s) · {WorktreeItem.FormatBytes(_sizeBytes)} · {_safeCount} safe";

    public Brush BackgroundBrush => _isSelected ? SelectedBackgroundBrush : IdleBackgroundBrush;

    public Brush BorderBrush => _isSelected ? SelectedBorderBrush : IdleBorderBrush;

    public void Update(int count, long sizeBytes, int safeCount)
    {
        if (_count == count && _sizeBytes == sizeBytes && _safeCount == safeCount)
        {
            return;
        }

        _count = count;
        _sizeBytes = sizeBytes;
        _safeCount = safeCount;
        RaisePropertyChanged(nameof(SizeBytes));
        RaisePropertyChanged(nameof(Summary));
    }

    private static SolidColorBrush CreateBrush(byte alpha, byte red, byte green, byte blue)
    {
        return new SolidColorBrush(ColorHelper.FromArgb(alpha, red, green, blue));
    }
}
