using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class ProcessCommandLineResolverTests
{
    [Fact]
    public void FormatStartPathDisplay_IncludesSubprocessArguments()
    {
        const string commandLine =
            "\"C:\\Users\\user\\AppData\\Local\\Google\\Chrome\\Application\\chrome.exe\" " +
            "--type=renderer --extension-process --renderer-client-id=12";

        var startPath = ProcessCommandLineResolver.FormatStartPathDisplay(commandLine, fallbackPath: null);

        Assert.StartsWith(@"C:\Users\user\AppData\Local\Google\Chrome\Application\chrome.exe", startPath);
        Assert.Contains("--type=renderer", startPath);
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
