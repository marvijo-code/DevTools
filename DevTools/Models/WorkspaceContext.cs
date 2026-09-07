namespace DevTools.ViewModels;

public enum MainContentTab { KillProcesses, WorkflowExplorer, DiskUsage, Resources }

public sealed record WorkspaceContext(string Title, bool ShowRuns, bool ShowProcesses, bool ShowStorage, bool ShowSystem)
{
    public static WorkspaceContext For(MainContentTab tab) => tab switch
    {
        MainContentTab.WorkflowExplorer => new("Selected Run", true, false, false, false),
        MainContentTab.KillProcesses => new("Selected Process", false, true, false, false),
        MainContentTab.DiskUsage => new("Storage Overview", false, false, true, false),
        _ => new("System Overview", false, false, false, true)
    };
}
