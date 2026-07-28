using DevTools.Models;

namespace DevTools.Services.Resources;

/// <summary>
/// Groups developer processes into sessions - one per process tree - so usage can be read as "this agent,
/// working in this repo, is holding this much" rather than as a flat list of interchangeable node.exe rows.
///
/// A session root is a developer-workload process whose parent is not itself a developer workload. Every
/// descendant is rolled into that root regardless of category, because a shell an agent spawned to run a
/// build is part of that agent's cost.
/// </summary>
internal sealed class AgentSessionBuilder
{
    private readonly ProcessDetailsReader _detailsReader = new();
    private readonly ClaudeSessionCatalog _claudeSessions = new();

    public IReadOnlyList<AgentSessionUsage> Build(
        IReadOnlyList<RawProcessCounters> rawProcesses,
        IReadOnlyDictionary<int, ProcessUsage> usageByPid)
    {
        if (rawProcesses.Count == 0)
        {
            return [];
        }

        var byPid = new Dictionary<int, RawProcessCounters>(rawProcesses.Count);
        foreach (var process in rawProcesses)
        {
            byPid[process.ProcessId] = process;
        }

        var livePids = byPid.Keys.ToHashSet();
        _detailsReader.RetainOnly(livePids);
        _claudeSessions.RetainOnly(livePids);

        var childrenByParent = BuildChildIndex(rawProcesses, byPid);
        var sessions = new List<AgentSessionUsage>();

        foreach (var process in rawProcesses)
        {
            if (!IsSessionRoot(process, byPid))
            {
                continue;
            }

            var session = BuildSession(process, childrenByParent, byPid, usageByPid);
            if (session is not null)
            {
                sessions.Add(session);
            }
        }

        var totalCommit = sessions.Sum(session => session.CommitBytes);

        return sessions
            .Select(session => session with { SharePercent = ToSharePercent(session.CommitBytes, totalCommit) })
            .OrderByDescending(session => session.CommitBytes)
            .ToList();
    }

    private static Dictionary<int, List<int>> BuildChildIndex(
        IReadOnlyList<RawProcessCounters> processes,
        Dictionary<int, RawProcessCounters> byPid)
    {
        var index = new Dictionary<int, List<int>>(processes.Count);

        foreach (var process in processes)
        {
            // A parent PID that has been reused by a newer process would otherwise graft an unrelated
            // subtree onto this session, so only link when the parent predates the child.
            if (!byPid.TryGetValue(process.ParentProcessId, out var parent) ||
                parent.StartTime > process.StartTime)
            {
                continue;
            }

            if (!index.TryGetValue(process.ParentProcessId, out var children))
            {
                children = [];
                index[process.ParentProcessId] = children;
            }

            children.Add(process.ProcessId);
        }

        return index;
    }

    private bool IsSessionRoot(RawProcessCounters process, Dictionary<int, RawProcessCounters> byPid)
    {
        if (process.ProcessId <= 0)
        {
            return false;
        }

        var category = WorkloadClassifier.Classify(process.ImageName);
        if (!WorkloadClassifier.IsDeveloperWorkload(category))
        {
            return false;
        }

        if (!byPid.TryGetValue(process.ParentProcessId, out var parent) ||
            parent.StartTime > process.StartTime)
        {
            return true;
        }

        if (!WorkloadClassifier.IsDeveloperWorkload(WorkloadClassifier.Classify(parent.ImageName)))
        {
            return true;
        }

        // A child working in a different directory than its parent is a separate session. Without this,
        // every agent launched from one long-lived shim collapses into a single enormous tree instead of
        // one row per repo, which is the whole point of the breakdown.
        var ownWorkspace = _detailsReader.Read(process.ProcessId).WorkingDirectory;
        var parentWorkspace = _detailsReader.Read(parent.ProcessId).WorkingDirectory;

        return ownWorkspace.Length > 0 &&
               parentWorkspace.Length > 0 &&
               !string.Equals(ownWorkspace, parentWorkspace, StringComparison.OrdinalIgnoreCase);
    }

    private AgentSessionUsage? BuildSession(
        RawProcessCounters root,
        Dictionary<int, List<int>> childrenByParent,
        Dictionary<int, RawProcessCounters> byPid,
        IReadOnlyDictionary<int, ProcessUsage> usageByPid)
    {
        var members = new List<ProcessUsage>();
        var rootDetails = _detailsReader.Read(root.ProcessId);
        var workspace = rootDetails.WorkingDirectory;

        var pending = new Stack<int>();
        pending.Push(root.ProcessId);
        var visited = new HashSet<int>();

        while (pending.Count > 0)
        {
            var pid = pending.Pop();
            if (!visited.Add(pid))
            {
                continue;
            }

            if (usageByPid.TryGetValue(pid, out var usage))
            {
                members.Add(usage);
            }

            if (workspace.Length == 0 && pid != root.ProcessId)
            {
                // A launcher shim often has no useful working directory; inherit the first child that does.
                var childDetails = _detailsReader.Read(pid);
                if (childDetails.HasWorkingDirectory)
                {
                    workspace = childDetails.WorkingDirectory;
                }
            }

            if (childrenByParent.TryGetValue(pid, out var children))
            {
                foreach (var child in children)
                {
                    // Stop at a nested session root so its subtree is attributed to it rather than
                    // counted twice, once here and once in its own row.
                    if (byPid.TryGetValue(child, out var childProcess) && IsSessionRoot(childProcess, byPid))
                    {
                        continue;
                    }

                    pending.Push(child);
                }
            }
        }

        if (members.Count == 0)
        {
            return null;
        }

        var rootCategory = WorkloadClassifier.Classify(root.ImageName);
        var rootName = WorkloadClassifier.NormalizeImageName(root.ImageName);

        return new AgentSessionUsage(
            root.ProcessId,
            rootName,
            rootCategory,
            workspace,
            DescribeWorkspace(workspace),
            root.StartTime,
            members.Count,
            members.Sum(member => member.CpuPercent),
            members.Sum(member => member.CommitBytes),
            members.Sum(member => member.WorkingSetBytes),
            members.Sum(member => member.DiskBytesPerSecond),
            members.Sum(member => member.ThreadCount),
            members
                .OrderByDescending(member => member.CommitBytes)
                .ToList(),
            0,
            // Any process in the tree may be the one that registered the session, so ask them all.
            _claudeSessions.ResolveTitle(members.Select(member => member.ProcessId)));
    }

    /// <summary>Leaf folder name, which is how a developer actually refers to a checkout.</summary>
    private static string DescribeWorkspace(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return "unknown workspace";
        }

        var trimmed = workspace.TrimEnd('\\', '/');
        var separatorIndex = trimmed.LastIndexOfAny(['\\', '/']);

        return separatorIndex >= 0 && separatorIndex < trimmed.Length - 1
            ? trimmed[(separatorIndex + 1)..]
            : trimmed;
    }

    private static double ToSharePercent(long bytes, long totalBytes)
    {
        return totalBytes <= 0 ? 0 : Math.Clamp(bytes * 100.0 / totalBytes, 0, 100);
    }
}
