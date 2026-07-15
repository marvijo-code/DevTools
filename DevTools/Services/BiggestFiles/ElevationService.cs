using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace DevTools.Services.BiggestFiles;

/// <summary>
/// Elevation + volume checks that decide whether the fast MFT path is available, plus a
/// one-click relaunch-as-administrator helper.
/// </summary>
public static class ElevationService
{
    [SupportedOSPlatform("windows")]
    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the volume backing <paramref name="scanRoot"/> is formatted NTFS.</summary>
    public static bool IsNtfsVolume(string scanRoot)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(scanRoot));
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var drive = new DriveInfo(root);
            return drive.IsReady &&
                   string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Relaunches the current executable with a UAC elevation prompt. Returns false (with a
    /// friendly message) when the user cancels the prompt or elevation is otherwise unavailable;
    /// the caller is responsible for exiting the current instance on success.
    /// </summary>
    public static bool TryRestartAsAdmin(out string? error)
    {
        error = null;

        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                error = "Could not determine the DevTools executable path.";
                return false;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty
            };

            Process.Start(startInfo);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — the user dismissed the UAC prompt.
            error = "Administrator relaunch was cancelled.";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
