using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class ProcessCommandLineResolverTests
{
    [Fact]
    public void FormatStartPathDisplay_ExplainsSubprocessWithoutVolatileArguments()
    {
        const string commandLine =
            "\"C:\\Users\\user\\AppData\\Local\\Google\\Chrome\\Application\\chrome.exe\" " +
            "--type=renderer --extension-process --renderer-client-id=12";

        var startPath = ProcessCommandLineResolver.FormatStartPathDisplay(commandLine, fallbackPath: null);

        Assert.StartsWith(@"C:\Users\user\AppData\Local\Google\Chrome\Application\chrome.exe", startPath);
        Assert.Contains("Extension renderer", startPath);
        Assert.DoesNotContain("--", startPath);
    }

    [Theory]
    [InlineData("--type=renderer --renderer-client-id=12", "Page renderer")]
    [InlineData("--type=utility --utility-sub-type=network.mojom.NetworkService --token=private", "Network service")]
    [InlineData("--type=utility --utility-sub-type=node.mojom.NodeService", "Node.js utility process")]
    [InlineData("--type=utility --utility-sub-type=audio.mojom.AudioService", "Audio service")]
    [InlineData("--type=gpu-process --gpu-preferences=opaque", "Graphics process")]
    [InlineData("--type=unknown --secret=private", "Child process")]
    public void ChatGptSwitches_HaveReadableRoles(string args, string expected)
    {
        var display = ProcessCommandLineResolver.FormatStartPathDisplay("\"C:\\Program Files\\WindowsApps\\ChatGPT\\ChatGPT.exe\" " + args, null);
        Assert.EndsWith(expected, display);
        Assert.DoesNotContain("private", display);
        Assert.DoesNotContain("--", display);
    }

    [Theory]
    [InlineData("-contentproc -parentPid 123 -appDir \"C:\\Program Files\\Mozilla Firefox\\browser\" - 3 tab", "Web content process (may contain several tabs)")]
    [InlineData("-contentproc -processType=socket", "Network service")]
    [InlineData("-contentproc -childID 5 - 5 rdd", "Media decoder")]
    [InlineData("-contentproc - 4 unknown", "Firefox child process")]
    [InlineData("", "Browser main process")]
    public void FirefoxSwitches_SupportCurrentAndExplicitProcessTypes(string args, string expected)
    {
        Assert.Equal(expected, ProcessCommandLineResolver.ResolveRole("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" " + args));
    }

    [Fact]
    public void ArbitraryToolArguments_AreNotTreatedAsAppServer()
    {
        Assert.Equal(string.Empty, ProcessCommandLineResolver.ResolveRole("tool.exe --description=app-server"));
        Assert.Equal("Codex app server", ProcessCommandLineResolver.ResolveRole("codex.exe app-server --listen stdio://"));
    }

    [Fact]
    public void FormatStartPathDisplay_UsesExecutableOnlyForToolProcesses()
    {
        const string commandLine =
            "\"C:\\Program Files\\dotnet\\sdk\\10.0.108\\Roslyn\\bincore\\VBCSCompiler.exe\" -pipename:abc";

        var startPath = ProcessCommandLineResolver.FormatStartPathDisplay(commandLine, fallbackPath: null);

        Assert.Equal(@"C:\Program Files\dotnet\sdk\10.0.108\Roslyn\bincore\VBCSCompiler.exe", startPath);
    }
}
