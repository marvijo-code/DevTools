using DevTools.Infrastructure;
using DevTools.Services.Resources;

namespace DevTools.Models;

/// <summary>Free and used space on one fixed volume.</summary>
public sealed record VolumeUsage(
    string Name,
    string Label,
    long TotalBytes,
    long FreeBytes)
{
    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    public double UsedPercent => TotalBytes <= 0
        ? 0
        : Math.Clamp(UsedBytes * 100.0 / TotalBytes, 0, 100);

    public double FreePercent => TotalBytes <= 0
        ? 0
        : Math.Clamp(FreeBytes * 100.0 / TotalBytes, 0, 100);

    public string HeadlineText => $"{Name} ({Label})  —  {ByteFormatter.Format(FreeBytes)} free";

    public string DetailText =>
        $"{ByteFormatter.Format(UsedBytes)} used of {ByteFormatter.Format(TotalBytes)} · {UsedPercent:N0}% full · {FreePercent:N0}% free";
}

/// <summary>Usage attributed to a single process between two ticks.</summary>
public sealed record ProcessUsage(
    int ProcessId,
    string ImageName,
    WorkloadCategory Category,
    double CpuPercent,
    long CommitBytes,
    long WorkingSetBytes,
    double DiskBytesPerSecond,
    double HardFaultsPerSecond,
    int ThreadCount,
    double SharePercent)
{
    public string Title => $"{ImageName}  (PID {ProcessId})";

    public string CategoryName => WorkloadClassifier.DisplayName(Category);

    public string CommitText => ByteFormatter.Format(CommitBytes);

    public string WorkingSetText => ByteFormatter.Format(WorkingSetBytes);

    public string CpuText => $"{CpuPercent:N1}% CPU";

    public string DiskText => $"{ByteFormatter.Format((long)DiskBytesPerSecond)}/s";

    public string DetailText =>
        $"{CategoryName} · commit {CommitText} · working set {WorkingSetText} · {CpuText} · disk {DiskText} · {ThreadCount} threads";
}

/// <summary>Usage rolled up across every process in one workload category.</summary>
public sealed record WorkloadUsage(
    WorkloadCategory Category,
    string DisplayName,
    int ProcessCount,
    double CpuPercent,
    long CommitBytes,
    long WorkingSetBytes,
    double DiskBytesPerSecond,
    double HardFaultsPerSecond,
    int ThreadCount,
    string TopProcessName,
    double SharePercent)
{
    public string CommitText => ByteFormatter.Format(CommitBytes);

    public string HeadlineText => $"{DisplayName}  —  {CommitText}";

    public string DetailText =>
        $"{ProcessCount} process(es) · {CpuPercent:N1}% CPU · working set {ByteFormatter.Format(WorkingSetBytes)} · " +
        $"disk {ByteFormatter.Format((long)DiskBytesPerSecond)}/s · {ThreadCount} threads · largest: {TopProcessName}";
}

/// <summary>
/// One point in time for the whole machine. <see cref="CommitPercent"/> matters more than
/// <see cref="PhysicalUsedPercent"/> on a memory-tight box: physical RAM always reads near full because
/// Windows caches, whereas commit running at the limit is what actually stalls processes.
/// </summary>
public sealed record ResourceSample(
    DateTimeOffset Timestamp,
    double CpuPercent,
    long PhysicalTotalBytes,
    long PhysicalAvailableBytes,
    long CommitUsedBytes,
    long CommitLimitBytes,
    double DiskReadBytesPerSecond,
    double DiskWriteBytesPerSecond,
    double HardFaultsPerSecond,
    int ProcessCount,
    int ThreadCount,
    int HandleCount,
    IReadOnlyList<WorkloadUsage> Workloads,
    IReadOnlyList<ProcessUsage> TopProcesses,
    IReadOnlyList<VolumeUsage> Volumes,
    IReadOnlyList<AgentSessionUsage> Sessions)
{
    /// <summary>The volume Windows is installed on, which is the one that hurts when it fills up.</summary>
    public VolumeUsage? SystemVolume
    {
        get
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
            return Volumes.FirstOrDefault(volume =>
                       string.Equals(volume.Name, systemRoot, StringComparison.OrdinalIgnoreCase)) ??
                   Volumes.FirstOrDefault();
        }
    }

    public long PhysicalUsedBytes => Math.Max(0, PhysicalTotalBytes - PhysicalAvailableBytes);

    public double PhysicalUsedPercent => PhysicalTotalBytes <= 0
        ? 0
        : Math.Clamp(PhysicalUsedBytes * 100.0 / PhysicalTotalBytes, 0, 100);

    public double CommitPercent => CommitLimitBytes <= 0
        ? 0
        : Math.Clamp(CommitUsedBytes * 100.0 / CommitLimitBytes, 0, 100);

    /// <summary>
    /// How far commit has run past physical RAM. Above 1.0 the machine is backing memory with the page
    /// file, which is where "my PC should handle this but it crawls" comes from.
    /// </summary>
    public double CommitToPhysicalRatio => PhysicalTotalBytes <= 0
        ? 0
        : CommitUsedBytes / (double)PhysicalTotalBytes;

    public double DiskBytesPerSecond => DiskReadBytesPerSecond + DiskWriteBytesPerSecond;

    public static ResourceSample Empty { get; } = new(
        DateTimeOffset.MinValue,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        [],
        [],
        [],
        []);
}
