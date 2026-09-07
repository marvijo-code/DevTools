using System.Text.RegularExpressions;

namespace DevTools.Services;

public static partial class ProcessDisplayNameResolver
{
    private static readonly Regex ExecutablePathRegex = ExecutablePathPattern();
    private static readonly Regex ElectronTypeRegex = ElectronTypePattern();
    private static readonly Regex UtilitySubTypeRegex = UtilitySubTypePattern();
    private static readonly Regex RendererClientIdRegex = RendererClientIdPattern();
    private static readonly Regex AppServerListenRegex = AppServerListenPattern();
    private static readonly Regex FirefoxProcessTypeRegex = FirefoxProcessTypePattern();

    public static string Resolve(
        string? commandLine,
        string? processName,
        int processId,
        int parentProcessId,
        long workingSetBytes,
        CodexSessionCatalog? sessionCatalog = null)
    {
        var normalizedCommandLine = commandLine?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCommandLine))
        {
            return string.IsNullOrWhiteSpace(processName) ? $"Process {processId}" : processName;
        }

        var executablePath = TryExtractExecutablePath(normalizedCommandLine);
        var executableName = string.IsNullOrWhiteSpace(executablePath)
            ? processName ?? "Process"
            : Path.GetFileNameWithoutExtension(executablePath);
        var appName = ResolveAppName(executablePath, executableName, normalizedCommandLine);

        if (IsCodexFamily(appName, normalizedCommandLine))
        {
            return ResolveCodexDisplayName(appName, normalizedCommandLine, processId, workingSetBytes, sessionCatalog);
        }

        if (IsFirefoxFamily(appName, executableName, normalizedCommandLine))
        {
            return ResolveFirefoxDisplayName(normalizedCommandLine, processId, workingSetBytes);
        }

        if (TryResolveElectronDisplayName(appName, normalizedCommandLine, processId, workingSetBytes, out var electronName))
        {
            return electronName;
        }

        if (executableName.Equals("codex", StringComparison.OrdinalIgnoreCase) &&
            normalizedCommandLine.Contains("app-server", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveCodexAppServerName(normalizedCommandLine);
        }

        return ResolveLeafDisplayName(executablePath, appName);
    }

    public static string ResolveExecutableBaseName(string? commandLine, string? processName)
    {
        var executablePath = TryExtractExecutablePath(commandLine ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            return Path.GetFileNameWithoutExtension(executablePath);
        }

        return string.IsNullOrWhiteSpace(processName)
            ? "Process"
            : Path.GetFileNameWithoutExtension(processName);
    }

    public static string? TryExtractExecutablePath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var match = ExecutablePathRegex.Match(commandLine);
        return match.Success ? match.Groups["path"].Value : null;
    }

    private static string ResolveCodexDisplayName(
        string appName,
        string commandLine,
        int processId,
        long workingSetBytes,
        CodexSessionCatalog? sessionCatalog)
    {
        if (commandLine.Contains("app-server", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveCodexAppServerName(commandLine);
        }

        var typeMatch = ElectronTypeRegex.Match(commandLine);
        if (!typeMatch.Success)
        {
            var sessionTitle = sessionCatalog?.GetMostRecentThreadTitle();
            return string.IsNullOrWhiteSpace(sessionTitle) ? appName : $"{appName} — {sessionTitle}";
        }

        var processType = typeMatch.Groups["type"].Value;
        return processType switch
        {
            "renderer" => ResolveRendererName(appName, commandLine, processId, workingSetBytes, sessionCatalog),
            "gpu-process" => $"{appName} — GPU Process",
            "utility" => $"{appName} — {ResolveUtilityName(commandLine)}",
            "crashpad-handler" => $"{appName} — Crash Handler",
            _ => $"{appName} — {ToTitleWords(processType)}"
        };
    }

    private static string ResolveRendererName(
        string appName,
        string commandLine,
        int processId,
        long workingSetBytes,
        CodexSessionCatalog? sessionCatalog)
    {
        var sessionTitle = sessionCatalog?.GetMostRecentThreadTitle();
        if (!string.IsNullOrWhiteSpace(sessionTitle) && workingSetBytes >= 32 * 1024 * 1024)
        {
            return $"{appName} — {sessionTitle}";
        }

        var windowTitle = ProcessWindowTitleResolver.TryGetWindowTitle(processId);
        if (!string.IsNullOrWhiteSpace(windowTitle) &&
            !ProcessWindowTitleResolver.IsGenericAppWindowTitle(windowTitle, appName))
        {
            return $"{appName} — {windowTitle}";
        }

        var clientIdMatch = RendererClientIdRegex.Match(commandLine);
        if (clientIdMatch.Success)
        {
            return workingSetBytes >= 32 * 1024 * 1024
                ? $"{appName} — Session Window {clientIdMatch.Groups["id"].Value}"
                : $"{appName} — Background Page {clientIdMatch.Groups["id"].Value}";
        }

        return workingSetBytes >= 32 * 1024 * 1024
            ? $"{appName} — Session Window"
            : $"{appName} — Background Page";
    }

    private static string ResolveCodexAppServerName(string commandLine)
    {
        var listenMatch = AppServerListenRegex.Match(commandLine);
        if (listenMatch.Success)
        {
            var listenTarget = listenMatch.Groups["target"].Value;
            return listenTarget.Equals("stdio://", StringComparison.OrdinalIgnoreCase)
                ? "Codex — App Server (IDE Session)"
                : $"Codex — App Server ({listenTarget})";
        }

        return "Codex — App Server";
    }

    private static bool TryResolveElectronDisplayName(
        string appName,
        string commandLine,
        int processId,
        long workingSetBytes,
        out string displayName)
    {
        var typeMatch = ElectronTypeRegex.Match(commandLine);
        if (!typeMatch.Success)
        {
            displayName = appName;
            return false;
        }

        var processType = typeMatch.Groups["type"].Value;
        displayName = processType switch
        {
            "renderer" => ResolveElectronRendererName(appName, commandLine, processId, workingSetBytes),
            "gpu-process" => $"{appName} — GPU Process",
            "utility" => $"{appName} — {ResolveUtilityName(commandLine)}",
            "crashpad-handler" => $"{appName} — Crash Handler",
            _ => $"{appName} — {ToTitleWords(processType)}"
        };
        return true;
    }

    private static string ResolveElectronRendererName(
        string appName,
        string commandLine,
        int processId,
        long workingSetBytes)
    {
        var windowTitle = ProcessWindowTitleResolver.TryGetWindowTitle(processId);
        if (!string.IsNullOrWhiteSpace(windowTitle) &&
            !ProcessWindowTitleResolver.IsGenericAppWindowTitle(windowTitle, appName))
        {
            return $"{appName} — {windowTitle}";
        }

        var clientIdMatch = RendererClientIdRegex.Match(commandLine);
        if (clientIdMatch.Success)
        {
            return workingSetBytes >= 32 * 1024 * 1024
                ? $"{appName} — Session Window {clientIdMatch.Groups["id"].Value}"
                : $"{appName} — Background Page {clientIdMatch.Groups["id"].Value}";
        }

        return workingSetBytes >= 32 * 1024 * 1024
            ? $"{appName} — Session Window"
            : $"{appName} — Background Page";
    }

    private static string ResolveFirefoxDisplayName(string commandLine, int processId, long workingSetBytes)
    {
        if (!commandLine.Contains("-contentproc", StringComparison.OrdinalIgnoreCase))
        {
            return "Firefox";
        }

        var processType = ProcessCommandLineResolver.GetFirefoxProcessType(commandLine);
        if (string.IsNullOrEmpty(processType))
        {
            return "Firefox — Content Process";
        }

        if (processType.Equals("tab", StringComparison.OrdinalIgnoreCase))
        {
            var windowTitle = ProcessWindowTitleResolver.TryGetWindowTitle(processId);
            if (!string.IsNullOrWhiteSpace(windowTitle) &&
                !ProcessWindowTitleResolver.IsGenericAppWindowTitle(windowTitle, "Firefox"))
            {
                return $"Firefox — {windowTitle}";
            }

            return workingSetBytes >= 16 * 1024 * 1024
                ? "Firefox — Tab"
                : "Firefox — Background Tab";
        }

        return processType switch
        {
            "gpu" => "Firefox — GPU Process",
            "socket" => "Firefox — Network Service",
            "rdd" => "Firefox — Media Decoder",
            "utility" => "Firefox — Utility Process",
            "vr" => "Firefox — VR Process",
            "forkserver" => "Firefox — Fork Server",
            _ => $"Firefox — {ToTitleWords(processType)}"
        };
    }

    private static string ResolveUtilityName(string commandLine)
    {
        var utilityMatch = UtilitySubTypeRegex.Match(commandLine);
        if (!utilityMatch.Success)
        {
            return "Utility Process";
        }

        var rawSubType = utilityMatch.Groups["subtype"].Value;
        var shortName = rawSubType.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(shortName))
        {
            return "Utility Process";
        }

        if (shortName.EndsWith("Service", StringComparison.OrdinalIgnoreCase) && shortName.Length > "Service".Length)
        {
            shortName = shortName[..^"Service".Length];
        }

        return $"{ToTitleWords(shortName)} Service";
    }

    private static string ResolveAppName(string? executablePath, string executableName, string commandLine)
    {
        if (commandLine.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) ||
            executableName.Equals("codex", StringComparison.OrdinalIgnoreCase))
        {
            return "Codex";
        }

        if (commandLine.Contains("Cursor", StringComparison.OrdinalIgnoreCase))
        {
            return "Cursor";
        }

        if (IsClaudeFamily(executableName, executablePath, commandLine))
        {
            return "Claude";
        }

        if (IsChromiumBrowser(executableName, commandLine))
        {
            return ResolveChromiumBrowserName(executableName, executablePath);
        }

        if (IsFirefoxFamily(string.Empty, executableName, commandLine))
        {
            return "Firefox";
        }

        if (commandLine.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
        {
            var packageSegment = executablePath?
                .Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(segment => segment.Contains('.', StringComparison.Ordinal));
            if (!string.IsNullOrWhiteSpace(packageSegment))
            {
                var appSegment = packageSegment.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(appSegment))
                {
                    var dotIndex = appSegment.IndexOf('.');
                    if (dotIndex >= 0 && dotIndex < appSegment.Length - 1)
                    {
                        return appSegment[(dotIndex + 1)..];
                    }
                }
            }
        }

        return ToTitleWords(executableName);
    }

    private static string ResolveLeafDisplayName(string? executablePath, string appName)
    {
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var executableBaseName = Path.GetFileNameWithoutExtension(executablePath);
            if (!string.IsNullOrWhiteSpace(executableBaseName))
            {
                return executableBaseName;
            }
        }

        return appName;
    }

    private static bool IsChromiumBrowser(string executableName, string commandLine)
    {
        if (executableName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
            executableName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
            executableName.Equals("brave", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return commandLine.Contains(@"\Google\Chrome\", StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains(@"\Microsoft\Edge\", StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains(@"\BraveSoftware\", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveChromiumBrowserName(string executableName, string? executablePath)
    {
        if (executableName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
            executablePath?.Contains(@"\Microsoft\Edge\", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Edge";
        }

        if (executableName.Equals("brave", StringComparison.OrdinalIgnoreCase) ||
            executablePath?.Contains(@"\BraveSoftware\", StringComparison.OrdinalIgnoreCase) == true)
        {
            return "Brave";
        }

        return "Chrome";
    }

    private static bool IsClaudeFamily(string executableName, string? executablePath, string commandLine)
    {
        return executableName.Equals("claude", StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains(@"AnthropicClaude", StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains(@"\Claude\", StringComparison.OrdinalIgnoreCase) ||
               executablePath?.Contains(@"AnthropicClaude", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsFirefoxFamily(string appName, string executableName, string commandLine)
    {
        if (executableName.Equals("firefox", StringComparison.OrdinalIgnoreCase) ||
            appName.Equals("Firefox", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return commandLine.Contains(@"\Mozilla Firefox\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodexFamily(string appName, string commandLine)
    {
        return appName.Equals("Codex", StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase) ||
               commandLine.Contains(@"\resources\codex.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string ToTitleWords(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Process";
        }

        return string.Join(
            ' ',
            value
                .Replace('-', ' ')
                .Replace('_', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    [GeneratedRegex("-processType=(?<type>[a-zA-Z0-9_-]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FirefoxProcessTypePattern();

    [GeneratedRegex("^\"(?<path>(?:\\\\|[^\"])+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ExecutablePathPattern();

    [GeneratedRegex("--type=(?<type>[a-zA-Z0-9_-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex ElectronTypePattern();

    [GeneratedRegex("--utility-sub-type=(?<subtype>[a-zA-Z0-9_.-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UtilitySubTypePattern();

    [GeneratedRegex("--renderer-client-id=(?<id>\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex RendererClientIdPattern();

    [GeneratedRegex("app-server(?:\\s+[^\\s\"]+)*\\s+--listen\\s+(?<target>[^\\s\"]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AppServerListenPattern();
}
