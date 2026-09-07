using System.Text.RegularExpressions;

namespace DevTools.Services;

public static class ProcessCommandLineResolver
{
    public static string ResolveExecutablePath(string? commandLine, string? fallbackPath)
    {
        var path = ProcessDisplayNameResolver.TryExtractExecutablePath(commandLine ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(path)) return path;
        if (!string.IsNullOrWhiteSpace(fallbackPath)) return fallbackPath;
        var bareExecutable = Regex.Match(commandLine ?? string.Empty, @"^\s*([^\s""<>]+\.exe)(?=\s|$)", RegexOptions.IgnoreCase);
        return bareExecutable.Success ? bareExecutable.Groups[1].Value : string.Empty;
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

        var role = ResolveRole(commandLine, executablePath);
        return string.IsNullOrEmpty(role) ? executablePath : $"{executablePath}{Environment.NewLine}{role}";
    }

    // Only interpret known switches. Handles, feature flags and arbitrary argument values
    // remain available in CommandLine, but do not obscure the executable and its purpose.
    public static string ResolveRole(string? commandLine, string? executablePath = null)
    {
        var args = ExtractArgumentTail(commandLine) ?? string.Empty;
        var executable = Path.GetFileName(ResolveExecutablePath(commandLine, executablePath));
        if (executable.Equals("firefox.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (!Regex.IsMatch(args, @"(?:^|\s)-contentproc(?:\s|$)", RegexOptions.IgnoreCase))
                return "Browser main process";
            return GetFirefoxProcessType(commandLine ?? string.Empty) switch
            {
                "tab" => "Web content process (may contain several tabs)",
                "gpu" => "Graphics process",
                "socket" => "Network service",
                "rdd" => "Media decoder",
                "utility" => "Utility process",
                "vr" => "Virtual reality process",
                "forkserver" => "Process launcher",
                _ => "Firefox child process"
            };
        }

        var type = SwitchValue(args, "--type");
        if (type.Length > 0)
        {
            return type switch
            {
                "renderer" => Regex.IsMatch(args, @"(?:^|\s)--extension-process(?:\s|$)")
                    ? "Extension renderer" : "Page renderer",
                "gpu-process" => "Graphics process",
                "crashpad-handler" => "Crash reporter",
                "utility" => SwitchValue(args, "--utility-sub-type") switch
                {
                    "network.mojom.networkservice" => "Network service",
                    "audio.mojom.audioservice" => "Audio service",
                    "video_capture.mojom.videocaptureservice" => "Camera capture service",
                    "node.mojom.nodeservice" => "Node.js utility process",
                    "storage.mojom.storageservice" => "Storage service",
                    _ => "Utility process"
                },
                _ => "Child process"
            };
        }

        if (executable.Equals("codex.exe", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(args, @"^app-server(?:\s|$)", RegexOptions.IgnoreCase))
            return "Codex app server";
        return executable.ToLowerInvariant() switch
        {
            "chatgpt.exe" or "codex.exe" or "claude.exe" => "Application main process",
            "chrome.exe" or "msedge.exe" => "Browser main process",
            _ => string.Empty
        };
    }

    public static string GetFirefoxProcessType(string commandLine)
    {
        var explicitType = SwitchValue(ExtractArgumentTail(commandLine) ?? "", "-processType");
        if (explicitType.Length > 0) return explicitType;
        // Gecko child launches also put the process type after the child id at the end.
        var tail = Regex.Match(commandLine, @"\s\d+\s+(tab|gpu|socket|rdd|utility|vr|forkserver)\s*$", RegexOptions.IgnoreCase);
        return tail.Success ? tail.Groups[1].Value.ToLowerInvariant() : string.Empty;
    }

    private static string SwitchValue(string args, string name)
    {
        var match = Regex.Match(args, @"(?:^|\s)" + Regex.Escape(name) + "(?:=|\\s+)\"?([a-zA-Z0-9_.-]+)(?:\"?(?=\\s|$))", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : string.Empty;
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
