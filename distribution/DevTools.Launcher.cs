using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Forms;
using DevTools.Infrastructure;

internal static class Launcher
{
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
            string shell = File.ReadAllText(Path.Combine(root, "powershell-path.txt")).Trim();
            var command = new StringBuilder();
            command.Append("-NoProfile -WindowStyle Hidden -File \"");
            command.Append(Path.Combine(root, "Start-DevTools.ps1"));
            command.Append("\" -InstallRoot \"");
            command.Append(root);
            command.Append('"');
            if (noElevateForOneLaunch)
            {
                command.Append(" -NoElevate");
            }
            else if (elevateForOneLaunch)
            {
                command.Append(" -Elevate");
            }

            var start = new ProcessStartInfo(shell, command.ToString());
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WorkingDirectory = root;
            using (var child = Process.Start(start))
            {
                child.WaitForExit();
                if (child.ExitCode == 2)
                {
                    throw new InvalidOperationException("DevTools starts with administrator rights and the Windows prompt was declined. Approve the prompt, or open \"DevTools (no admin)\" from the Start menu.");
                }

                if (child.ExitCode != 0)
                {
                    throw new InvalidOperationException("DevTools could not start. Run Install-DevTools.ps1 to repair the installation.");
                }
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
}
