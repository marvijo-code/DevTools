using DevTools.Models;
using DevTools.Services.Resources;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class ResourceAdvisorTests
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    private readonly ResourceAdvisor _advisor = new();

    [Fact]
    public void Analyze_ReturnsNothingWithoutSamples()
    {
        Assert.Empty(_advisor.Analyze(new ResourceHistory(10)));
    }

    [Fact]
    public void Analyze_FlagsCommitChargeNearTheLimitAsCritical()
    {
        var history = Fill(CreateSample(commitUsedBytes: 50 * Gigabyte, commitLimitBytes: 55 * Gigabyte));

        var finding = Assert.Single(
            _advisor.Analyze(history),
            advice => advice.Title.Contains("Commit charge", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ResourceAdviceSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Analyze_FlagsCommitRunningPastPhysicalRam()
    {
        var history = Fill(CreateSample(
            commitUsedBytes: 48 * Gigabyte,
            commitLimitBytes: 56 * Gigabyte,
            physicalTotalBytes: 16 * Gigabyte));

        var finding = Assert.Single(
            _advisor.Analyze(history),
            advice => advice.Title.Contains("physical RAM", StringComparison.OrdinalIgnoreCase) &&
                      advice.Title.Contains('x', StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ResourceAdviceSeverity.Critical, finding.Severity);
    }

    [Fact]
    public void Analyze_FlagsSustainedHardFaultsAsThrashing()
    {
        var history = Fill(CreateSample(hardFaultsPerSecond: 900));

        Assert.Contains(
            _advisor.Analyze(history),
            advice => advice.Title.Contains("Hard page faults", StringComparison.OrdinalIgnoreCase) &&
                      advice.Severity == ResourceAdviceSeverity.Critical);
    }

    [Fact]
    public void Analyze_FlagsASingleProcessHoldingMostOfRam()
    {
        var runaway = CreateProcess("bun", commitBytes: 12 * Gigabyte);
        var history = Fill(CreateSample(physicalTotalBytes: 16 * Gigabyte, topProcesses: [runaway]));

        var finding = Assert.Single(
            _advisor.Analyze(history),
            advice => advice.Title.StartsWith("bun holds", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ResourceAdviceSeverity.Critical, finding.Severity);
        Assert.Contains("Kill Processes tab", finding.Action, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_FlagsAntivirusScanningWhenItBurnsCpu()
    {
        var history = Fill(CreateSample(workloads:
        [
            CreateWorkload(WorkloadCategory.Security, processCount: 1, cpuPercent: 22)
        ]));

        Assert.Contains(
            _advisor.Analyze(history),
            advice => advice.Action.Contains("Defender exclusion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_FlagsARuntimeSwarmSpawnedByAgents()
    {
        var history = Fill(CreateSample(workloads:
        [
            CreateWorkload(WorkloadCategory.AgentRuntimes, processCount: 22, commitBytes: 4 * Gigabyte)
        ]));

        Assert.Contains(
            _advisor.Analyze(history),
            advice => advice.Title.Contains("agent runtime processes", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_WarnsWhenTheAgentFleetExceedsWhatRamSupports()
    {
        // 20 agents at 1 GB each against 16 GB of RAM: the 12 GB budget supports about 12.
        var history = Fill(CreateSample(
            physicalTotalBytes: 16 * Gigabyte,
            workloads:
            [
                CreateWorkload(WorkloadCategory.CodingAgents, processCount: 20, commitBytes: 20 * Gigabyte)
            ]));

        var finding = Assert.Single(
            _advisor.Analyze(history),
            advice => advice.Title.StartsWith("Agent fleet", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(ResourceAdviceSeverity.Warning, finding.Severity);
        Assert.Contains("ceiling of about 12", finding.Action, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Analyze_StaysQuietOnAHealthyMachine()
    {
        var history = Fill(CreateSample(
            cpuPercent: 20,
            commitUsedBytes: 8 * Gigabyte,
            commitLimitBytes: 56 * Gigabyte,
            physicalTotalBytes: 32 * Gigabyte,
            physicalAvailableBytes: 20 * Gigabyte));

        Assert.Empty(_advisor.Analyze(history));
    }

    [Fact]
    public void Analyze_OrdersFindingsBySeverity()
    {
        var history = Fill(CreateSample(
            commitUsedBytes: 50 * Gigabyte,
            commitLimitBytes: 55 * Gigabyte,
            physicalTotalBytes: 16 * Gigabyte,
            workloads:
            [
                CreateWorkload(WorkloadCategory.Builds, processCount: 3, commitBytes: 2 * Gigabyte)
            ]));

        var findings = _advisor.Analyze(history);

        Assert.Equal(ResourceAdviceSeverity.Critical, findings[0].Severity);
        Assert.Equal(ResourceAdviceSeverity.Info, findings[^1].Severity);
    }

    private static ResourceHistory Fill(ResourceSample sample)
    {
        // The advisor averages over a 30-sample window, so a condition must be sustained to register.
        var history = new ResourceHistory(60);
        for (var i = 0; i < 40; i++)
        {
            history.Add(sample);
        }

        return history;
    }

    private static ResourceSample CreateSample(
        double cpuPercent = 15,
        long commitUsedBytes = 8L * 1024 * 1024 * 1024,
        long commitLimitBytes = 56L * 1024 * 1024 * 1024,
        long physicalTotalBytes = 32L * 1024 * 1024 * 1024,
        long physicalAvailableBytes = 16L * 1024 * 1024 * 1024,
        double hardFaultsPerSecond = 0,
        IReadOnlyList<WorkloadUsage>? workloads = null,
        IReadOnlyList<ProcessUsage>? topProcesses = null)
    {
        return ResourceSample.Empty with
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            CpuPercent = cpuPercent,
            CommitUsedBytes = commitUsedBytes,
            CommitLimitBytes = commitLimitBytes,
            PhysicalTotalBytes = physicalTotalBytes,
            PhysicalAvailableBytes = physicalAvailableBytes,
            HardFaultsPerSecond = hardFaultsPerSecond,
            Workloads = workloads ?? [],
            TopProcesses = topProcesses ?? []
        };
    }

    private static WorkloadUsage CreateWorkload(
        WorkloadCategory category,
        int processCount,
        double cpuPercent = 1,
        long commitBytes = 0) =>
        new(
            category,
            WorkloadClassifier.DisplayName(category),
            processCount,
            cpuPercent,
            commitBytes,
            commitBytes,
            0,
            0,
            processCount * 4,
            "example",
            0);

    private static ProcessUsage CreateProcess(string imageName, long commitBytes) =>
        new(
            4242,
            imageName,
            WorkloadClassifier.Classify(imageName),
            1,
            commitBytes,
            commitBytes,
            0,
            0,
            8,
            0);
}
