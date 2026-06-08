using System.Runtime.InteropServices;
using System.Text;

namespace DevTools.Services;

public static class ProcessWindowTitleResolver
{
    public static string? TryGetWindowTitle(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        string? bestTitle = null;
        EnumWindows(
            (windowHandle, _) =>
            {
                GetWindowThreadProcessId(windowHandle, out uint windowProcessId);
                if (windowProcessId != (uint)processId || !IsWindowVisible(windowHandle))
                {
                    return true;
                }

                var title = ReadWindowTitle(windowHandle);
                if (string.IsNullOrWhiteSpace(title) || IsIgnoredWindowTitle(title))
                {
                    return true;
                }

                bestTitle = title;
                return true;
            },
            IntPtr.Zero);

        return bestTitle;
    }

    public static bool IsGenericAppWindowTitle(string title, string appName)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return true;
        }

        if (string.Equals(title, appName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return title.Equals("Google Chrome", StringComparison.OrdinalIgnoreCase) ||
               title.Equals("Mozilla Firefox", StringComparison.OrdinalIgnoreCase) ||
               title.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase) ||
               title.Equals("Claude", StringComparison.OrdinalIgnoreCase) ||
               title.Equals("Codex", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadWindowTitle(IntPtr windowHandle)
    {
        var builder = new StringBuilder(512);
        _ = GetWindowText(windowHandle, builder, builder.Capacity);
        return builder.ToString().Trim();
    }

    private static bool IsIgnoredWindowTitle(string title)
    {
        return title.Equals("Default IME", StringComparison.OrdinalIgnoreCase) ||
               title.Equals("MSCTFIME UI", StringComparison.OrdinalIgnoreCase);
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
