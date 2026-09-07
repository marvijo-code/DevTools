using System.Collections.ObjectModel;
using DevTools.Infrastructure;
using DevTools.Models;
using DevTools.Services.Resources;

namespace DevTools.ViewModels;

/// <summary>
/// Resource monitoring tab. Sampling runs continuously while the app is open rather than only while the
/// tab is visible, so switching to it after something has already gone wrong still shows the history that
/// explains it.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Ten minutes of history at the one-second sampling interval.</summary>
    private const int ResourceHistoryCapacity = 600;

    private static readonly IReadOnlyList<int> ResourceIntervalChoices = [1, 2, 5];

    private readonly ResourceMonitor _resourceMonitor = new();
    private readonly ResourceAdvisor _resourceAdvisor = new();
    private readonly ResourceHistory _resourceHistory = new(ResourceHistoryCapacity);
    private readonly SemaphoreSlim _resourceSampleGate = new(1, 1);
    private readonly ObservableCollection<WorkloadUsage> _resourceWorkloads = [];
    private readonly ObservableCollection<ProcessUsage> _resourceTopProcesses = [];
    private readonly ObservableCollection<ResourceAdvice> _resourceAdvice = [];
    private readonly ObservableCollection<VolumeUsage> _resourceVolumes = [];
    private readonly ObservableCollection<AgentSessionEntry> _agentSessions = [];

    private IReadOnlyList<double> _cpuSeries = [];
    private IReadOnlyList<double> _commitSeries = [];
    private IReadOnlyList<double> _memorySeries = [];
    private IReadOnlyList<double> _diskSeries = [];
    private IReadOnlyList<double> _hardFaultSeries = [];
    private IReadOnlyList<double> _diskFreeSeries = [];

    private string _cpuValueText = "--";
    private string _commitValueText = "--";
    private string _memoryValueText = "--";
    private string _diskValueText = "--";
    private string _hardFaultValueText = "--";
    private string _diskFreeValueText = "--";
    private string _agentSessionSummaryText = "No agent sessions detected yet.";
    private string _resourceVerdictText = "Collecting the first samples...";
    private string _resourceStatusText = "Monitor starting.";
    private string _resourceCountsText = "--";
    private bool _resourceMonitoringEnabled = true;
    private int _resourceIntervalSeconds = 1;

    public ObservableCollection<WorkloadUsage> ResourceWorkloads => _resourceWorkloads;

    public ObservableCollection<ProcessUsage> ResourceTopProcesses => _resourceTopProcesses;

    public ObservableCollection<ResourceAdvice> ResourceAdvice => _resourceAdvice;

    public ObservableCollection<VolumeUsage> ResourceVolumes => _resourceVolumes;

    public ObservableCollection<AgentSessionEntry> AgentSessions => _agentSessions;

    public IReadOnlyList<int> ResourceIntervalOptions => ResourceIntervalChoices;

    public IReadOnlyList<double> CpuSeries => _cpuSeries;

    public IReadOnlyList<double> CommitSeries => _commitSeries;

    public IReadOnlyList<double> MemorySeries => _memorySeries;

    public IReadOnlyList<double> DiskSeries => _diskSeries;

    public IReadOnlyList<double> HardFaultSeries => _hardFaultSeries;

    public IReadOnlyList<double> DiskFreeSeries => _diskFreeSeries;

    public string CpuValueText => _cpuValueText;

    public string CommitValueText => _commitValueText;

    public string MemoryValueText => _memoryValueText;

    public string DiskValueText => _diskValueText;

    public string HardFaultValueText => _hardFaultValueText;

    public string DiskFreeValueText => _diskFreeValueText;

    public string AgentSessionSummaryText => _agentSessionSummaryText;

    /// <summary>The four biggest sessions, for the compact context panel.</summary>
    public IReadOnlyList<AgentSessionEntry> TopAgentSessions => _agentSessions.Take(4).ToList();

    public double CommitPercentValue => _resourceHistory.Latest?.CommitPercent ?? 0;

    public double MemoryPercentValue => _resourceHistory.Latest?.PhysicalUsedPercent ?? 0;

    public string MachineSpecText
    {
        get
        {
            var latest = _resourceHistory.Latest;
            if (latest is null || latest.PhysicalTotalBytes <= 0)
            {
                return $"{Environment.ProcessorCount} logical processors";
            }

            return $"{Environment.ProcessorCount} logical processors{Environment.NewLine}" +
                   $"{ByteFormatter.Format(latest.PhysicalTotalBytes)} physical RAM{Environment.NewLine}" +
                   $"{ByteFormatter.Format(latest.CommitLimitBytes)} commit limit (RAM + page file){Environment.NewLine}" +
                   $"{latest.ProcessCount:N0} processes, {latest.ThreadCount:N0} threads";
        }
    }

    /// <summary>Title of the shared right-hand panel, which follows whichever tab is active.</summary>
    public string ContextPaneTitle => WorkspaceContext.For(_activeTab).Title;

    public string ContextPaneSubtitle => _activeTab switch
    {
        MainContentTab.Resources => ResourceCountsText,
        MainContentTab.WorkflowExplorer => SelectedRun?.Repository.FullName ?? "Select a workflow run to see its details.",
        MainContentTab.KillProcesses => SelectedProcess?.Note ?? "Select a process row to inspect it here.",
        _ => "Disk usage and scan information for this machine."
    };

    public string ResourceVerdictText => _resourceVerdictText;

    public string ResourceStatusText => _resourceStatusText;

    public string ResourceCountsText => _resourceCountsText;

    public bool ResourceMonitoringEnabled
    {
        get => _resourceMonitoringEnabled;
        set
        {
            if (SetProperty(ref _resourceMonitoringEnabled, value))
            {
                SetResourceStatus(value ? "Monitoring resumed." : "Monitoring paused.");
                SaveSettings();
            }
        }
    }

    public int ResourceIntervalSeconds
    {
        get => _resourceIntervalSeconds;
        set
        {
            var normalized = ResourceIntervalChoices.Contains(value) ? value : 1;
            if (SetProperty(ref _resourceIntervalSeconds, normalized))
            {
                SaveSettings();
            }
        }
    }

    public string ResourceWindowText =>
        $"Last {FormatWindow(_resourceHistory.Count * _resourceIntervalSeconds)} · {_resourceHistory.Count} samples";

    /// <summary>
    /// Takes one sample and refreshes everything the tab shows. The kernel query runs off the UI thread
    /// because it walks every process; the collection updates then resume on the dispatcher.
    /// </summary>
    public async Task SampleResourcesAsync()
    {
        if (!_resourceMonitoringEnabled || !await _resourceSampleGate.WaitAsync(0))
        {
            return;
        }

        try
        {
            var sample = await Task.Run(_resourceMonitor.Sample);

            if (sample.PhysicalTotalBytes <= 0)
            {
                SetResourceStatus("Kernel counters unavailable on this tick.");
                return;
            }

            if (!_resourceMonitor.HasBaseline)
            {
                SetResourceStatus("Establishing baseline...");
                return;
            }

            _resourceHistory.Add(sample);
            UpdateSeries();
            UpdateValueText(sample);
            SynchronizeWorkloads(sample);
            SynchronizeTopProcesses(sample);
            SynchronizeVolumes(sample);
            SynchronizeSessions(sample);
            UpdateAdvice();
            SetResourceStatus($"Sampling every {_resourceIntervalSeconds}s · updated {sample.Timestamp:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            AppLog.Error("SampleResourcesAsync failed.", ex);
            SetResourceStatus($"Sampling failed: {ex.Message}");
        }
        finally
        {
            _resourceSampleGate.Release();
        }
    }

    public void ResetResourceHistory()
    {
        _resourceHistory.Clear();
        _resourceWorkloads.Clear();
        _resourceTopProcesses.Clear();
        _resourceAdvice.Clear();
        _resourceVolumes.Clear();
        _agentSessions.Clear();
        UpdateSeries();
        SetResourceStatus("History cleared.");
    }

    private void UpdateSeries()
    {
        _cpuSeries = _resourceHistory.Series(sample => sample.CpuPercent);
        _commitSeries = _resourceHistory.Series(sample => sample.CommitPercent);
        _memorySeries = _resourceHistory.Series(sample => sample.PhysicalUsedPercent);
        _diskSeries = _resourceHistory.Series(sample => sample.DiskBytesPerSecond / (1024d * 1024d));
        _hardFaultSeries = _resourceHistory.Series(sample => sample.HardFaultsPerSecond);
        _diskFreeSeries = _resourceHistory.Series(sample =>
            (sample.SystemVolume?.FreeBytes ?? 0) / (1024d * 1024d * 1024d));

        RaisePropertyChanged(nameof(DiskFreeSeries));
        RaisePropertyChanged(nameof(CpuSeries));
        RaisePropertyChanged(nameof(CommitSeries));
        RaisePropertyChanged(nameof(MemorySeries));
        RaisePropertyChanged(nameof(DiskSeries));
        RaisePropertyChanged(nameof(HardFaultSeries));
        RaisePropertyChanged(nameof(ResourceWindowText));
    }

    private void UpdateValueText(ResourceSample sample)
    {
        _cpuValueText = $"{sample.CpuPercent:N0}%  (avg {_resourceHistory.Average(item => item.CpuPercent, 60):N0}%)";
        _commitValueText =
            $"{ByteFormatter.Format(sample.CommitUsedBytes)} of {ByteFormatter.Format(sample.CommitLimitBytes)}  ({sample.CommitPercent:N0}%)";
        _memoryValueText =
            $"{ByteFormatter.Format(sample.PhysicalUsedBytes)} of {ByteFormatter.Format(sample.PhysicalTotalBytes)}  ({sample.PhysicalUsedPercent:N0}%)";
        _diskValueText = $"{ByteFormatter.Format((long)sample.DiskBytesPerSecond)}/s  (read {ByteFormatter.Format((long)sample.DiskReadBytesPerSecond)}/s)";
        _hardFaultValueText = $"{sample.HardFaultsPerSecond:N0}/s  (avg {_resourceHistory.Average(item => item.HardFaultsPerSecond, 60):N0}/s)";
        _resourceCountsText =
            $"{sample.ProcessCount:N0} processes · {sample.ThreadCount:N0} threads · {sample.HandleCount:N0} handles";
        _resourceVerdictText = BuildVerdict(sample);

        var systemVolume = sample.SystemVolume;
        _diskFreeValueText = systemVolume is null
            ? "no fixed volume"
            : $"{systemVolume.Name} {ByteFormatter.Format(systemVolume.FreeBytes)} free of {ByteFormatter.Format(systemVolume.TotalBytes)}  ({systemVolume.UsedPercent:N0}% full)";

        RaisePropertyChanged(nameof(DiskFreeValueText));
        RaisePropertyChanged(nameof(CpuValueText));
        RaisePropertyChanged(nameof(CommitValueText));
        RaisePropertyChanged(nameof(MemoryValueText));
        RaisePropertyChanged(nameof(DiskValueText));
        RaisePropertyChanged(nameof(HardFaultValueText));
        RaisePropertyChanged(nameof(ResourceCountsText));
        RaisePropertyChanged(nameof(ResourceVerdictText));
        RaisePropertyChanged(nameof(CommitPercentValue));
        RaisePropertyChanged(nameof(MemoryPercentValue));
        RaisePropertyChanged(nameof(MachineSpecText));
        RaisePropertyChanged(nameof(ContextPaneSubtitle));
    }

    /// <summary>
    /// One-line answer to "what is the machine short of right now". Memory is checked before CPU because a
    /// paging machine reports low CPU while being far slower than a saturated one.
    /// </summary>
    private string BuildVerdict(ResourceSample sample)
    {
        var hardFaults = _resourceHistory.Average(item => item.HardFaultsPerSecond, 30);
        var cpu = _resourceHistory.Average(item => item.CpuPercent, 30);

        if (sample.CommitPercent >= 88 || hardFaults >= 400)
        {
            return $"Bottleneck: MEMORY. Commit is at {sample.CommitPercent:N0}% of the limit with {hardFaults:N0} hard faults/s - the machine is paging, not computing.";
        }

        if (sample.CommitToPhysicalRatio > 1.25)
        {
            return $"Bottleneck: MEMORY. Committed memory is {sample.CommitToPhysicalRatio:N1}x physical RAM, so the page file is absorbing the overflow.";
        }

        if (cpu >= 85)
        {
            return $"Bottleneck: CPU. Sustained {cpu:N0}% across {Environment.ProcessorCount} logical processors.";
        }

        if (sample.DiskBytesPerSecond > 100L * 1024 * 1024)
        {
            return $"Bottleneck: DISK. {ByteFormatter.Format((long)sample.DiskBytesPerSecond)}/s sustained I/O.";
        }

        return $"Headroom available. CPU {cpu:N0}%, commit {sample.CommitPercent:N0}% of limit, {ByteFormatter.Format(sample.PhysicalAvailableBytes)} RAM free.";
    }

    private void SynchronizeWorkloads(ResourceSample sample)
    {
        // Replace in place so the list does not visibly flicker once a second.
        for (var i = 0; i < sample.Workloads.Count; i++)
        {
            if (i < _resourceWorkloads.Count)
            {
                _resourceWorkloads[i] = sample.Workloads[i];
                continue;
            }

            _resourceWorkloads.Add(sample.Workloads[i]);
        }

        for (var i = _resourceWorkloads.Count - 1; i >= sample.Workloads.Count; i--)
        {
            _resourceWorkloads.RemoveAt(i);
        }
    }

    private void SynchronizeTopProcesses(ResourceSample sample)
    {
        for (var i = 0; i < sample.TopProcesses.Count; i++)
        {
            if (i < _resourceTopProcesses.Count)
            {
                _resourceTopProcesses[i] = sample.TopProcesses[i];
                continue;
            }

            _resourceTopProcesses.Add(sample.TopProcesses[i]);
        }

        for (var i = _resourceTopProcesses.Count - 1; i >= sample.TopProcesses.Count; i--)
        {
            _resourceTopProcesses.RemoveAt(i);
        }
    }

    private void SynchronizeVolumes(ResourceSample sample)
    {
        for (var i = 0; i < sample.Volumes.Count; i++)
        {
            if (i < _resourceVolumes.Count)
            {
                _resourceVolumes[i] = sample.Volumes[i];
                continue;
            }

            _resourceVolumes.Add(sample.Volumes[i]);
        }

        for (var i = _resourceVolumes.Count - 1; i >= sample.Volumes.Count; i--)
        {
            _resourceVolumes.RemoveAt(i);
        }
    }

    /// <summary>
    /// Matches incoming sessions to existing rows by key so an expanded session stays expanded, and only
    /// creates a row when a genuinely new session appears.
    /// </summary>
    private void SynchronizeSessions(ResourceSample sample)
    {
        var existingByKey = _agentSessions.ToDictionary(entry => entry.Key, StringComparer.Ordinal);

        for (var i = 0; i < sample.Sessions.Count; i++)
        {
            var incoming = sample.Sessions[i];

            if (existingByKey.TryGetValue(incoming.Key, out var entry))
            {
                entry.UpdateFrom(incoming);

                var currentIndex = _agentSessions.IndexOf(entry);
                if (currentIndex != i)
                {
                    _agentSessions.Move(currentIndex, i);
                }

                continue;
            }

            var created = new AgentSessionEntry(incoming);
            if (i < _agentSessions.Count)
            {
                _agentSessions.Insert(i, created);
            }
            else
            {
                _agentSessions.Add(created);
            }
        }

        for (var i = _agentSessions.Count - 1; i >= sample.Sessions.Count; i--)
        {
            _agentSessions.RemoveAt(i);
        }

        UpdateSessionSummary(sample);
        RaisePropertyChanged(nameof(TopAgentSessions));
    }

    private void UpdateSessionSummary(ResourceSample sample)
    {
        if (sample.Sessions.Count == 0)
        {
            _agentSessionSummaryText = "No developer sessions detected.";
            RaisePropertyChanged(nameof(AgentSessionSummaryText));
            return;
        }

        var totalCommit = sample.Sessions.Sum(session => session.CommitBytes);
        var agentSessions = sample.Sessions.Count(session => session.Category == WorkloadCategory.CodingAgents);
        var stale = sample.Sessions.Count(session => session.IsLongLived);

        _agentSessionSummaryText =
            $"{sample.Sessions.Count} session(s) · {agentSessions} agent(s) · {ByteFormatter.Format(totalCommit)} committed" +
            (stale > 0 ? $" · {stale} running over 12h" : string.Empty);

        RaisePropertyChanged(nameof(AgentSessionSummaryText));
    }

    private void UpdateAdvice()
    {
        var advice = _resourceAdvisor.Analyze(_resourceHistory);

        for (var i = 0; i < advice.Count; i++)
        {
            if (i < _resourceAdvice.Count)
            {
                if (_resourceAdvice[i] != advice[i])
                {
                    _resourceAdvice[i] = advice[i];
                }

                continue;
            }

            _resourceAdvice.Add(advice[i]);
        }

        for (var i = _resourceAdvice.Count - 1; i >= advice.Count; i--)
        {
            _resourceAdvice.RemoveAt(i);
        }
    }

    private void SetResourceStatus(string status)
    {
        if (SetProperty(ref _resourceStatusText, status, nameof(ResourceStatusText)))
        {
            RaisePropertyChanged(nameof(ResourceWindowText));
        }
    }

    private static string FormatWindow(int seconds)
    {
        return seconds >= 60
            ? $"{seconds / 60}m {seconds % 60}s"
            : $"{seconds}s";
    }
}
