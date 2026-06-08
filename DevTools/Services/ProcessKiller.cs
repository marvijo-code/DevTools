using System.Diagnostics;
using System.Management;

namespace DevTools.Services;

public sealed class ProcessKiller
{
    public bool TryKillProcessTree(int processId, out string? errorMessage)
    {
        errorMessage = null;

        if (processId <= 0)
        {
            errorMessage = "Invalid process id.";
            return false;
        }

        var processIds = CollectProcessTree(processId);
        if (processIds.Count == 0)
        {
            return true;
        }

        var errors = new List<string>();
        foreach (var targetProcessId in processIds.OrderByDescending(id => id))
        {
            if (TryKillProcess(targetProcessId, out var singleError))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(singleError))
            {
                errors.Add($"PID {targetProcessId}: {singleError}");
            }
        }

        if (errors.Count > 0)
        {
            errorMessage = string.Join("; ", errors);
            return false;
        }

        return true;
    }

    public bool TryKillProcess(int processId, out string? errorMessage)
    {
        errorMessage = null;

        if (processId <= 0)
        {
            errorMessage = "Invalid process id.";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(true);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static HashSet<int> CollectProcessTree(int rootProcessId)
    {
        var processIds = new HashSet<int> { rootProcessId };
        var parentLookup = new Dictionary<int, List<int>>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId FROM Win32_Process");
            foreach (ManagementObject processObject in searcher.Get())
            {
                using (processObject)
                {
                    var processId = Convert.ToInt32(processObject["ProcessId"]);
                    var parentProcessId = Convert.ToInt32(processObject["ParentProcessId"]);
                    if (!parentLookup.TryGetValue(parentProcessId, out var children))
                    {
                        children = [];
                        parentLookup[parentProcessId] = children;
                    }

                    children.Add(processId);
                }
            }
        }
        catch (ManagementException)
        {
            return processIds;
        }

        var pending = new Queue<int>();
        pending.Enqueue(rootProcessId);
        while (pending.Count > 0)
        {
            var currentProcessId = pending.Dequeue();
            if (!parentLookup.TryGetValue(currentProcessId, out var children))
            {
                continue;
            }

            foreach (var childProcessId in children)
            {
                if (processIds.Add(childProcessId))
                {
                    pending.Enqueue(childProcessId);
                }
            }
        }

        return processIds;
    }

    public int TryKillProcesses(IEnumerable<int> processIds, out string? errorMessage)
    {
        errorMessage = null;
        var killedCount = 0;
        var errors = new List<string>();

        foreach (var processId in processIds.Distinct().Where(id => id > 0))
        {
            if (TryKillProcess(processId, out var singleError))
            {
                killedCount++;
                continue;
            }

            if (!string.IsNullOrWhiteSpace(singleError))
            {
                errors.Add($"PID {processId}: {singleError}");
            }
        }

        if (errors.Count > 0)
        {
            errorMessage = string.Join("; ", errors);
        }

        return killedCount;
    }
}
