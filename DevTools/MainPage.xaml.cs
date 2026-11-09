using System.ComponentModel;
using System.Diagnostics;
using DevTools.Models;
using DevTools.Infrastructure;
using DevTools.ViewModels;
using DevTools.Services;
using DevTools.Services.DiskCleanup;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Launcher = Windows.System.Launcher;

namespace DevTools;

public sealed partial class MainPage : Page
{
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _refreshTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _liveRunTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _resourceTimer;
    private readonly WindowZoomController _windowZoomController;
    private CancellationTokenSource? _selectionCts;
    private bool _isConfirmationDialogOpen;

    public MainViewModel ViewModel { get; } = new();

    public MainPage()
    {
        this.InitializeComponent();
        DataContext = ViewModel;
        _windowZoomController = new WindowZoomController(RootLayoutGrid);
        RegisterZoomKeyboardAccelerators();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;

        _refreshTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _refreshTimer.Tick += RefreshTimer_Tick;
        _liveRunTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _liveRunTimer.Interval = TimeSpan.FromMilliseconds(500);
        _liveRunTimer.Tick += LiveRunTimer_Tick;
        _resourceTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _resourceTimer.Tick += ResourceTimer_Tick;
        ApplyRefreshTimerSettings();
        ApplyResourceTimerSettings();
        ApplyActivePaneState();
        RootLayoutGrid.KeyDown += RootLayoutGrid_KeyDown;

        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        AppLog.Info("MainPage loaded.");
        _liveRunTimer.Start();
        ApplyResourceTimerSettings();
        _ = InitializePageAsync();
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        AppLog.Info("MainPage unloaded.");
        _refreshTimer.Stop();
        _liveRunTimer.Stop();
        _resourceTimer.Stop();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
    }

    private async void RefreshTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        await ViewModel.RefreshOnTimerAsync(CancellationToken.None);
    }

    private void LiveRunTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        ViewModel.RefreshLiveRunMetrics();
        ViewModel.RefreshDiskUsageAnimation();
    }

    private async void ResourceTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        await ViewModel.SampleResourcesAsync();
    }

    private async Task InitializePageAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        try
        {
            // The GitHub auth check runs alongside the tab load instead of in front of it.
            var initialization = ViewModel.InitializeAsync(cts.Token);
            // Tab data loads run unbounded - the 8s startup token is only for the auth/run check,
            // and cancelling a disk scan mid-flight leaves the tab stuck on "Canceled".
            if (ViewModel.IsKillProcessesVisible)
            {
                await ViewModel.EnsureKillProcessesLoadedAsync(CancellationToken.None);
            }
            else if (ViewModel.IsDiskUsageVisible)
            {
                await ViewModel.EnsureDiskUsageLoadedAsync(CancellationToken.None);
            }
            else if (ViewModel.IsOpenWorkVisible)
            {
                await ViewModel.EnsureOpenWorkLoadedAsync(CancellationToken.None);
            }
            else if (ViewModel.IsWorktreesVisible)
            {
                await ViewModel.EnsureWorktreesLoadedAsync(CancellationToken.None);
            }

            await initialization;
            ApplyRefreshTimerSettings();
            ApplyActivePaneState();
            AppLog.Info("InitializePageAsync completed.");
        }
        catch (OperationCanceledException)
        {
            ViewModel.AuthMessage = "GitHub auth check timed out. Click Reload Auth.";
            ViewModel.StatusMessage = "Startup timed out while checking GitHub auth.";
            AppLog.Info("InitializePageAsync timed out.");
        }
        catch (Exception ex)
        {
            AppLog.Error("InitializePageAsync failed.", ex);
            ViewModel.StatusMessage = ex.Message;
        }
    }

    private async void RefreshNow_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info("Refresh Now clicked.");
        await ViewModel.RefreshAsync(CancellationToken.None);

        if (ViewModel.IsKillProcessesVisible)
        {
            await ViewModel.RefreshKillProcessesAsync(CancellationToken.None);
        }
        else if (ViewModel.IsDiskUsageVisible)
        {
            await ViewModel.RefreshDiskUsageAsync(CancellationToken.None);
        }
        else if (ViewModel.IsOpenWorkVisible)
        {
            await ViewModel.RefreshOpenWorkAsync(CancellationToken.None);
        }
        else if (ViewModel.IsWorktreesVisible)
        {
            await ViewModel.RefreshWorktreesAsync(CancellationToken.None);
        }
    }

    private void GhLogin_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info("Sign In With gh clicked.");
        ViewModel.LaunchGhLogin();
    }

    private async void ReloadAuth_Click(object sender, RoutedEventArgs e)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        try
        {
            AppLog.Info("Reload Auth clicked.");
            await ViewModel.ReloadAuthAsync(cts.Token);
            await ViewModel.RefreshAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            ViewModel.AuthMessage = "GitHub auth check timed out. Try Sign In With gh.";
            ViewModel.StatusMessage = "Reload Auth timed out.";
            AppLog.Info("Reload Auth timed out.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Reload Auth failed.", ex);
            ViewModel.StatusMessage = ex.Message;
        }
    }

    private async void AddRepository_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info("Add Repository clicked.");
        if (!ViewModel.AddRepository())
        {
            return;
        }

        await ViewModel.RefreshAsync(CancellationToken.None);
    }

    private async void RemoveRepository_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info("Remove Repository clicked.");
        if (sender is not FrameworkElement element || element.Tag is not RepositoryDefinition repository)
        {
            return;
        }

        if (ViewModel.RemoveRepository(repository))
        {
            await ViewModel.RefreshAsync(CancellationToken.None);
        }
    }

    private async void RunsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView list) ViewModel.SelectedRun = list.SelectedItem as WorkflowRunItem;
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
        _selectionCts = new CancellationTokenSource();

        try
        {
            AppLog.Info("RunsList selection changed.");
            UpdateRunDetailsExpanders();
            await ViewModel.ResolveSelectedRunAsync(_selectionCts.Token);
            UpdateRunDetailsExpanders();
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("RunsList selection change canceled.");
        }
        catch (Exception ex)
        {
            AppLog.Error("RunsList selection handling failed.", ex);
            ViewModel.StatusMessage = ex.Message;
        }
    }

    private async void UsePat_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.ApplyPersonalAccessToken(PatInputBox.Text))
        {
            return;
        }

        PatInputBox.Text = string.Empty;
        await ViewModel.RefreshAsync(CancellationToken.None);
    }

    private async void ClearPat_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearPersonalAccessToken();
        PatInputBox.Text = string.Empty;
        await ViewModel.RefreshAsync(CancellationToken.None);
    }

    private async void UseGhToken_Click(object sender, RoutedEventArgs e)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        try
        {
            await ViewModel.UseGhTokenAsync(cts.Token);
            await ViewModel.RefreshAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            ViewModel.StatusMessage = "Using gh token timed out.";
            AppLog.Info("Use gh token timed out.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Use gh token failed.", ex);
            ViewModel.StatusMessage = ex.Message;
        }
    }

    private async void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string url || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            await Launcher.LaunchUriAsync(uri);
            AppLog.Info($"Opened link: {url}");
        }
    }

    private async void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        AppLog.Info($"Open Logs clicked. Folder={AppLog.LogDirectory}");
        await Launcher.LaunchFolderPathAsync(AppLog.LogDirectory);
    }

    private void NavigateToResources_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowResources();
        ApplyActivePaneState();
        ResourcesScroll.ChangeView(null, 0, null, true);
        ResourcesScroll.Focus(FocusState.Programmatic);
    }

    private void ClearResourceHistory_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetResourceHistory();
    }

    private void ToggleAgentSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not AgentSessionEntry session)
        {
            return;
        }

        session.ToggleExpanded();
    }

    private async void NavigateToKillProcesses_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowKillProcesses();
        ApplyActivePaneState();
        ProcessFilterBox.Focus(FocusState.Programmatic);
        KillProcessesScroll.ChangeView(null, 0, null, true);
        KillProcessesScroll.Focus(FocusState.Programmatic);
        await ViewModel.EnsureKillProcessesLoadedAsync(CancellationToken.None);
    }

    private void NavigateToRuns_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowWorkflowExplorer();
        ApplyActivePaneState();
        RunFilterBox.Focus(FocusState.Programmatic);
    }

    private async void NavigateToDisk_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowDiskUsage();
        ApplyActivePaneState();
        DiskScroll.ChangeView(null, 0, null, true);
        DiskScroll.Focus(FocusState.Programmatic);
        await ViewModel.EnsureDiskUsageLoadedAsync(CancellationToken.None);
    }

    private async void NavigateToOpenWork_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowOpenWork();
        ApplyActivePaneState();
        OpenWorkScroll.ChangeView(null, 0, null, true);
        OpenWorkScroll.Focus(FocusState.Programmatic);
        await ViewModel.EnsureOpenWorkLoadedAsync(CancellationToken.None);
    }

    private async void NavigateToWorktrees_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowWorktrees();
        ApplyActivePaneState();
        WorktreesScroll.ChangeView(null, 0, null, true);
        WorktreesScroll.Focus(FocusState.Programmatic);
        await ViewModel.EnsureWorktreesLoadedAsync(CancellationToken.None);
    }

    private async void RefreshOpenWork_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshOpenWorkAsync(CancellationToken.None);
    }

    private async void RefreshCleanupOptions_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshCleanupOptionsAsync(CancellationToken.None);
    }

    private async void RunCleanupOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not CleanupOption option || !option.CanRun)
        {
            return;
        }

        switch (option.ActionKind)
        {
            case CleanupActionKind.Navigate:
                ViewModel.WorktreeFilter = "Safe to remove";
                ViewModel.ShowWorktrees();
                ApplyActivePaneState();
                WorktreesScroll.ChangeView(null, 0, null, true);
                await ViewModel.EnsureWorktreesLoadedAsync(CancellationToken.None);
                return;

            case CleanupActionKind.OpenFolder:
                if (option.TargetPath is { } folder && Directory.Exists(folder))
                {
                    Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
                }

                return;
        }

        // Every delete is confirmed, with the verdict and exactly what will happen, even for safe options.
        var result = await ShowConfirmationDialogAsync(() => new ContentDialog
        {
            Title = $"{option.Title}?",
            Content = $"{option.WhatHappens}\n\nVerdict: {option.VerdictText}. {option.Reason}\n\nExpected: {option.SizeText}",
            PrimaryButtonText = option.ActionText,
            CloseButtonText = "Cancel",
            DefaultButton = option.Verdict == CleanupVerdict.Safe ? ContentDialogButton.Primary : ContentDialogButton.Close,
            XamlRoot = XamlRoot
        });

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.RunCleanupOptionAsync(option, CancellationToken.None);
    }

    private async void RefreshWorktrees_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshWorktreesAsync(CancellationToken.None);
    }

    private async void PruneWorktrees_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.PruneStaleWorktreesAsync(CancellationToken.None);
    }

    private void WorktreeRepository_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WorktreeRepositoryFilter repository })
        {
            ViewModel.SelectWorktreeRepository(repository);
            WorktreesScroll.ChangeView(null, 0, null, true);
        }
    }

    private async void RemoveWorktree_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not WorktreeItem item || !item.CanRemove)
        {
            return;
        }

        // The default button is always Cancel, and a row with uncommitted files names the loss on its own button,
        // so a stray Enter can never delete work.
        var result = await ShowConfirmationDialogAsync(() => new ContentDialog
        {
            Title = item.IsMissing ? "Prune Worktree Record?" : item.IsDirty ? "Force Remove Worktree?" : "Remove Worktree?",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = item.BuildRemovalWarning(),
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                }
            },
            PrimaryButtonText = item.IsDirty ? "Discard Changes and Remove" : item.RemoveButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        });

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        AppLog.Info($"Remove worktree confirmed: {item.Path}");
        await ViewModel.RemoveWorktreeAsync(item, CancellationToken.None);
    }

    private async Task<ContentDialogResult> ShowConfirmationDialogAsync(Func<ContentDialog> createDialog)
    {
        // WinUI throws if another dialog is open, including a second click on the same row.
        // All cleanup confirmations share this guard until the active dialog has closed.
        if (_isConfirmationDialogOpen)
        {
            AppLog.Info("Confirmation skipped because a dialog is already open.");
            return ContentDialogResult.None;
        }

        _isConfirmationDialogOpen = true;
        try
        {
            var dialog = createDialog();
            AppLog.Info($"Opening confirmation: {dialog.Title}");
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Confirmation dialog failed.", ex);
            ViewModel.StatusMessage = $"Could not open confirmation. Nothing was removed. {ex.Message}";
            return ContentDialogResult.None;
        }
        finally
        {
            _isConfirmationDialogOpen = false;
        }
    }

    private async void RefreshKillProcesses_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshKillProcessesAsync(CancellationToken.None);
    }

    private void OpenSystemInformer_Click(object sender, RoutedEventArgs e) => OpenInspector(null);

    private async void ConnectFirefox_Click(object sender, RoutedEventArgs e)
    {
        var port = new NumberBox { Header = "Local Firefox port", Value = 63419, Minimum = 1024, Maximum = 65535 };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = "Connect to Firefox's local developer server to show tab names beside process memory. Enable the developer server in Firefox first. Private and unloaded tabs are excluded.",
            TextWrapping = TextWrapping.Wrap, MaxWidth = 460
        });
        content.Children.Add(port);
        content.Children.Add(new TextBlock { Text = FirefoxTabBridge.Status, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        var dialog = new ContentDialog
        {
            Title = "Connect Firefox tabs", Content = content,
            PrimaryButtonText = "Connect", CloseButtonText = "Cancel", XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await FirefoxTabBridge.ConnectAsync((int)port.Value);
            await ViewModel.RefreshKillProcessesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Error("Firefox connection failed.", ex);
            await new ContentDialog
            {
                Title = "Firefox connection", Content = FirefoxTabBridge.Status,
                CloseButtonText = "Close", XamlRoot = XamlRoot
            }.ShowAsync();
        }
    }

    private void InspectProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: int processId } && processId > 0)
            OpenInspector(processId);
    }

    private async void OpenInspector(int? processId)
    {
        try
        {
            new SystemInformerIntegration().Open(processId);
            AppLog.Info($"Opened official System Informer. ProcessId={processId}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not open System Informer.", ex);
            await new ContentDialog
            {
                Title = "System Informer", Content = ex.Message,
                CloseButtonText = "Close", XamlRoot = XamlRoot
            }.ShowAsync();
        }
    }

    private void SortProcesses_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is string tag &&
            Enum.TryParse<ProcessSortColumn>(tag, out var column))
            ViewModel.SortProcesses(column);
    }

    private void SelectActivity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AppActivityEntry activity }) ViewModel.SelectedActivity = activity;
    }

    private async void EndActivityProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AppActivityEntry activity } || !activity.CanEndProcess) return;
        ViewModel.SelectedActivity = activity;
        var processId = activity.ProcessId!.Value;
        DateTime? processStartedAt = null;
        async Task<bool> StillMatchesAsync()
        {
            if (activity.ProcessId != processId) return false;
            var titles = await FirefoxTabBridge.ReadProcessTitlesAsync();
            if (!titles.TryGetValue(processId, out var names) ||
                !names.Split(Environment.NewLine).Contains(activity.Title, StringComparer.Ordinal)) return false;
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.ProcessName.Equals("firefox", StringComparison.OrdinalIgnoreCase)) return false;
                var startedAt = process.StartTime;
                if (processStartedAt is not null && processStartedAt != startedAt) return false;
                processStartedAt = startedAt;
                return true;
            }
            catch (ArgumentException) { return false; }
        }
        try
        {
            if (!await StillMatchesAsync())
            {
                await new ContentDialog { Title = "Tab process changed", Content = "Refresh or reconnect Firefox before ending this process.",
                    CloseButtonText = "Close", XamlRoot = XamlRoot }.ShowAsync();
                return;
            }
            var dialog = new ContentDialog
            {
                Title = "End shared Firefox process?",
                Content = $"End PID {processId} for '{activity.Title}'? Other tabs sharing this process will also stop. If this is Firefox's main process, Firefox will close.",
                PrimaryButtonText = "End process", CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (!await StillMatchesAsync()) { ViewModel.StatusMessage = "The tab process changed. Nothing was ended."; return; }
            await ViewModel.KillProcessAsync(processId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not end the Firefox tab process.", ex);
            ViewModel.StatusMessage = "Could not end the tab process. Refresh Firefox and try again.";
        }
    }

    private void SelectProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProcessKillEntry entry }) ViewModel.SelectedProcess = entry;
    }

    private void ProcessActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProcessKillEntry entry } anchor) return;
        ViewModel.SelectedProcess = entry;
        var menu = new MenuFlyout();
        var details = new MenuFlyoutItem { Text = "Show details", Tag = entry };
        details.Click += SelectProcess_Click;
        var inspect = new MenuFlyoutItem { Text = "Inspect in System Informer", Tag = entry.ProcessId, IsEnabled = entry.CanKill };
        inspect.Click += InspectProcess_Click;
        var copy = new MenuFlyoutItem { Text = "Copy name, PID and full path", Tag = entry };
        copy.Click += CopyProcessInfo_Click;
        var end = new MenuFlyoutItem { Text = "End process...", Tag = entry.ProcessId, IsEnabled = entry.CanKill };
        end.Click += KillProcess_Click;
        menu.Items.Add(details);
        menu.Items.Add(inspect);
        menu.Items.Add(copy);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(end);
        menu.ShowAt(anchor);
    }

    private void ProcessGroupActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ProcessKillSection section } anchor) return;
        var menu = new MenuFlyout();
        var end = new MenuFlyoutItem { Text = "End all processes...", Tag = section,
            IsEnabled = section.Entries.Any(entry => entry.CanKill) };
        end.Click += KillProcessSection_Click;
        menu.Items.Add(end);
        menu.ShowAt(anchor);
    }

    private void ToggleProcessSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not ProcessKillSection section)
        {
            return;
        }

        ViewModel.ToggleProcessSection(section);
    }

    private async void ToggleDiskEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not DiskUsageEntry entry)
        {
            return;
        }

        if (!entry.IsExpanded && entry.CanExpand && !entry.HasLoadedChildren)
        {
            await ViewModel.EnsureDiskUsageChildrenLoadedAsync(entry, CancellationToken.None);
        }

        entry.ToggleExpanded();
    }

    private void BrowseDrive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string root })
        {
            _ = ViewModel.BrowseFolderAsync(root);
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            _ = ViewModel.BrowseFolderAsync(path);
        }
    }

    private void ToggleFolderRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path })
        {
            ViewModel.ToggleFolderRow(path);
        }
    }

    private void BrowseUp_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BrowseFolderUp();
    }

    private void CopyProcessInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not ProcessKillEntry entry)
        {
            return;
        }

        var dataPackage = new DataPackage();
        dataPackage.SetText(entry.CopyText);
        Clipboard.SetContent(dataPackage);
        ViewModel.StatusMessage = $"Copied process details for PID {entry.ProcessId}.";
        AppLog.Info($"Copied process details for PID {entry.ProcessId}.");
    }

    private void RegisterZoomKeyboardAccelerators()
    {
        RegisterZoomAccelerator(VirtualKey.Add, VirtualKeyModifiers.Control, zoomIn: true);
        RegisterZoomAccelerator(VirtualKey.Subtract, VirtualKeyModifiers.Control, zoomIn: false);
    }

    private void RootLayoutGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var controlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (!controlState.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            return;
        }

        switch (e.Key)
        {
            case (VirtualKey)187:
            case (VirtualKey)107:
                ApplyWindowZoom(zoomIn: true);
                e.Handled = true;
                break;
            case (VirtualKey)189:
            case (VirtualKey)109:
                ApplyWindowZoom(zoomIn: false);
                e.Handled = true;
                break;
        }
    }

    private void ApplyWindowZoom(bool zoomIn)
    {
        if (zoomIn)
        {
            _windowZoomController.Increase();
        }
        else
        {
            _windowZoomController.Decrease();
        }

        ViewModel.StatusMessage = $"Window zoom: {_windowZoomController.Scale:P0}";
    }

    private void RegisterZoomAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, bool zoomIn)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers
        };
        accelerator.Invoked += (_, args) =>
        {
            ApplyWindowZoom(zoomIn);
            args.Handled = true;
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private async void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not int processId || processId <= 0)
        {
            return;
        }

        var entry = ViewModel.KillProcessSections.SelectMany(section => section.Entries).FirstOrDefault(item => item.ProcessId == processId);
        if (entry is not null) ViewModel.SelectedProcess = entry;
        var name = entry?.DisplayName ?? "Process";
        var dialog = new ContentDialog
        {
            Title = "End process?",
            Content = $"End {name} (PID {processId}) and all of its subprocesses?",
            PrimaryButtonText = "End process",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.KillProcessAsync(processId, CancellationToken.None);
    }

    private async void KillProcessSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not ProcessKillSection section)
        {
            return;
        }

        var killableCount = section.Entries.Count(entry => entry.CanKill);
        if (killableCount == 0)
        {
            ViewModel.StatusMessage = $"No killable processes in {section.Title}.";
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Kill Process Group?",
            Content = $"Kill all {killableCount} process(es) in {section.Title} and their subprocess trees?",
            PrimaryButtonText = "Kill All",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.KillProcessSectionAsync(section, CancellationToken.None);
    }

    private async void RefreshDiskUsage_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshDiskUsageAsync(CancellationToken.None);
    }

    private async void StartBiggestFilesScan_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.StartBiggestFilesScanAsync();
    }

    private void CancelBiggestFilesScan_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelBiggestFilesScan();
    }

    /// <summary>
    /// Bulk Cleanup measures first, shows what would go, and only deletes after the user
    /// confirms. The measured pass deletes nothing.
    /// </summary>
    private async void BulkCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBulkCleanupRunning)
        {
            ViewModel.StatusMessage = "A bulk cleanup is already running.";
            return;
        }

        if (!ViewModel.IsBulkCleanupAvailable)
        {
            ViewModel.StatusMessage = ViewModel.BulkCleanupAvailabilityText;
            return;
        }

        var preview = await ViewModel.PreviewBulkCleanupAsync();
        if (!preview.Started)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = preview.Succeeded ? "Run the cleanup for real?" : "Cleanup preview failed",
            Content = BuildBulkCleanupDialogText(preview),
            PrimaryButtonText = preview.Succeeded ? "Clean Up Now" : null,
            CloseButtonText = preview.Succeeded ? "Cancel" : "Close",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (!preview.Succeeded)
        {
            await dialog.ShowAsync();
            return;
        }

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            ViewModel.StatusMessage = "Bulk cleanup canceled. Nothing was deleted.";
            return;
        }

        await ViewModel.RunBulkCleanupAsync();
    }

    private void CancelBulkCleanup_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.CancelBulkCleanup();
    }

    private static string BuildBulkCleanupDialogText(DiskCleanupResult preview)
    {
        if (!preview.Succeeded)
        {
            return $"{preview.Message}\n\nOpen the Cleanup report in the Storage pane for the full output.";
        }

        var lines = new List<string>
        {
            "A dry run already measured this machine. These lanes would be deleted:",
            string.Empty
        };

        foreach (var lane in preview.Report.Lanes)
        {
            var items = lane.ItemCount is null ? string.Empty : $" ({lane.ItemCount} items)";
            lines.Add($"  {DiskCleanupService.FormatSize(lane.Bytes),10}  {lane.Lane}{items}");
        }

        lines.Add(string.Empty);
        lines.Add($"Total reclaimable: {DiskCleanupService.FormatSize(preview.Report.TotalReclaimableBytes)}");
        lines.Add(string.Empty);
        lines.Add("Kept: every video master, the release folder, episode.json, and the top-level files of a published episode.");
        lines.Add("Deleted: package caches, build output in stale checkouts, and the intermediate folders of already-published video episodes (work, runs, node_modules, video, captures, audio, slides, review, edit, ledgers, prompts, scripts, transcript, verification, toolkit, vps-setup, shorts).");
        lines.Add(string.Empty);
        lines.Add("This cannot be undone. Caches and build output come back on the next install or build.");
        return string.Join(Environment.NewLine, lines);
    }

    private void SetBiggestFilesRootSystem_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BiggestFilesScanRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
    }

    private void SetBiggestFilesRootDev_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.BiggestFilesScanRoot = @"C:\dev";
    }

    private async void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsBiggestFilesScanning)
        {
            ViewModel.StatusMessage = "Cancel the running scan before restarting as admin.";
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Restart as Administrator?",
            Content = "DevTools will close and relaunch with administrator rights so it can read the NTFS Master File Table directly. A Windows UAC prompt will appear.",
            PrimaryButtonText = "Restart",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (ViewModel.RequestRestartAsAdmin())
        {
            Application.Current.Exit();
        }
    }

    private void RevealBiggestFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string rawPath || string.IsNullOrWhiteSpace(rawPath))
        {
            return;
        }

        if (File.Exists(rawPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{rawPath}\"",
                UseShellExecute = true
            });
            AppLog.Info($"Revealed file in Explorer: {rawPath}");
            return;
        }

        ViewModel.StatusMessage = $"File no longer exists: {rawPath}";
    }

    private void CopyBiggestFilePath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not BiggestFileEntry entry)
        {
            return;
        }

        var dataPackage = new DataPackage();
        dataPackage.SetText(entry.CopyText);
        Clipboard.SetContent(dataPackage);
        ViewModel.StatusMessage = $"Copied path: {entry.Path}";
    }

    private async void DeleteBiggestFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not BiggestFileEntry entry)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete File?",
            Content = $"Permanently delete this file?\n\n{entry.Path}\n\nSize: {entry.Usage}",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.DeleteBiggestFileAsync(entry, CancellationToken.None);
    }

    private void NavigateToRepos_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowWorkflowExplorer();
        ApplyActivePaneState();
        RepositoryExpander.IsExpanded = true;
        OwnerInputBox.Focus(FocusState.Programmatic);
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ProcessFilterText))
            KillProcessesScroll.ChangeView(null, 0, null, true);

        if (e.PropertyName is nameof(MainViewModel.AutoRefreshEnabled) or nameof(MainViewModel.RefreshIntervalSeconds))
        {
            ApplyRefreshTimerSettings();
        }

        if (e.PropertyName is nameof(MainViewModel.ResourceIntervalSeconds) or
            nameof(MainViewModel.ResourceMonitoringEnabled))
        {
            ApplyResourceTimerSettings();
        }

        if (e.PropertyName is nameof(MainViewModel.IsKillProcessesVisible) or
            nameof(MainViewModel.IsWorkflowExplorerVisible) or
            nameof(MainViewModel.IsDiskUsageVisible) or
            nameof(MainViewModel.IsOpenWorkVisible) or
            nameof(MainViewModel.IsWorktreesVisible) or
            nameof(MainViewModel.ActiveTab))
        {
            ApplyActivePaneState();
        }

        if (e.PropertyName is nameof(MainViewModel.SelectedRun))
        {
            UpdateRunDetailsExpanders();
            RunStatusBadge.Visibility = ViewModel.IsWorkflowExplorerVisible && ViewModel.HasSelectedRun ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ApplyRefreshTimerSettings()
    {
        _refreshTimer.Stop();
        _refreshTimer.Interval = TimeSpan.FromSeconds(ViewModel.RefreshIntervalSeconds);

        if (ViewModel.AutoRefreshEnabled)
        {
            _refreshTimer.Start();
            AppLog.Info($"Refresh timer active. Interval={ViewModel.RefreshIntervalSeconds}s");
            return;
        }

        AppLog.Info("Refresh timer paused.");
    }

    /// <summary>
    /// Sampling runs regardless of which tab is showing so the history is already populated when the user
    /// switches to Resources after noticing the machine struggling.
    /// </summary>
    private void ApplyResourceTimerSettings()
    {
        _resourceTimer.Stop();

        if (!ViewModel.ResourceMonitoringEnabled)
        {
            AppLog.Info("Resource monitor paused.");
            return;
        }

        _resourceTimer.Interval = TimeSpan.FromSeconds(ViewModel.ResourceIntervalSeconds);
        _resourceTimer.Start();
        AppLog.Info($"Resource monitor active. Interval={ViewModel.ResourceIntervalSeconds}s");
    }

    private void ApplyActivePaneState()
    {
        var context = WorkspaceContext.For(ViewModel.ActiveTab);
        var isKillVisible = context.ShowProcesses;
        var isWorkflowVisible = context.ShowRuns;
        var isDiskVisible = context.ShowStorage;
        var isResourcesVisible = context.ShowSystem;
        var isOpenWorkVisible = context.ShowOpenWork;
        var isWorktreesVisible = context.ShowWorktrees;

        KillProcessesContentPane.Visibility = isKillVisible ? Visibility.Visible : Visibility.Collapsed;
        WorkflowContentPane.Visibility = isWorkflowVisible ? Visibility.Visible : Visibility.Collapsed;
        DiskContentPane.Visibility = isDiskVisible ? Visibility.Visible : Visibility.Collapsed;
        ResourcesContentPane.Visibility = isResourcesVisible ? Visibility.Visible : Visibility.Collapsed;
        OpenWorkContentPane.Visibility = isOpenWorkVisible ? Visibility.Visible : Visibility.Collapsed;
        WorktreesContentPane.Visibility = isWorktreesVisible ? Visibility.Visible : Visibility.Collapsed;

        RunDetailsBorder.Visibility = Visibility.Visible;
        Grid.SetColumnSpan(MainContentBorder, 1);
        DetailsScroll.Visibility = isWorkflowVisible ? Visibility.Visible : Visibility.Collapsed;
        ProcessDetailsScroll.Visibility = isKillVisible ? Visibility.Visible : Visibility.Collapsed;
        StorageOverviewScroll.Visibility = isDiskVisible ? Visibility.Visible : Visibility.Collapsed;
        SystemOverviewScroll.Visibility = isResourcesVisible ? Visibility.Visible : Visibility.Collapsed;
        RunStatusBadge.Visibility = isWorkflowVisible && ViewModel.HasSelectedRun ? Visibility.Visible : Visibility.Collapsed;

        ApplyTabButtonState(ResourcesNavigationButton, isResourcesVisible);
        ApplyTabButtonState(ProcessesNavigationButton, isKillVisible);
        ApplyTabButtonState(RunsNavigationButton, isWorkflowVisible);
        ApplyTabButtonState(StorageNavigationButton, isDiskVisible);
        ApplyTabButtonState(OpenWorkNavigationButton, isOpenWorkVisible);
        ApplyTabButtonState(WorktreesNavigationButton, isWorktreesVisible);
    }

    private static void ApplyTabButtonState(Button button, bool isActive)
    {
        button.Opacity = isActive ? 1.0 : 0.72;
        button.BorderThickness = isActive ? new Thickness(2) : new Thickness(1);
        button.FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        if (Application.Current.Resources.TryGetValue(
                isActive ? "ControlFillColorSecondaryBrush" : "ControlFillColorTransparentBrush",
                out var background) &&
            background is Brush backgroundBrush)
        {
            button.Background = backgroundBrush;
        }

        if (Application.Current.Resources.TryGetValue(
                "TextFillColorPrimaryBrush",
                out var foreground) &&
            foreground is Brush foregroundBrush)
        {
            button.Foreground = foregroundBrush;
            return;
        }

        button.Foreground = new SolidColorBrush(
            isActive
                ? Microsoft.UI.Colors.White
                : Microsoft.UI.Colors.Black);
    }

    private void UpdateRunDetailsExpanders()
    {
        var selectedRun = ViewModel.SelectedRun;
        if (selectedRun is null)
        {
            TriggerDetailsExpander.IsExpanded = false;
            PullRequestDescriptionExpander.IsExpanded = false;
            return;
        }

        TriggerDetailsExpander.IsExpanded = selectedRun.HasTriggerDetails;
        PullRequestDescriptionExpander.IsExpanded = selectedRun.HasRunDescription;
    }

    private void OpenPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string rawPath || string.IsNullOrWhiteSpace(rawPath))
        {
            return;
        }

        var targetPath = FindExistingPath(rawPath);
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            ViewModel.StatusMessage = $"Path could not be resolved: {rawPath}";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = true
        });

        AppLog.Info($"Opened path: {targetPath}");
    }

    private async void ClearFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string rawPath || string.IsNullOrWhiteSpace(rawPath))
        {
            return;
        }

        var targetPath = FindExistingPath(rawPath);
        if (string.IsNullOrWhiteSpace(targetPath) || !Directory.Exists(targetPath))
        {
            ViewModel.StatusMessage = $"Folder could not be resolved: {rawPath}";
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Clear Folder Contents?",
            Content = $"Delete everything inside this folder and keep the folder itself?\n\n{targetPath}",
            PrimaryButtonText = "Clear Folder",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await Task.Run(() => ClearDirectoryContents(targetPath));
            ViewModel.StatusMessage = $"Cleared folder contents: {targetPath}";

            if (ViewModel.IsDiskUsageVisible)
            {
                await ViewModel.RefreshDiskUsageAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error($"ClearFolder_Click failed for {targetPath}.", ex);
            ViewModel.StatusMessage = ex.Message;
        }
    }

    private static string? FindExistingPath(string rawPath)
    {
        var candidate = rawPath.Trim().Trim('"');
        while (!string.IsNullOrWhiteSpace(candidate))
        {
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }

            candidate = Path.GetDirectoryName(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return null;
    }

    private static void ClearDirectoryContents(string directoryPath)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            if (Directory.Exists(entry))
            {
                ResetAttributes(entry);
                Directory.Delete(entry, true);
                continue;
            }

            ResetAttributes(entry);
            File.Delete(entry);
        }
    }

    private static void ResetAttributes(string path)
    {
        try
        {
            File.SetAttributes(path, System.IO.FileAttributes.Normal);
        }
        catch
        {
        }
    }
}
