using System;
using DevTools.Infrastructure;
using Microsoft.Extensions.Logging;
using Uno.Resizetizer;

namespace DevTools;

public partial class App : Application
{
    /// <summary>
    /// Initializes the singleton application object. This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        try { ShellIdentity.ConfigureProcess(); }
        catch (Exception ex) { AppLog.Error("Setting Windows app identity failed.", ex); }
        this.InitializeComponent();
        AppLog.Info("App constructed.");
    }

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


        // Do not repeat app initialization when the Window already has content,
        // just ensure that the window is active
        if (MainWindow.Content is not Frame rootFrame)
        {
            // Create a Frame to act as the navigation context and navigate to the first page
            rootFrame = new Frame();

            // Place the frame in the current Window
            MainWindow.Content = rootFrame;

            rootFrame.NavigationFailed += OnNavigationFailed;
        }

        if (rootFrame.Content == null)
        {
            // When the navigation stack isn't restored navigate to the first page,
            // configuring the new page by passing required information as a navigation
            // parameter
            try
            {
                rootFrame.Navigate(typeof(MainPage), args.Arguments);
                AppLog.Info("Navigated to MainPage.");
            }
            catch (Exception ex)
            {
                AppLog.Error("Navigating to MainPage failed.", ex);
                throw;
            }
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

        // Maximize first so the window is laid out once at full size, then give it focus.
        try
        {
            const int SwMaximize = 3;
            ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(MainWindow), SwMaximize);
        }
        catch (Exception ex) { AppLog.Error("Maximizing the window failed.", ex); }
        MainWindow.Activate();
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

    /// <summary>
    /// Invoked when Navigation to a certain page fails
    /// </summary>
    /// <param name="sender">The Frame which failed navigation</param>
    /// <param name="e">Details about the navigation failure</param>
    void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        AppLog.Error($"Navigation failed for {e.SourcePageType.FullName}.", e.Exception);
        throw new InvalidOperationException($"Failed to load {e.SourcePageType.FullName}: {e.Exception}");
    }

    /// <summary>
    /// Configures global Uno Platform logging
    /// </summary>
    public static void InitializeLogging()
    {
#if DEBUG
        // Logging is disabled by default for release builds, as it incurs a significant
        // initialization cost from Microsoft.Extensions.Logging setup. If startup performance
        // is a concern for your application, keep this disabled. If you're running on the web or
        // desktop targets, you can use URL or command line parameters to enable it.
        //
        // For more performance documentation: https://platform.uno/docs/articles/Uno-UI-Performance.html

        var factory = LoggerFactory.Create(builder =>
        {
#if __WASM__
            builder.AddProvider(new global::Uno.Extensions.Logging.WebAssembly.WebAssemblyConsoleLoggerProvider());
#elif __IOS__
            builder.AddProvider(new global::Uno.Extensions.Logging.OSLogLoggerProvider());

            // Log to the Visual Studio Debug console
            builder.AddConsole();
#else
            builder.AddConsole();
#endif

            // Exclude logs below this level
            builder.SetMinimumLevel(LogLevel.Information);

            // Default filters for Uno Platform namespaces
            builder.AddFilter("Uno", LogLevel.Warning);
            builder.AddFilter("Windows", LogLevel.Warning);
            builder.AddFilter("Microsoft", LogLevel.Warning);

            // Generic Xaml events
            // builder.AddFilter("Microsoft.UI.Xaml", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.VisualStateGroup", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.StateTriggerBase", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.UIElement", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.FrameworkElement", LogLevel.Trace );

            // Layouter specific messages
            // builder.AddFilter("Microsoft.UI.Xaml.Controls", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Controls.Layouter", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Controls.Panel", LogLevel.Debug );

            // builder.AddFilter("Windows.Storage", LogLevel.Debug );

            // Binding related messages
            // builder.AddFilter("Microsoft.UI.Xaml.Data", LogLevel.Debug );
            // builder.AddFilter("Microsoft.UI.Xaml.Data", LogLevel.Debug );

            // Binder memory references tracking
            // builder.AddFilter("Uno.UI.DataBinding.BinderReferenceHolder", LogLevel.Debug );

            // DevServer and HotReload related
            // builder.AddFilter("Uno.UI.RemoteControl", LogLevel.Information);

            // Debug JS interop
            // builder.AddFilter("Uno.Foundation.WebAssemblyRuntime", LogLevel.Debug );
        });

        global::Uno.Extensions.LogExtensionPoint.AmbientLoggerFactory = factory;

#if HAS_UNO
        global::Uno.UI.Adapter.Microsoft.Extensions.Logging.LoggingAdapter.Initialize();
#endif
#endif
    }
}
