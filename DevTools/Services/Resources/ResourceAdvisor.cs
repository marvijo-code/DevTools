using DevTools.Infrastructure;
using DevTools.Models;

namespace DevTools.Services.Resources;

/// <summary>
/// Reads the sampled history and reports what is actually constraining the machine, in the order that
/// fixing it would help. The rules are tuned for a multi-agent coding workflow: the usual culprit is not
/// CPU but commit charge running past physical RAM, which shows up as paging rather than as a busy CPU.
/// </summary>
public sealed class ResourceAdvisor
{
    /// <summary>Samples to average over when deciding whether a condition is sustained rather than a spike.</summary>
    private const int SustainedWindow = 30;

    private const double CommitCriticalPercent = 88;
    private const double CommitWarningPercent = 75;
    private const double CpuSustainedPercent = 85;
    private const double HardFaultCriticalRate = 400;
    private const double HardFaultWarningRate = 120;
    private const long LowPhysicalBytes = 1L * 1024 * 1024 * 1024;
    private const int RuntimeProcessWarningCount = 15;
    private const long CriticalFreeBytes = 10L * 1024 * 1024 * 1024;
    private const long WarningFreeBytes = 25L * 1024 * 1024 * 1024;
    private const long FillRateWarningBytesPerHour = 2L * 1024 * 1024 * 1024;

    /// <summary>Minimum window before a disk-space slope is worth extrapolating from.</summary>
    private const int MinimumTrendSamples = 120;

    public IReadOnlyList<ResourceAdvice> Analyze(ResourceHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var latest = history.Latest;
        if (latest is null || latest.PhysicalTotalBytes <= 0)
        {
            return [];
        }

        var findings = new List<ResourceAdvice>();

        AddMemoryFindings(findings, history, latest);
        AddPagingFindings(findings, history, latest);
        AddDiskSpaceFindings(findings, history, latest);
        AddCpuFindings(findings, history, latest);
        AddRunawayProcessFindings(findings, latest);
        AddStaleSessionFindings(findings, latest);
        AddWorkloadFindings(findings, latest);
        AddConcurrencyGuidance(findings, latest);

        return findings
            .OrderByDescending(finding => finding.Severity)
            .ToList();
    }

    private static void AddMemoryFindings(List<ResourceAdvice> findings, ResourceHistory history, ResourceSample latest)
    {
        var commitPercent = history.Average(sample => sample.CommitPercent, SustainedWindow);

        if (commitPercent >= CommitCriticalPercent)
        {
            findings.Add(new ResourceAdvice(
                ResourceAdviceSeverity.Critical,
                $"Commit charge at {commitPercent:N0}% of the limit",
                $"{ByteFormatter.Format(latest.CommitUsedBytes)} committed against a {ByteFormatter.Format(latest.CommitLimitBytes)} limit. " +
                "Once this reaches 100% Windows starts refusing allocations and processes crash rather than slow down.",
                "Close the largest committed processes, or raise the page file ceiling. Long term this is a signal to run fewer agents at once."));
        }
        else if (commitPercent >= CommitWarningPercent)
        {
            findings.Add(new ResourceAdvice(
                ResourceAdviceSeverity.Warning,
                $"Commit charge at {commitPercent:N0}% of the limit",
                $"{ByteFormatter.Format(latest.CommitUsedBytes)} committed against a {ByteFormatter.Format(latest.CommitLimitBytes)} limit.",
                "There is headroom left, but starting another agent fleet will push this into the danger zone."));
        }

        var ratio = latest.CommitToPhysicalRatio;
        if (ratio > 1.25)
        {
            findings.Add(new ResourceAdvice(
                ratio > 2.0 ? ResourceAdviceSeverity.Critical : ResourceAdviceSeverity.Warning,
                $"Committed memory is {ratio:N1}x physical RAM",
                $"{ByteFormatter.Format(latest.CommitUsedBytes)} committed on a machine with {ByteFormatter.Format(latest.PhysicalTotalBytes)} of RAM. " +
                "The difference is backed by the page file, so memory access that should cost nanoseconds costs a disk round trip instead.",
                "This is why the machine feels slow while the CPU looks idle. Reduce concurrent memory-heavy processes before adding more cores of work."));
        }

        if (latest.PhysicalAvailableBytes < LowPhysicalBytes)
        {
            findings.Add(new ResourceAdvice(
                ResourceAdviceSeverity.Critical,
                $"Only {ByteFormatter.Format(latest.PhysicalAvailableBytes)} of physical RAM available",
                $"{ByteFormatter.Format(latest.PhysicalUsedBytes)} of {ByteFormatter.Format(latest.PhysicalTotalBytes)} is in use. " +
                "Windows is evicting working sets to keep up, which shows up as every app being sluggish at once.",
                "Free memory by closing the top committed processes below, then re-check before starting new work."));
        }
    }

    private static void AddPagingFindings(List<ResourceAdvice> findings, ResourceHistory history, ResourceSample latest)
    {
        var hardFaultRate = history.Average(sample => sample.HardFaultsPerSecond, SustainedWindow);

        if (hardFaultRate >= HardFaultCriticalRate)
        {
            findings.Add(new ResourceAdvice(
                ResourceAdviceSeverity.Critical,
                $"Hard page faults averaging {hardFaultRate:N0}/s",
                "Sustained hard faults mean the machine is thrashing: it is repeatedly reading memory back from the page file. " +
                "This is the single largest cause of an unresponsive machine that still reports spare CPU.",
                "Cut memory pressure first. Nothing else you tune will matter while this number stays high."));
        }
        else if (hardFaultRate >= HardFaultWarningRate)
        {
            findings.Add(new ResourceAdvice(
                ResourceAdviceSeverity.Warning,
                $"Hard page faults averaging {hardFaultRate:N0}/s",
                "The machine is paging steadily. Interactive latency will be noticeably worse than the CPU graph suggests.",
                "Trim the largest committed processes to bring the working set back under physical RAM."));
        }

        var diskRate = history.Average(sample => sample.DiskBytesPerSecond, SustainedWindow);
        if (diskRate > 80L * 1024 * 1024)
        {
            var topDiskProcess = latest.TopProcesses
                .OrderByDescending(process => process.DiskBytesPerSecond)
                .FirstOrDefault();

            findings.Add(new ResourceAdvice(
                ResourceAdviceSeverity.Warning,
                $"Sustained disk I/O of {ByteFormatter.Format((long)diskRate)}/s",
                topDiskProcess is null
                    ? "Heavy sustained I/O will slow every file operation your agents perform."
                    : $"Largest current contributor: {topDiskProcess.ImageName} (PID {topDiskProcess.ProcessId}) at {ByteFormatter.Format((long)topDiskProcess.DiskBytesPerSecond)}/s.",
                "Check for a runaway scan, an indexer, or antivirus reading every file your agents touch."));
        }
    }

    /// <summary>
    /// Free space matters twice over here: a full system volume breaks builds outright, and it also caps
    /// the page file, which is the only thing keeping an oversubscribed machine alive.
    /// </summary>
    private static void AddDiskSpaceFindings(List<ResourceAdvice> findings, ResourceHistory history, ResourceSample latest)
    {
        foreach (var volume in latest.Volumes)
        {
            var isSystemVolume = ReferenceEquals(volume, latest.SystemVolume);

            if (volume.FreeBytes < CriticalFreeBytes || volume.FreePercent < 5)
            {
                findings.Add(new ResourceAdvice(
                    ResourceAdviceSeverity.Critical,
                    $"{volume.Name} has only {ByteFormatter.Format(volume.FreeBytes)} free ({volume.FreePercent:N0}%)",
                    isSystemVolume
                        ? "This is the system volume, so the page file cannot grow either. Builds, restores and Windows itself will start failing."
                        : "Builds and package restores on this volume will start failing.",
                    "Reclaim space now - the Disk Usage and Biggest Files tabs will find the largest offenders."));
            }
            else if (volume.FreeBytes < WarningFreeBytes || volume.FreePercent < 12)
            {
                findings.Add(new ResourceAdvice(
                    ResourceAdviceSeverity.Warning,
                    $"{volume.Name} is {volume.UsedPercent:N0}% full ({ByteFormatter.Format(volume.FreeBytes)} free)",
                    "Node module trees, build outputs and package caches grow quickly across several repos.",
                    "Clear build outputs and package caches from the Disk Usage tab before it becomes urgent."));
            }

            AddFillRateFinding(findings, history, volume);
        }
    }

    /// <summary>
    /// Projects when a volume runs out based on the trend across the sampled window. Only reports when the
    /// window is long enough for the slope to mean anything and the drive is genuinely filling.
    /// </summary>
    private static void AddFillRateFinding(List<ResourceAdvice> findings, ResourceHistory history, VolumeUsage volume)
    {
        var samples = history.Snapshot();
        if (samples.Count < MinimumTrendSamples)
        {
            return;
        }

        var oldest = FindVolume(samples[0], volume.Name);
        var newest = FindVolume(samples[^1], volume.Name);
        if (oldest is null || newest is null)
        {
            return;
        }

        var elapsed = samples[^1].Timestamp - samples[0].Timestamp;
        if (elapsed <= TimeSpan.Zero)
        {
            return;
        }

        var consumedBytes = oldest.FreeBytes - newest.FreeBytes;
        var bytesPerHour = consumedBytes / elapsed.TotalHours;

        if (bytesPerHour < FillRateWarningBytesPerHour)
        {
            return;
        }

        var hoursRemaining = newest.FreeBytes / bytesPerHour;

        findings.Add(new ResourceAdvice(
            hoursRemaining < 12 ? ResourceAdviceSeverity.Critical : ResourceAdviceSeverity.Warning,
            $"{volume.Name} is filling at {ByteFormatter.Format((long)bytesPerHour)}/hour",
            $"Free space dropped by {ByteFormatter.Format(consumedBytes)} over the last {FormatDuration(elapsed)}. " +
            $"At this rate the volume runs out in roughly {FormatDuration(TimeSpan.FromHours(hoursRemaining))}.",
            "Find what is writing - a runaway log, a build loop, or a container layer cache - rather than just deleting files."));
    }

    private static VolumeUsage? FindVolume(ResourceSample sample, string name)
    {
        return sample.Volumes.FirstOrDefault(volume =>
            string.Equals(volume.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 24)
        {
            return $"{duration.TotalDays:N1} days";
        }

        return duration.TotalHours >= 1
            ? $"{duration.TotalHours:N1} hours"
            : $"{Math.Max(1, duration.TotalMinutes):N0} minutes";
    }

    private static void AddCpuFindings(List<ResourceAdvice> findings, ResourceHistory history, ResourceSample latest)
    {
        var cpuPercent = history.Average(sample => sample.CpuPercent, SustainedWindow);

        if (cpuPercent >= CpuSustainedPercent)
        {
            var topCpu = latest.Workloads
                .OrderByDescending(workload => workload.CpuPercent)
                .FirstOrDefault();

            findings.Add(new ResourceAdvice(
                ResourceAdviceSeverity.Warning,
                $"CPU sustained at {cpuPercent:N0}%",
                topCpu is null
                    ? "All cores are saturated."
                    : $"Largest consumer right now: {topCpu.DisplayName} at {topCpu.CpuPercent:N0}% of the machine.",
                "This one is genuine CPU saturation - reduce parallel builds or test runners rather than memory."));
        }
        else if (cpuPercent < 40 && latest.CommitToPhysicalRatio > 1.25)
        {
            findings.Add(new ResourceAdvice(
                ResourceAdviceSeverity.Info,
                $"CPU is only {cpuPercent:N0}% while memory is oversubscribed",
                "The processor has headroom the memory subsystem cannot feed. Adding more parallel agents will not use that headroom, it will deepen the paging.",
                "Treat RAM, not cores, as the limit when deciding how many agents to run."));
        }
    }

    private static void AddRunawayProcessFindings(List<ResourceAdvice> findings, ResourceSample latest)
    {
        if (latest.PhysicalTotalBytes <= 0)
        {
            return;
        }

        foreach (var process in latest.TopProcesses.Take(5))
        {
            var shareOfRam = process.CommitBytes / (double)latest.PhysicalTotalBytes;
            if (shareOfRam < 0.20)
            {
                continue;
            }

            findings.Add(new ResourceAdvice(
                shareOfRam >= 0.50 ? ResourceAdviceSeverity.Critical : ResourceAdviceSeverity.Warning,
                $"{process.ImageName} holds {ByteFormatter.Format(process.CommitBytes)} ({shareOfRam:P0} of RAM)",
                $"PID {process.ProcessId} has committed more than a fifth of the machine's physical memory on its own. " +
                "A single process at this size is usually a leak or a stuck job rather than real work.",
                $"Verify what PID {process.ProcessId} is doing, and kill it from the Kill Processes tab if it is orphaned."));
        }
    }

    /// <summary>
    /// A session that has been alive for many hours and is holding serious memory is almost always a
    /// forgotten dev server or an abandoned agent run rather than active work. These are the cheapest wins
    /// available: reclaiming them costs nothing anyone is currently using.
    /// </summary>
    private static void AddStaleSessionFindings(List<ResourceAdvice> findings, ResourceSample latest)
    {
        if (latest.PhysicalTotalBytes <= 0)
        {
            return;
        }

        foreach (var session in latest.Sessions.Where(session => session.IsLongLived).Take(4))
        {
            var shareOfRam = session.CommitBytes / (double)latest.PhysicalTotalBytes;
            if (shareOfRam < 0.10)
            {
                continue;
            }

            findings.Add(new ResourceAdvice(
                shareOfRam >= 0.35 ? ResourceAdviceSeverity.Critical : ResourceAdviceSeverity.Warning,
                $"Stale session: {session.DisplayName} holding {session.CommitText} after {session.UptimeText}",
                $"{session.ProcessCount} process(es) rooted at PID {session.RootProcessId} in {session.WorkspaceText}, " +
                $"running for {session.UptimeText} and holding {shareOfRam:P0} of physical RAM. " +
                "Long-lived dev servers leak steadily; this is usually reclaimable memory nobody is using.",
                $"Confirm it is not in use, then kill PID {session.RootProcessId} and restart it when you actually need it."));
        }
    }

    private static void AddWorkloadFindings(List<ResourceAdvice> findings, ResourceSample latest)
    {
        foreach (var workload in latest.Workloads)
        {
            switch (workload.Category)
            {
                case WorkloadCategory.Security when workload.CpuPercent > 8 || workload.DiskBytesPerSecond > 20L * 1024 * 1024:
                    findings.Add(new ResourceAdvice(
                        ResourceAdviceSeverity.Warning,
                        $"Antivirus scanning is using {workload.CpuPercent:N0}% CPU",
                        "Real-time scanning inspects every file your agents read and write, which multiplies with the number of agents running.",
                        "Add your repo roots, package caches and toolchain folders to the Defender exclusion list."));
                    break;

                case WorkloadCategory.AgentRuntimes when workload.ProcessCount >= RuntimeProcessWarningCount:
                    findings.Add(new ResourceAdvice(
                        ResourceAdviceSeverity.Warning,
                        $"{workload.ProcessCount} agent runtime processes holding {ByteFormatter.Format(workload.CommitBytes)}",
                        "Every agent session starts its own set of MCP servers and language servers. These multiply per session, not per machine, and they outlive sessions that exited badly.",
                        "Trim the MCP servers loaded per session, and sweep orphaned runtimes between runs."));
                    break;

                case WorkloadCategory.Builds when workload.CommitBytes > 1L * 1024 * 1024 * 1024:
                    findings.Add(new ResourceAdvice(
                        ResourceAdviceSeverity.Info,
                        $"Build servers holding {ByteFormatter.Format(workload.CommitBytes)}",
                        $"{workload.ProcessCount} compiler and build-server processes stay resident between builds to keep them warm.",
                        "Worth reclaiming when memory is tight, at the cost of a slower next build."));
                    break;

                case WorkloadCategory.WindowsMaintenance when workload.CpuPercent > 5 || workload.DiskBytesPerSecond > 20L * 1024 * 1024:
                    findings.Add(new ResourceAdvice(
                        ResourceAdviceSeverity.Info,
                        $"Windows background maintenance active ({workload.TopProcessName})",
                        $"Indexing and update tasks are using {workload.CpuPercent:N0}% CPU and {ByteFormatter.Format((long)workload.DiskBytesPerSecond)}/s of disk.",
                        "Exclude source and build directories from Windows Search indexing."));
                    break;

                case WorkloadCategory.Browsers when workload.CommitBytes > 3L * 1024 * 1024 * 1024:
                    findings.Add(new ResourceAdvice(
                        ResourceAdviceSeverity.Info,
                        $"Browsers holding {ByteFormatter.Format(workload.CommitBytes)}",
                        $"{workload.ProcessCount} browser processes are resident while agents compete for the same memory.",
                        "Browser memory is the cheapest to reclaim when you need headroom for a build."));
                    break;
            }
        }
    }

    private static void AddConcurrencyGuidance(List<ResourceAdvice> findings, ResourceSample latest)
    {
        var agents = latest.Workloads.FirstOrDefault(workload => workload.Category == WorkloadCategory.CodingAgents);
        if (agents is null || agents.ProcessCount == 0)
        {
            return;
        }

        var runtimes = latest.Workloads.FirstOrDefault(workload => workload.Category == WorkloadCategory.AgentRuntimes);
        var agentFootprint = agents.CommitBytes + (runtimes?.CommitBytes ?? 0);

        // Leave roughly a quarter of RAM for Windows, the browser and the editor before dividing the rest.
        var budget = (long)(latest.PhysicalTotalBytes * 0.75);
        var perAgentBytes = agentFootprint / Math.Max(1, agents.ProcessCount);

        if (perAgentBytes <= 0)
        {
            return;
        }

        var supported = Math.Max(1, (int)(budget / perAgentBytes));

        findings.Add(new ResourceAdvice(
            supported < agents.ProcessCount ? ResourceAdviceSeverity.Warning : ResourceAdviceSeverity.Info,
            $"Agent fleet: {agents.ProcessCount} processes at {ByteFormatter.Format(perAgentBytes)} each",
            $"Agents plus their runtimes hold {ByteFormatter.Format(agentFootprint)}. " +
            $"With {ByteFormatter.Format(latest.PhysicalTotalBytes)} of RAM and a quarter reserved for the rest of the system, " +
            $"this machine sustains roughly {supported} concurrent agent processes without paging.",
            supported < agents.ProcessCount
                ? $"You are running {agents.ProcessCount} against a comfortable ceiling of about {supported}. Stagger runs or cut the per-session footprint."
                : "Current concurrency fits within physical RAM."));
    }
}
