using System;
using System.Diagnostics;
using System.IO;
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
            string root = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            string shell = File.ReadAllText(Path.Combine(root, "powershell-path.txt")).Trim();
            var start = new ProcessStartInfo(shell,
                "-NoProfile -WindowStyle Hidden -File \"" + Path.Combine(root, "Start-DevTools.ps1") + "\" -InstallRoot \"" + root + "\"");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WorkingDirectory = root;
            using (var child = Process.Start(start))
            {
                child.WaitForExit();
                if (child.ExitCode != 0) throw new InvalidOperationException("DevTools could not start. Run Install-DevTools.ps1 to repair the installation.");
            }
            return 0;
        }
        catch (Exception error)
        {
            if (args.Length > 0)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "registration-error.txt"), error.ToString());
                return 1;
            }
            MessageBox.Show(error.Message, "DevTools", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
