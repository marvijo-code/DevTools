namespace DevTools.ViewModels;

public enum MainContentTab { OpenWork, KillProcesses, WorkflowExplorer, DiskUsage, Worktrees, Projects, Resources }

public sealed record WorkspaceContext(
    MainContentTab Tab,
    string Title,
    bool ShowRuns,
    bool ShowProcesses,
    bool ShowStorage,
    bool ShowSystem)
{
    public bool ShowOpenWork => Tab == MainContentTab.OpenWork;

    public bool ShowWorktrees => Tab == MainContentTab.Worktrees;

    public bool ShowProjects => Tab == MainContentTab.Projects;

    public static WorkspaceContext For(MainContentTab tab) => tab switch
    {
        MainContentTab.WorkflowExplorer => new(tab, "Selected Run", true, false, false, false),
        MainContentTab.KillProcesses => new(tab, "Selected Process", false, true, false, false),
        MainContentTab.DiskUsage => new(tab, "Storage Overview", false, false, true, false),
        MainContentTab.Worktrees => new(tab, "Worktree Details", false, false, false, false),
        MainContentTab.Projects => new(tab, "Project Cleaner", false, false, false, false),
        MainContentTab.OpenWork => new(tab, "Open Work", false, false, false, false),
        _ => new(tab, "System Overview", false, false, false, true)
    };
}