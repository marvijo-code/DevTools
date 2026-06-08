namespace DevTools.Services;

public static class ProcessCommandLineResolver
{
    public static string ResolveExecutablePath(string? commandLine, string? fallbackPath)
    {
        return ProcessDisplayNameResolver.TryExtractExecutablePath(commandLine ?? string.Empty)
            ?? fallbackPath
            ?? string.Empty;
    }

    public static string? ExtractArgumentTail(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            var endQuote = trimmed.IndexOf('"', 1);
            if (endQuote < 0)
            {
                return null;
            }

            var args = trimmed[(endQuote + 1)..].Trim();
            return string.IsNullOrWhiteSpace(args) ? null : args;
        }

        var firstSpace = trimmed.IndexOf(' ');
        if (firstSpace < 0)
        {
            return null;
        }

        var tail = trimmed[(firstSpace + 1)..].Trim();
        return string.IsNullOrWhiteSpace(tail) ? null : tail;
    }

    public static string FormatStartPathDisplay(string? commandLine, string? fallbackPath)
    {
        var executablePath = ResolveExecutablePath(commandLine, fallbackPath);
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return string.Empty;
        }

        var args = ExtractArgumentTail(commandLine);
        if (string.IsNullOrWhiteSpace(args) || !ShouldShowArguments(args))
        {
            return executablePath;
        }

        return $"{executablePath}{Environment.NewLine}{args}";
    }

    public static bool IsSubprocessCommandLine(string? commandLine)
    {
        var args = ExtractArgumentTail(commandLine);
        if (string.IsNullOrWhiteSpace(args))
        {
            return false;
        }

        return ShouldShowArguments(args);
    }

    private static bool ShouldShowArguments(string args)
    {
        return args.Contains("--type=", StringComparison.OrdinalIgnoreCase) ||
               args.Contains("--utility-sub-type=", StringComparison.OrdinalIgnoreCase) ||
               args.Contains("app-server", StringComparison.OrdinalIgnoreCase) ||
               args.Contains("--renderer-client-id=", StringComparison.OrdinalIgnoreCase) ||
               args.Contains("-contentproc", StringComparison.OrdinalIgnoreCase) ||
               args.Contains("-processType=", StringComparison.OrdinalIgnoreCase);
    }
}
