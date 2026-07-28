using System.Diagnostics;
using DevTools.Models;

namespace DevTools.Services.Resources;

/// <summary>
/// Turns two consecutive kernel snapshots into a single <see cref="ResourceSample"/>. All rates are
/// derived from deltas, so the first tick after construction produces no CPU or disk numbers - that is
/// expected and the caller simply shows the second sample onwards.
/// </summary>
public sealed class ResourceMonitor
{
    private const int TopProcessCount = 15;

    private readonly int _logicalProcessorCount = Math.Max(1, Environment.ProcessorCount);
    private readonly Dictionary<int, RawProcessCounters> _previousProcesses = new(512);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly VolumeSampler _volumeSampler = new();
    private readonly AgentSessionBuilder _sessionBuilder = new();

    private SystemCounters _previousSystem;
    private TimeSpan _previousElapsed;
    private bool _hasPreviousSample;

    /// <summary>True once a delta is available, i.e. from the second call to <see cref="Sample"/> on.</summary>
    public bool HasBaseline => _hasPreviousSample;

    public ResourceSample Sample()
    {
        var system = NativeResourceApi.ReadSystemCounters();
        var processes = NativeResourceApi.ReadProcessCounters();
        var elapsed = _clock.Elapsed;
        var intervalSeconds = (elapsed - _previousElapsed).TotalSeconds;

        if (!_hasPreviousSample || intervalSeconds <= 0)
        {
            CaptureBaseline(system, processes, elapsed);
            return ResourceSample.Empty with
            {
                Timestamp = DateTimeOffset.Now,
                PhysicalTotalBytes = system.PhysicalTotalBytes,
                PhysicalAvailableBytes = system.PhysicalAvailableBytes,
                CommitUsedBytes = system.CommitTotalBytes,
                CommitLimitBytes = system.CommitLimitBytes,
                ProcessCount = system.ProcessCount,
                ThreadCount = system.ThreadCount,
                HandleCount = system.HandleCount,
                Volumes = _volumeSampler.Sample()
            };
        }

        var cpuPercent = ComputeSystemCpuPercent(system);
        var (workloads, topProcesses, sessions, diskReadRate, diskWriteRate, hardFaultRate) =
            ComputeProcessUsage(processes, intervalSeconds);

        CaptureBaseline(system, processes, elapsed);

        return new ResourceSample(
            DateTimeOffset.Now,
            cpuPercent,
            system.PhysicalTotalBytes,
            system.PhysicalAvailableBytes,
            system.CommitTotalBytes,
            system.CommitLimitBytes,
            diskReadRate,
            diskWriteRate,
            hardFaultRate,
            system.ProcessCount,
            system.ThreadCount,
            system.HandleCount,
            workloads,
            topProcesses,
            _volumeSampler.Sample(),
            sessions);
    }

    private double ComputeSystemCpuPercent(SystemCounters system)
    {
        // GetSystemTimes reports kernel time inclusive of idle, so busy time is (kernel - idle) + user.
        var idleDelta = system.IdleTicks - _previousSystem.IdleTicks;
        var kernelDelta = system.KernelTicks - _previousSystem.KernelTicks;
        var userDelta = system.UserTicks - _previousSystem.UserTicks;
        var totalDelta = kernelDelta + userDelta;

        if (totalDelta <= 0)
        {
            return 0;
        }

        var busyDelta = totalDelta - idleDelta;
        return Math.Clamp(busyDelta * 100.0 / totalDelta, 0, 100);
    }

    private (
        IReadOnlyList<WorkloadUsage> Workloads,
        IReadOnlyList<ProcessUsage> TopProcesses,
        IReadOnlyList<AgentSessionUsage> Sessions,
        double DiskReadRate,
        double DiskWriteRate,
        double HardFaultRate) ComputeProcessUsage(
            IReadOnlyList<RawProcessCounters> processes,
            double intervalSeconds)
    {
        var usages = new List<ProcessUsage>(processes.Count);
        var totalReadRate = 0d;
        var totalWriteRate = 0d;
        var totalHardFaultRate = 0d;

        // 100ns CPU ticks over the interval, scaled by core count so 100% means "the whole machine".
        var cpuDenominator = intervalSeconds * _logicalProcessorCount * 10_000_000d;

        foreach (var process in processes)
        {
            if (process.ProcessId <= 0)
            {
                // PID 0 is the idle process; its CPU time is the inverse of what we want to show.
                continue;
            }

            var cpuPercent = 0d;
            var diskRate = 0d;
            var hardFaultRate = 0d;

            if (_previousProcesses.TryGetValue(process.ProcessId, out var previous) &&
                IsSameProcessInstance(previous, process))
            {
                var cpuDelta = process.CpuTicks - previous.CpuTicks;
                if (cpuDelta > 0 && cpuDenominator > 0)
                {
                    cpuPercent = Math.Clamp(cpuDelta * 100.0 / cpuDenominator, 0, 100);
                }

                var readDelta = Math.Max(0, process.ReadBytes - previous.ReadBytes);
                var writeDelta = Math.Max(0, process.WriteBytes - previous.WriteBytes);
                diskRate = (readDelta + writeDelta) / intervalSeconds;

                totalReadRate += readDelta / intervalSeconds;
                totalWriteRate += writeDelta / intervalSeconds;

                var faultDelta = Math.Max(0, process.HardFaultCount - previous.HardFaultCount);
                hardFaultRate = faultDelta / intervalSeconds;
                totalHardFaultRate += hardFaultRate;
            }

            var imageName = WorkloadClassifier.NormalizeImageName(process.ImageName);
            if (imageName.Length == 0)
            {
                imageName = $"PID {process.ProcessId}";
            }

            usages.Add(new ProcessUsage(
                process.ProcessId,
                imageName,
                WorkloadClassifier.Classify(process.ImageName),
                cpuPercent,
                process.CommitBytes,
                process.WorkingSetBytes,
                diskRate,
                hardFaultRate,
                process.ThreadCount,
                0));
        }

        // Shares are expressed against total committed memory, which is the resource under contention.
        var totalCommitBytes = usages.Sum(usage => usage.CommitBytes);

        var workloads = usages
            .GroupBy(usage => usage.Category)
            .Select(group => new WorkloadUsage(
                group.Key,
                WorkloadClassifier.DisplayName(group.Key),
                group.Count(),
                group.Sum(usage => usage.CpuPercent),
                group.Sum(usage => usage.CommitBytes),
                group.Sum(usage => usage.WorkingSetBytes),
                group.Sum(usage => usage.DiskBytesPerSecond),
                group.Sum(usage => usage.HardFaultsPerSecond),
                group.Sum(usage => usage.ThreadCount),
                group.OrderByDescending(usage => usage.CommitBytes).First().ImageName,
                ToSharePercent(group.Sum(usage => usage.CommitBytes), totalCommitBytes)))
            .OrderByDescending(workload => workload.CommitBytes)
            .ToList();

        // Rank by commit rather than working set: on a paging machine the working set of an idle-but-huge
        // process shrinks, which would hide the process actually consuming the memory budget.
        var topProcesses = usages
            .OrderByDescending(usage => usage.CommitBytes)
            .Take(TopProcessCount)
            .Select(usage => usage with { SharePercent = ToSharePercent(usage.CommitBytes, totalCommitBytes) })
            .ToList();

        var usageByPid = usages.ToDictionary(usage => usage.ProcessId);
        var sessions = _sessionBuilder.Build(processes, usageByPid);

        return (workloads, topProcesses, sessions, totalReadRate, totalWriteRate, totalHardFaultRate);
    }

    private static double ToSharePercent(long bytes, long totalBytes)
    {
        return totalBytes <= 0 ? 0 : Math.Clamp(bytes * 100.0 / totalBytes, 0, 100);
    }

    /// <summary>
    /// Guards against PID reuse: if a PID was recycled, its counters go backwards, so treat the entry as
    /// a brand new process instead of reporting a nonsense negative delta.
    /// </summary>
    private static bool IsSameProcessInstance(RawProcessCounters previous, RawProcessCounters current)
    {
        return current.CpuTicks >= previous.CpuTicks &&
               string.Equals(previous.ImageName, current.ImageName, StringComparison.OrdinalIgnoreCase);
    }

    private void CaptureBaseline(
        SystemCounters system,
        IReadOnlyList<RawProcessCounters> processes,
        TimeSpan elapsed)
    {
        _previousSystem = system;
        _previousElapsed = elapsed;

        _previousProcesses.Clear();
        foreach (var process in processes)
        {
            _previousProcesses[process.ProcessId] = process;
        }

        _hasPreviousSample = processes.Count > 0;
    }
}
