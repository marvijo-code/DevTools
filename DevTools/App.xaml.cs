using System;
using System.Diagnostics;
using DevTools.Infrastructure;
using DevTools.Models;
using DevTools.ViewModels;
using Uno.Resizetizer;

namespace DevTools;

public partial class App : Application
{
    private Task<AppSettings?>? _startupSettingsRead;

    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        StartupTimestamp = Stopwatch.GetTimestamp();
        // Start the read before XAML initialization. The page needs its saved tab before layout.
        _startupSettingsRead = Task.Run(MainViewModel.ReadSettings);
        try { ShellIdentity.ConfigureProcess(); }
        catch (Exception ex) { AppLog.Error("Setting Windows app identity failed.", ex); }
        this.InitializeComponent();
        AppLog.Info("App constructed.");
    }

    internal static long StartupTimestamp { get; private set; }

    internal Task<AppSettings?>? TakeStartupSettingsRead() => Interlocked.Exchange(ref _startupSettingsRead, null);

    protected Window? MainWindow { get; private set; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Info($"OnLaunched called. Arguments='{args.Arguments ?? string.Empty}'");
        MainWindow = new Window();
#if DEBUG
        MainWindow.UseStudio();
#endif

        try
        {
            MainWindow.Content = new MainPage();
            AppLog.Info("MainPage attached to the window.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Creating MainPage failed.", ex);
            throw;
        }

        MainWindow.SetWindowIcon();
        try { ShellIdentity.ConfigureWindow(WinRT.Interop.WindowNative.GetWindowHandle(MainWindow)); }
        catch (Exception ex) { AppLog.Error("Setting Windows taskbar identity failed.", ex); }
        // --minimized: open on the taskbar without taking focus (background starts and
        // automated checks while someone is using the PC). Otherwise activate as usual.
        var startMinimized = Environment.GetCommandLineArgs()
            .Any(argument => string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase));
        if (startMinimized && TryShowMinimized(MainWindow))
        {
            AppLog.Info("Main window shown minimized without focus (--minimized).");
            return;
        }

        // Maximize after the window is shown: activating can reset the show state.
        MainWindow.Activate();
        try
        {
            const int ShowWindowMaximize = 3;
            ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(MainWindow), ShowWindowMaximize);
            AppLog.Info("Main window maximized.");
        }
        catch (Exception ex) { AppLog.Error("Maximizing the window failed.", ex); }

        AppLog.Info("Main window activated.");
    }

    private static bool TryShowMinimized(Window window)
    {
        try
        {
            // SW_SHOWMINNOACTIVE: show minimized and leave the active window (and focus) alone.
            const int SwShowMinNoActive = 7;
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            ShowWindow(handle, SwShowMinNoActive);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Showing the window minimized failed; activating it instead.", ex);
            return false;
        }
    }
}
