using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class ProcessDisplayNameResolverTests
{
    [Fact]
    public void Resolve_CodexMainProcess_UsesLatestSessionTitle()
    {
        var catalog = new CodexSessionCatalog();
        var commandLine = "\"C:\\Program Files\\WindowsApps\\OpenAI.Codex_26.602.4764.0_x64__2p2nqsd0c76g0\\app\\Codex.exe\"";

        var displayName = ProcessDisplayNameResolver.Resolve(
            commandLine,
            "Codex.exe",
            11832,
            12436,
            150_000_000,
            catalog);

        Assert.Equal("Codex", displayName);
    }

    [Fact]
    public void Resolve_CodexRenderer_UsesSessionTitleWhenWorkingSetIsLarge()
    {
        var catalog = CodexSessionCatalog.LoadDefault();
        var commandLine =
            "\"C:\\Program Files\\WindowsApps\\OpenAI.Codex_26.602.4764.0_x64__2p2nqsd0c76g0\\app\\Codex.exe\" " +
            "--type=renderer --renderer-client-id=5";

        var displayName = ProcessDisplayNameResolver.Resolve(
            commandLine,
            "Codex.exe",
            13920,
            11832,
            578_000_000,
            catalog);

        Assert.StartsWith("Codex — ", displayName);
        Assert.DoesNotContain("--type=renderer", displayName);
    }

    [Theory]
    [InlineData("--type=gpu-process", "Codex — GPU Process")]
    [InlineData("--type=utility --utility-sub-type=network.mojom.NetworkService", "Codex — Network Service")]
    [InlineData("--type=crashpad-handler", "Codex — Crash Handler")]
    public void Resolve_CodexSupportProcesses_UseFriendlyLabels(string arguments, string expected)
    {
        var commandLine =
            "\"C:\\Program Files\\WindowsApps\\OpenAI.Codex_26.602.4764.0_x64__2p2nqsd0c76g0\\app\\Codex.exe\" " +
            arguments;

        var displayName = ProcessDisplayNameResolver.Resolve(
            commandLine,
            "Codex.exe",
            1,
            2,
            10_000_000,
            null);

        Assert.Equal(expected, displayName);
    }

    [Fact]
    public void Resolve_CodexAppServerStdio_UsesIdeSessionLabel()
    {
        var commandLine =
            "\"C:\\Users\\user\\AppData\\Local\\OpenAI\\Codex\\bin\\fb2111b91430cb17\\codex.exe\" app-server --listen stdio://";

        var displayName = ProcessDisplayNameResolver.Resolve(
            commandLine,
            "codex.exe",
            4292,
            10316,
            3_000_000,
            null);

        Assert.Equal("Codex — App Server (IDE Session)", displayName);
    }

    [Fact]
    public void Resolve_ClaudeRenderer_UsesWindowTitleWhenAvailable()
    {
        var commandLine =
            "\"C:\\Users\\user\\AppData\\Local\\AnthropicClaude\\app-1.0\\claude.exe\" " +
            "--type=renderer --renderer-client-id=4";

        var displayName = ProcessDisplayNameResolver.Resolve(
            commandLine,
            "claude.exe",
            42,
            1000,
            80_000_000,
            null);

        Assert.StartsWith("Claude — ", displayName);
        Assert.DoesNotContain("--type=renderer", displayName);
    }

    [Fact]
    public void Resolve_FirefoxTabProcess_UsesTabLabel()
    {
        var commandLine =
            "\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" " +
            "-contentproc -processType=tab -profile \"C:\\Users\\user\\AppData\\Roaming\\Mozilla\\Firefox\\Profiles\\abc.default\" -parentPid 1000";

        var displayName = ProcessDisplayNameResolver.Resolve(
            commandLine,
            "firefox.exe",
            2000,
            1000,
            4_000_000,
            null);

        Assert.StartsWith("Firefox — ", displayName);
    }

    [Fact]
    public void Resolve_ChromeRenderer_UsesSessionWindowLabel()
    {
        var commandLine =
            "\"C:\\Users\\user\\AppData\\Local\\Google\\Chrome\\Application\\chrome.exe\" " +
            "--type=renderer --extension-process";

        var displayName = ProcessDisplayNameResolver.Resolve(
            commandLine,
            "chrome.exe",
            131352,
            1000,
            120_000_000,
            null);

        Assert.Equal("Chrome — Session Window", displayName);
    }

    [Fact]
    public void Resolve_VbcsCompiler_UsesExecutableBaseName()
    {
        var commandLine =
            "\"C:\\Program Files\\dotnet\\sdk\\10.0.108\\Roslyn\\bincore\\VBCSCompiler.exe\" -pipename:abc";

        var displayName = ProcessDisplayNameResolver.Resolve(
            commandLine,
            "VBCSCompiler.exe",
            132992,
            1000,
            20_000_000,
            null);

        Assert.Equal("VBCSCompiler", displayName);
    }

    [Fact]
    public void TryExtractExecutablePath_ParsesQuotedExecutable()
    {
        const string commandLine =
            "\"C:\\Program Files\\WindowsApps\\OpenAI.Codex_26.602.4764.0_x64__2p2nqsd0c76g0\\app\\Codex.exe\" --type=renderer";

        var executablePath = ProcessDisplayNameResolver.TryExtractExecutablePath(commandLine);

        Assert.Equal(
            @"C:\Program Files\WindowsApps\OpenAI.Codex_26.602.4764.0_x64__2p2nqsd0c76g0\app\Codex.exe",
            executablePath);
    }
}
