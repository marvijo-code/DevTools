using DevTools.Services.Resources;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class WorkloadClassifierTests
{
    [Theory]
    [InlineData("claude.exe", WorkloadCategory.CodingAgents)]
    [InlineData("codex.exe", WorkloadCategory.CodingAgents)]
    [InlineData("opencode.exe", WorkloadCategory.CodingAgents)]
    [InlineData("node.exe", WorkloadCategory.AgentRuntimes)]
    [InlineData("bun.exe", WorkloadCategory.AgentRuntimes)]
    [InlineData("python.exe", WorkloadCategory.AgentRuntimes)]
    [InlineData("uv.exe", WorkloadCategory.AgentRuntimes)]
    [InlineData("esbuild.exe", WorkloadCategory.AgentRuntimes)]
    [InlineData("MSBuild.exe", WorkloadCategory.Builds)]
    [InlineData("VBCSCompiler.exe", WorkloadCategory.Builds)]
    [InlineData("dotnet.exe", WorkloadCategory.Builds)]
    [InlineData("bash.exe", WorkloadCategory.Shells)]
    [InlineData("pwsh.exe", WorkloadCategory.Shells)]
    [InlineData("conhost.exe", WorkloadCategory.Shells)]
    [InlineData("firefox.exe", WorkloadCategory.Browsers)]
    [InlineData("msedgewebview2.exe", WorkloadCategory.Browsers)]
    [InlineData("MsMpEng.exe", WorkloadCategory.Security)]
    [InlineData("vmmemWSL", WorkloadCategory.Virtualization)]
    [InlineData("SearchIndexer.exe", WorkloadCategory.WindowsMaintenance)]
    [InlineData("svchost.exe", WorkloadCategory.System)]
    [InlineData("Memory Compression", WorkloadCategory.System)]
    public void Classify_MapsKnownExecutablesToTheirWorkload(string imageName, WorkloadCategory expected)
    {
        Assert.Equal(expected, WorkloadClassifier.Classify(imageName));
    }

    [Fact]
    public void Classify_TreatsHeadlessChromeAsAnAgentRuntimeNotABrowser()
    {
        // Puppeteer-driven headless Chrome is spawned by agent tooling, so attributing it to the browser
        // bucket would hide it from the agent footprint the tab is meant to expose.
        Assert.Equal(WorkloadCategory.AgentRuntimes, WorkloadClassifier.Classify("chrome-headless-shell.exe"));
        Assert.Equal(WorkloadCategory.Browsers, WorkloadClassifier.Classify("chrome.exe"));
    }

    [Fact]
    public void Classify_PrefersTheMoreSpecificRuleForCodexOverCode()
    {
        Assert.Equal(WorkloadCategory.CodingAgents, WorkloadClassifier.Classify("codex-cli.exe"));
        Assert.Equal(WorkloadCategory.Editors, WorkloadClassifier.Classify("Code.exe"));
    }

    [Theory]
    [InlineData("python3.13.exe", WorkloadCategory.AgentRuntimes)]
    [InlineData("node-18.exe", WorkloadCategory.AgentRuntimes)]
    public void Classify_MatchesVersionedVariantsByPrefix(string imageName, WorkloadCategory expected)
    {
        Assert.Equal(expected, WorkloadClassifier.Classify(imageName));
    }

    [Fact]
    public void Classify_ReturnsOtherForUnknownAndEmptyNames()
    {
        Assert.Equal(WorkloadCategory.Other, WorkloadClassifier.Classify("SomeVendorWidget.exe"));
        Assert.Equal(WorkloadCategory.Other, WorkloadClassifier.Classify(null));
        Assert.Equal(WorkloadCategory.Other, WorkloadClassifier.Classify("   "));
    }

    [Theory]
    [InlineData(@"C:\Program Files\nodejs\node.exe", "node")]
    [InlineData("MsMpEng.exe", "MsMpEng")]
    [InlineData("Memory Compression", "Memory Compression")]
    [InlineData(null, "")]
    public void NormalizeImageName_StripsPathAndExtension(string? input, string expected)
    {
        Assert.Equal(expected, WorkloadClassifier.NormalizeImageName(input));
    }

    [Fact]
    public void IsDeveloperWorkload_SeparatesTheUsersWorkFromWindows()
    {
        Assert.True(WorkloadClassifier.IsDeveloperWorkload(WorkloadCategory.CodingAgents));
        Assert.True(WorkloadClassifier.IsDeveloperWorkload(WorkloadCategory.AgentRuntimes));
        Assert.True(WorkloadClassifier.IsDeveloperWorkload(WorkloadCategory.Builds));
        Assert.False(WorkloadClassifier.IsDeveloperWorkload(WorkloadCategory.System));
        Assert.False(WorkloadClassifier.IsDeveloperWorkload(WorkloadCategory.Security));
    }
}
