using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using DevTools.Infrastructure;

internal static class Launcher
{
    private const string ElevationMessage = "DevTools starts with administrator rights and the Windows prompt was declined. Approve the prompt, or open \"DevTools (no admin)\" from the Start menu.";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 2 && args[0] == "--register-shortcut")
            {
                ShellIdentity.RegisterShortcut(Path.GetFullPath(args[1]));
                return 0;
            }

            // DevTools starts as administrator by default; the app stores the user's choice in
            // its own settings.json. These flags override that choice for one launch:
            //   --no-admin  the "DevTools (no admin)" shortcut
            //   --admin     force elevation even when the preference says otherwise
            var elevateForOneLaunch = false;
            var noElevateForOneLaunch = false;
            foreach (var argument in args)
            {
                if (string.Equals(argument, "--no-admin", StringComparison.OrdinalIgnoreCase))
                {
                    noElevateForOneLaunch = true;
                }
                else if (string.Equals(argument, "--admin", StringComparison.OrdinalIgnoreCase))
                {
                    elevateForOneLaunch = true;
                }
            }

            string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string versionDirectory;
            bool elevate;
            if (!TryReadLaunchConfiguration(root, noElevateForOneLaunch, elevateForOneLaunch, out versionDirectory, out elevate))
            {
                return StartWithPowerShell(root, noElevateForOneLaunch, elevateForOneLaunch);
            }

            // Start the selected release before loading PowerShell for the background update.
            var app = new ProcessStartInfo(Path.Combine(versionDirectory, "DevTools.exe"));
            app.UseShellExecute = true;
            app.WorkingDirectory = versionDirectory;
            if (elevate)
            {
                app.Verb = "runas";
            }

            try
            {
                using (Process.Start(app)) { }
            }
            catch (Win32Exception error)
            {
                if (elevate)
                {
                    throw new InvalidOperationException(ElevationMessage, error);
                }

                throw;
            }

            if (ShouldRunUpdater(root, DateTimeOffset.UtcNow))
            {
                string updater = Path.Combine(versionDirectory, "Update-DevTools.ps1");
                if (!File.Exists(updater))
                {
                    updater = Path.Combine(root, "Update-DevTools.ps1");
                }

                using (StartScript(root, updater, "")) { }
            }

            return 0;
        }
        catch (Exception error)
        {
            // Only the silent --register-shortcut call reports through a file; a human who
            // launched DevTools gets a dialog instead.
            if (args.Length > 0 && args[0] == "--register-shortcut")
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "registration-error.txt"), error.ToString());
                return 1;
            }

            MessageBox.Show(error.Message, "DevTools", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static bool TryReadLaunchConfiguration(string root, bool noElevate, bool forceElevation,
        out string versionDirectory, out bool elevate)
    {
        versionDirectory = null;
        elevate = !noElevate;
        try
        {
            var serializer = new JavaScriptSerializer();
            var manifest = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(root, "current.json")));
            var version = ReadProperty(manifest, "directory") as string;
            if (string.IsNullOrWhiteSpace(version))
            {
                return false;
            }

            var boundary = Path.GetFullPath(Path.Combine(root, "versions")) + Path.DirectorySeparatorChar;
            var directory = Path.GetFullPath(Path.Combine(root, version));
            if (!directory.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(directory, "DevTools.exe")))
            {
                return false;
            }

            if (!noElevate && !forceElevation)
            {
                var settingsPath = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA"), "DevTools", "settings.json");
                if (File.Exists(settingsPath))
                {
                    var settings = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(settingsPath));
                    var preference = ReadProperty(settings, "RunAsAdministrator");
                    if (preference != null)
                    {
                        if (!(preference is bool))
                        {
                            return false;
                        }

                        elevate = (bool)preference;
                    }
                }
            }

            versionDirectory = directory;
            return true;
        }
        catch (Exception)
        {
            // The existing startup script handles installation repair and legacy settings.
            return false;
        }
    }

    internal static bool ShouldRunUpdater(string root, DateTimeOffset now)
    {
        try
        {
            var state = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(
                File.ReadAllText(Path.Combine(root, "update-state.json")));
            DateTimeOffset previous;
            var checkedAt = ReadProperty(state, "checkedAt") as string;
            // Keep the interval in sync with Update-DevTools.ps1, which also runs directly.
            return !DateTimeOffset.TryParse(checkedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out previous) ||
                now - previous >= TimeSpan.FromHours(6);
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static object ReadProperty(Dictionary<string, object> values, string name)
    {
        object found = null;
        var hasMatch = false;
        if (values != null)
        {
            foreach (var value in values)
            {
                if (!string.Equals(value.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (hasMatch)
                {
                    throw new InvalidDataException("The startup configuration contains ambiguous property names.");
                }

                hasMatch = true;
                found = value.Value;
            }
        }

        return found;
    }

    private static int StartWithPowerShell(string root, bool noElevate, bool forceElevation)
    {
        var arguments = noElevate ? " -NoElevate" : forceElevation ? " -Elevate" : "";
        using (var child = StartScript(root, Path.Combine(root, "Start-DevTools.ps1"), arguments))
        {
            child.WaitForExit();
            if (child.ExitCode == 2)
            {
                throw new InvalidOperationException(ElevationMessage);
            }

            if (child.ExitCode != 0)
            {
                throw new InvalidOperationException("DevTools could not start. Run Install-DevTools.ps1 to repair the installation.");
            }
        }

        return 0;
    }

    private static Process StartScript(string root, string script, string arguments)
    {
        var shell = File.ReadAllText(Path.Combine(root, "powershell-path.txt")).Trim();
        var command = "-NoProfile -WindowStyle Hidden -File \"" + script + "\" -InstallRoot \"" + root + "\"" + arguments;
        var start = new ProcessStartInfo(shell, command);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.WorkingDirectory = root;
        return Process.Start(start);
    }
}
