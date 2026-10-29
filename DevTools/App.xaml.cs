using System;
using DevTools.Infrastructure;
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
        // Ensure the current window is active
        MainWindow.Activate();
        AppLog.Info("Main window activated.");
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
}
