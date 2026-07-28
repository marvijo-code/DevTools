namespace DevTools.Services.Resources;

/// <summary>
/// Buckets a process into the kind of work it represents. The split is deliberately workflow-shaped
/// rather than OS-shaped: the question this tab answers is "which part of my dev setup is eating the
/// machine", so coding agents, the runtimes they spawn, and builds are separated from Windows itself.
/// </summary>
public enum WorkloadCategory
{
    CodingAgents,
    AgentRuntimes,
    Builds,
    Shells,
    Browsers,
    Editors,
    Security,
    Virtualization,
    WindowsMaintenance,
    System,
    Other
}

public static class WorkloadClassifier
{
    /// <summary>
    /// Longest-match-wins rules keyed on the executable name (without extension). Ordered most specific
    /// first so <c>chrome-headless-shell</c> does not fall through to the generic <c>chrome</c> rule.
    /// </summary>
    private static readonly (string Token, WorkloadCategory Category)[] Rules =
    [
        // Coding agents themselves.
        ("claude", WorkloadCategory.CodingAgents),
        ("codex", WorkloadCategory.CodingAgents),
        ("opencode", WorkloadCategory.CodingAgents),
        ("cursor-agent", WorkloadCategory.CodingAgents),
        ("aider", WorkloadCategory.CodingAgents),
        ("gemini", WorkloadCategory.CodingAgents),
        ("copilot", WorkloadCategory.CodingAgents),
        ("goose", WorkloadCategory.CodingAgents),
        ("crush", WorkloadCategory.CodingAgents),
        ("amp", WorkloadCategory.CodingAgents),

        // Runtimes agents spawn: MCP servers, language servers, bundlers, tool scripts.
        ("chrome-headless-shell", WorkloadCategory.AgentRuntimes),
        ("headless_shell", WorkloadCategory.AgentRuntimes),
        ("node", WorkloadCategory.AgentRuntimes),
        ("bun", WorkloadCategory.AgentRuntimes),
        ("deno", WorkloadCategory.AgentRuntimes),
        ("esbuild", WorkloadCategory.AgentRuntimes),
        ("tsserver", WorkloadCategory.AgentRuntimes),
        ("vite", WorkloadCategory.AgentRuntimes),
        ("uv", WorkloadCategory.AgentRuntimes),
        ("uvx", WorkloadCategory.AgentRuntimes),
        ("python", WorkloadCategory.AgentRuntimes),
        ("py", WorkloadCategory.AgentRuntimes),
        ("ruby", WorkloadCategory.AgentRuntimes),

        // Compilers and build servers.
        ("vbcscompiler", WorkloadCategory.Builds),
        ("msbuild", WorkloadCategory.Builds),
        ("dotnet", WorkloadCategory.Builds),
        ("csc", WorkloadCategory.Builds),
        ("gradle", WorkloadCategory.Builds),
        ("java", WorkloadCategory.Builds),
        ("cargo", WorkloadCategory.Builds),
        ("rustc", WorkloadCategory.Builds),
        ("rust-analyzer", WorkloadCategory.Builds),
        ("gopls", WorkloadCategory.Builds),
        ("go", WorkloadCategory.Builds),
        ("cl", WorkloadCategory.Builds),
        ("link", WorkloadCategory.Builds),
        ("ninja", WorkloadCategory.Builds),
        ("cmake", WorkloadCategory.Builds),
        ("tsc", WorkloadCategory.Builds),

        // Shells, terminals and CLI tooling.
        ("conhost", WorkloadCategory.Shells),
        ("openconsole", WorkloadCategory.Shells),
        ("windowsterminal", WorkloadCategory.Shells),
        ("powershell", WorkloadCategory.Shells),
        ("pwsh", WorkloadCategory.Shells),
        ("bash", WorkloadCategory.Shells),
        ("sh", WorkloadCategory.Shells),
        ("zsh", WorkloadCategory.Shells),
        ("cmd", WorkloadCategory.Shells),
        ("git", WorkloadCategory.Shells),
        ("gh", WorkloadCategory.Shells),
        ("rg", WorkloadCategory.Shells),
        ("fd", WorkloadCategory.Shells),
        ("ssh", WorkloadCategory.Shells),

        // Browsers.
        ("msedgewebview2", WorkloadCategory.Browsers),
        ("msedge", WorkloadCategory.Browsers),
        ("chrome", WorkloadCategory.Browsers),
        ("firefox", WorkloadCategory.Browsers),
        ("brave", WorkloadCategory.Browsers),
        ("opera", WorkloadCategory.Browsers),
        ("iexplore", WorkloadCategory.Browsers),

        // Editors and IDEs.
        ("devenv", WorkloadCategory.Editors),
        ("servicehub", WorkloadCategory.Editors),
        ("rider64", WorkloadCategory.Editors),
        ("idea64", WorkloadCategory.Editors),
        ("pycharm64", WorkloadCategory.Editors),
        ("jetbrains", WorkloadCategory.Editors),
        ("omnisharp", WorkloadCategory.Editors),
        ("roslyn", WorkloadCategory.Editors),
        ("code", WorkloadCategory.Editors),
        ("sublime_text", WorkloadCategory.Editors),

        // Antivirus and security scanning.
        ("msmpeng", WorkloadCategory.Security),
        ("mpdefendercoreservice", WorkloadCategory.Security),
        ("mpcmdrun", WorkloadCategory.Security),
        ("nissrv", WorkloadCategory.Security),
        ("securityhealthservice", WorkloadCategory.Security),
        ("smartscreen", WorkloadCategory.Security),
        ("avp", WorkloadCategory.Security),
        ("mcshield", WorkloadCategory.Security),
        ("ccsvchst", WorkloadCategory.Security),

        // Containers and virtual machines.
        ("vmmemwsl", WorkloadCategory.Virtualization),
        ("vmmem", WorkloadCategory.Virtualization),
        ("vmwp", WorkloadCategory.Virtualization),
        ("vmcompute", WorkloadCategory.Virtualization),
        ("wslservice", WorkloadCategory.Virtualization),
        ("wslhost", WorkloadCategory.Virtualization),
        ("wsl", WorkloadCategory.Virtualization),
        ("docker", WorkloadCategory.Virtualization),
        ("com.docker.backend", WorkloadCategory.Virtualization),
        ("vmware", WorkloadCategory.Virtualization),
        ("virtualbox", WorkloadCategory.Virtualization),

        // Windows background maintenance that competes with dev work.
        ("searchindexer", WorkloadCategory.WindowsMaintenance),
        ("searchprotocolhost", WorkloadCategory.WindowsMaintenance),
        ("searchfilterhost", WorkloadCategory.WindowsMaintenance),
        ("tiworker", WorkloadCategory.WindowsMaintenance),
        ("trustedinstaller", WorkloadCategory.WindowsMaintenance),
        ("compattelrunner", WorkloadCategory.WindowsMaintenance),
        ("mousocoreworker", WorkloadCategory.WindowsMaintenance),
        ("usoclient", WorkloadCategory.WindowsMaintenance),
        ("wuauclt", WorkloadCategory.WindowsMaintenance),
        ("onedrive", WorkloadCategory.WindowsMaintenance),
        ("backgroundtaskhost", WorkloadCategory.WindowsMaintenance),
        ("defrag", WorkloadCategory.WindowsMaintenance),

        // Core OS.
        ("memory compression", WorkloadCategory.System),
        ("system", WorkloadCategory.System),
        ("registry", WorkloadCategory.System),
        ("secure system", WorkloadCategory.System),
        ("idle", WorkloadCategory.System),
        ("svchost", WorkloadCategory.System),
        ("csrss", WorkloadCategory.System),
        ("wininit", WorkloadCategory.System),
        ("winlogon", WorkloadCategory.System),
        ("lsass", WorkloadCategory.System),
        ("services", WorkloadCategory.System),
        ("dwm", WorkloadCategory.System),
        ("explorer", WorkloadCategory.System),
        ("runtimebroker", WorkloadCategory.System),
        ("wmiprvse", WorkloadCategory.System),
        ("dllhost", WorkloadCategory.System),
        ("taskhostw", WorkloadCategory.System),
        ("fontdrvhost", WorkloadCategory.System),
        ("audiodg", WorkloadCategory.System),
        ("sihost", WorkloadCategory.System),
        ("ctfmon", WorkloadCategory.System)
    ];

    public static WorkloadCategory Classify(string? imageName)
    {
        var token = NormalizeImageName(imageName);
        if (token.Length == 0)
        {
            return WorkloadCategory.Other;
        }

        // Exact name wins outright, so "go.exe" never matches the "gopls" rule and vice versa.
        foreach (var (ruleToken, category) in Rules)
        {
            if (string.Equals(token, ruleToken, StringComparison.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        foreach (var (ruleToken, category) in Rules)
        {
            // Prefix match catches versioned and suffixed variants such as "python3.13" or "node-18".
            if (ruleToken.Length >= 3 && token.StartsWith(ruleToken, StringComparison.OrdinalIgnoreCase))
            {
                return category;
            }
        }

        return WorkloadCategory.Other;
    }

    /// <summary>Strips the path and extension so rules match on a bare executable name.</summary>
    public static string NormalizeImageName(string? imageName)
    {
        if (string.IsNullOrWhiteSpace(imageName))
        {
            return string.Empty;
        }

        var name = imageName.Trim();

        var separatorIndex = name.LastIndexOfAny(['\\', '/']);
        if (separatorIndex >= 0 && separatorIndex < name.Length - 1)
        {
            name = name[(separatorIndex + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name;
    }

    public static string DisplayName(WorkloadCategory category) => category switch
    {
        WorkloadCategory.CodingAgents => "Coding agents",
        WorkloadCategory.AgentRuntimes => "Agent runtimes (node, bun, python)",
        WorkloadCategory.Builds => "Compilers and build servers",
        WorkloadCategory.Shells => "Shells and CLI tools",
        WorkloadCategory.Browsers => "Browsers",
        WorkloadCategory.Editors => "Editors and IDEs",
        WorkloadCategory.Security => "Antivirus scanning",
        WorkloadCategory.Virtualization => "Containers and VMs",
        WorkloadCategory.WindowsMaintenance => "Windows background maintenance",
        WorkloadCategory.System => "Windows core",
        _ => "Other"
    };

    /// <summary>Categories that belong to the user's own dev workflow rather than to Windows.</summary>
    public static bool IsDeveloperWorkload(WorkloadCategory category) => category is
        WorkloadCategory.CodingAgents or
        WorkloadCategory.AgentRuntimes or
        WorkloadCategory.Builds or
        WorkloadCategory.Shells or
        WorkloadCategory.Editors;
}
