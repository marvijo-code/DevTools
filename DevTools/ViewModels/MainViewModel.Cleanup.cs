using System.Collections.ObjectModel;
using DevTools.Infrastructure;
using DevTools.Models;
using DevTools.Services;

namespace DevTools.ViewModels;

/// <summary>
/// The cleanup card in the Disk Usage tab: every cleanup DevTools can run on this machine, how much each
/// would free, and the one button that runs it.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly CleanupAdvisor _cleanupAdvisor;
    private readonly SemaphoreSlim _cleanupRefreshGate = new(1, 1);
    private string _cleanupSummary = "Cleanup options have not been checked yet.";
    private bool _isCleanupRefreshing;

    public ObservableCollection<CleanupOption> CleanupOptions { get; } = [];

    public string CleanupSummary => _cleanupSummary;

    public bool IsCleanupRefreshing => _isCleanupRefreshing;

    public bool CanRefreshCleanupOptions => !_isCleanupRefreshing;

    /// <summary>
    /// Re-checks every cleanup option. Each option's size fills in as soon as it is measured, then the list is
    /// ordered safe first and, within each verdict, biggest first.
    /// </summary>
    public async Task RefreshCleanupOptionsAsync(CancellationToken cancellationToken)
    {
        if (!await _cleanupRefreshGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            SetCleanupRefreshing(true);
            SetCleanupSummary("Checking what can be cleaned...");
            CleanupOptions.Clear();
            foreach (var option in _cleanupAdvisor.CreateOptions())
            {
                CleanupOptions.Add(option);
            }

            await Task.WhenAll(CleanupOptions.ToList().Select(async option =>
            {
                ApplyCleanupEstimate(option, await _cleanupAdvisor.EstimateAsync(option, cancellationToken));
            }));

            SortCleanupOptions();
            SetCleanupSummary(BuildCleanupSummary());
        }
        catch (OperationCanceledException)
        {
            SetCleanupSummary("Cleanup check canceled.");
        }
        catch (Exception ex)
        {
            AppLog.Error("RefreshCleanupOptionsAsync failed.", ex);
            SetCleanupSummary($"Cleanup check failed: {ex.Message}");
        }
        finally
        {
            SetCleanupRefreshing(false);
            _cleanupRefreshGate.Release();
        }
    }

    /// <summary>Runs one cleanup option, then reports what it did and how much the drive gained.</summary>
    public async Task RunCleanupOptionAsync(CleanupOption option, CancellationToken cancellationToken)
    {
        if (!option.CanRun)
        {
            return;
        }

        option.IsRunning = true;
        option.ResultText = "Working...";
        StatusMessage = $"Running cleanup: {option.Title}...";
        var driveRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        var freeBefore = new DriveInfo(driveRoot).AvailableFreeSpace;
        try
        {
            var result = await _cleanupAdvisor.RunAsync(option, cancellationToken);
            var drive = new DriveInfo(driveRoot);
            var gained = drive.AvailableFreeSpace - freeBefore;
            var gainedText = option.ActionKind == CleanupActionKind.Clean && gained > 0
                ? $" {drive.Name} has {CleanupRules.FormatBytes(gained)} more free space ({CleanupRules.FormatBytes(drive.AvailableFreeSpace)} free now)."
                : string.Empty;
            option.ResultText = $"{DateTime.Now:HH:mm}: {result}{gainedText}";
            StatusMessage = $"{option.Title}: {result}";

            if (option.ActionKind == CleanupActionKind.Clean)
            {
                ApplyCleanupEstimate(option, await _cleanupAdvisor.EstimateAsync(option, cancellationToken));
                SetCleanupSummary(BuildCleanupSummary());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error($"RunCleanupOptionAsync failed for {option.Key}.", ex);
            option.ResultText = $"Failed: {ex.Message}";
            StatusMessage = ex.Message;
        }
        finally
        {
            option.IsRunning = false;
        }
    }

    private void ApplyCleanupEstimate(CleanupOption option, CleanupEstimate estimate)
    {
        // The worktree option has no size of its own; when the Worktrees tab has already been scanned, show what
        // its merged worktrees add up to.
        if (option.Key == CleanupAdvisor.WorktreesKey && _hasWorktreeSnapshot)
        {
            var safe = _allWorktrees.Where(item => item.IsSafeToRemove).ToList();
            var bytes = safe.Sum(item => item.SizeBytes ?? 0);
            estimate = estimate with { SizeBytes = bytes, SizeText = $"{CleanupRules.FormatBytes(bytes)} in {safe.Count}" };
        }

        if (!string.IsNullOrWhiteSpace(estimate.Reason))
        {
            option.UpdateReason(estimate.Reason);
        }

        if (!string.IsNullOrWhiteSpace(estimate.WhatHappens))
        {
            option.UpdateWhatHappens(estimate.WhatHappens);
        }

        if (estimate.IsAvailable)
        {
            option.SetEstimate(estimate.SizeBytes, estimate.SizeText);
        }
        else
        {
            option.SetUnavailable(estimate.SizeText, option.Reason);
        }
    }

    private void SortCleanupOptions()
    {
        var ordered = CleanupOptions
            .OrderBy(option => option.Verdict)
            .ThenByDescending(option => option.IsAvailable)
            .ThenByDescending(option => option.SizeBytes ?? -1)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var currentIndex = CleanupOptions.IndexOf(ordered[i]);
            if (currentIndex != i)
            {
                CleanupOptions.Move(currentIndex, i);
            }
        }
    }

    private string BuildCleanupSummary()
    {
        long SumFor(CleanupVerdict verdict) => CleanupOptions
            .Where(option => option.Verdict == verdict && option.IsAvailable)
            .Sum(option => option.SizeBytes ?? 0);

        var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\");
        return $"{drive.Name} has {CleanupRules.FormatBytes(drive.AvailableFreeSpace)} free. " +
               $"Safe options: up to {CleanupRules.FormatBytes(SumFor(CleanupVerdict.Safe))}, no real downside. " +
               $"Careful: up to {CleanupRules.FormatBytes(SumFor(CleanupVerdict.Careful))}, read the note first. " +
               $"Your call: up to {CleanupRules.FormatBytes(SumFor(CleanupVerdict.YourCall))}. " +
               "Docker sizes free space inside Docker's disk until 'Shrink Docker disk' hands it back to Windows.";
    }

    private void SetCleanupRefreshing(bool isRefreshing)
    {
        if (SetProperty(ref _isCleanupRefreshing, isRefreshing, nameof(IsCleanupRefreshing)))
        {
            RaisePropertyChanged(nameof(CanRefreshCleanupOptions));
        }
    }

    private void SetCleanupSummary(string summary)
    {
        SetProperty(ref _cleanupSummary, summary, nameof(CleanupSummary));
    }
}