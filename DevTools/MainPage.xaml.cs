using System.ComponentModel;
using System.Diagnostics;
using DevTools.Models;
using DevTools.Infrastructure;
using DevTools.ViewModels;
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
    private readonly WindowZoomController _windowZoomController;
    private CancellationTokenSource? _selectionCts;

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
        ApplyRefreshTimerSettings();
        ApplyActivePaneState();
        RootLayoutGrid.KeyDown += RootLayoutGrid_KeyDown;

        Loaded += MainPage_Loaded;
        Unloaded += MainPage_Unloaded;
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        AppLog.Info("MainPage loaded.");
        _liveRunTimer.Start();
        _ = InitializePageAsync();
    }

    private void MainPage_Unloaded(object sender, RoutedEventArgs e)
    {
        AppLog.Info("MainPage unloaded.");
        _refreshTimer.Stop();
        _liveRunTimer.Stop();
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _selectionCts?.Cancel();
        _selectionCts?.Dispose();
    }

    private async void RefreshTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        await ViewModel.RefreshAsync(CancellationToken.None);
    }

    private void LiveRunTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        ViewModel.RefreshLiveRunMetrics();
        ViewModel.RefreshDiskUsageAnimation();
    }

    private async Task InitializePageAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        try
        {
            await ViewModel.InitializeAsync(cts.Token);
            // Tab data loads run unbounded — the 8s startup token is only for the auth/run check,
            // and cancelling a disk scan mid-flight leaves the tab stuck on "Canceled".
            if (ViewModel.IsKillProcessesVisible)
            {
                await ViewModel.EnsureKillProcessesLoadedAsync(CancellationToken.None);
            }
            else if (ViewModel.IsDiskUsageVisible)
            {
                await ViewModel.EnsureDiskUsageLoadedAsync(CancellationToken.None);
            }

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

    private async void KillProcessesTabButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowKillProcesses();
        ApplyActivePaneState();
        await ViewModel.EnsureKillProcessesLoadedAsync(CancellationToken.None);
    }

    private void WorkflowTabButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowWorkflowExplorer();
        ApplyActivePaneState();
    }

    private async void DiskTabButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowDiskUsage();
        ApplyActivePaneState();
        await ViewModel.EnsureDiskUsageLoadedAsync(CancellationToken.None);
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

    private void NavigateToDetails_Click(object sender, RoutedEventArgs e)
    {
        DetailsScroll.ChangeView(null, 0, null, true);
        DetailsScroll.Focus(FocusState.Programmatic);
    }

    private async void NavigateToDisk_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowDiskUsage();
        ApplyActivePaneState();
        DiskScroll.ChangeView(null, 0, null, true);
        DiskScroll.Focus(FocusState.Programmatic);
        await ViewModel.EnsureDiskUsageLoadedAsync(CancellationToken.None);
    }

    private async void RefreshKillProcesses_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RefreshKillProcessesAsync(CancellationToken.None);
    }

    private void ToggleProcessSection_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not ProcessKillSection section)
        {
            return;
        }

        section.ToggleExpanded();
    }

    private void ToggleDiskEntry_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not DiskUsageEntry entry)
        {
            return;
        }

        entry.ToggleExpanded();
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

        var dialog = new ContentDialog
        {
            Title = "Kill Process?",
            Content = $"Kill PID {processId} and all of its subprocesses?",
            PrimaryButtonText = "Kill",
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
        if (e.PropertyName is nameof(MainViewModel.AutoRefreshEnabled) or nameof(MainViewModel.RefreshIntervalSeconds))
        {
            ApplyRefreshTimerSettings();
        }

        if (e.PropertyName is nameof(MainViewModel.IsKillProcessesVisible) or
            nameof(MainViewModel.IsWorkflowExplorerVisible) or
            nameof(MainViewModel.IsDiskUsageVisible) or
            nameof(MainViewModel.ActiveTab))
        {
            ApplyActivePaneState();
        }

        if (e.PropertyName is nameof(MainViewModel.SelectedRun))
        {
            UpdateRunDetailsExpanders();
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

    private void ApplyActivePaneState()
    {
        var isKillVisible = ViewModel.IsKillProcessesVisible;
        var isWorkflowVisible = ViewModel.IsWorkflowExplorerVisible;
        var isDiskVisible = ViewModel.IsDiskUsageVisible;

        KillProcessesContentPane.Visibility = isKillVisible ? Visibility.Visible : Visibility.Collapsed;
        WorkflowContentPane.Visibility = isWorkflowVisible ? Visibility.Visible : Visibility.Collapsed;
        DiskContentPane.Visibility = isDiskVisible ? Visibility.Visible : Visibility.Collapsed;

        ApplyTabButtonState(KillProcessesTabButton, isKillVisible);
        ApplyTabButtonState(WorkflowTabButton, isWorkflowVisible);
        ApplyTabButtonState(DiskTabButton, isDiskVisible);
    }

    private static void ApplyTabButtonState(Button button, bool isActive)
    {
        button.Opacity = isActive ? 1.0 : 0.72;
        button.BorderThickness = isActive ? new Thickness(2) : new Thickness(1);
        button.FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

        if (Application.Current.Resources.TryGetValue(
                isActive ? "AccentFillColorDefaultBrush" : "ControlFillColorDefaultBrush",
                out var background) &&
            background is Brush backgroundBrush)
        {
            button.Background = backgroundBrush;
        }

        if (Application.Current.Resources.TryGetValue(
                isActive ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush",
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
