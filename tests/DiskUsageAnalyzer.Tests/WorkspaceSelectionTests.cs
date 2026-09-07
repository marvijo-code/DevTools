using DevTools.Services;
using DevTools.ViewModels;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class WorkspaceSelectionTests
{
    private sealed record Row(long Id, string Title);

    [Theory]
    [InlineData(MainContentTab.KillProcesses, "Selected Process")]
    [InlineData(MainContentTab.WorkflowExplorer, "Selected Run")]
    [InlineData(MainContentTab.DiskUsage, "Storage Overview")]
    [InlineData(MainContentTab.Resources, "System Overview")]
    public void EachWorkspaceHasOnlyItsOwnContext(MainContentTab tab, string title)
    {
        var context = WorkspaceContext.For(tab);
        Assert.Equal(title, context.Title);
        Assert.Single(new[] { context.ShowRuns, context.ShowProcesses, context.ShowStorage, context.ShowSystem }, value => value);
        Assert.Equal(tab == MainContentTab.WorkflowExplorer, context.ShowRuns);
    }

    [Fact]
    public void RefreshKeepsSelectedIdentityWhenRowsMoveOrAreReplaced()
    {
        var refreshed = new[] { new Row(3, "New background run"), new Row(2, "Selected run refreshed"), new Row(1, "Other run") };
        Assert.Same(refreshed[1], SelectionRetention.Find(refreshed, 2, row => row.Id));
    }

    [Fact]
    public void FilterDoesNotReplaceHiddenSelectionWithUnrelatedFirstRow()
    {
        var visible = new[] { new Row(3, "Other task") };
        Assert.Null(SelectionRetention.Find(visible, 2, row => row.Id));
        Assert.Null(SelectionRetention.Find(visible, null, row => row.Id));
    }

    [Fact]
    public void BackgroundInsertDoesNotResetTheListOrRemoveSelectedRow()
    {
        var selected = new Row(2, "Selected");
        var other = new Row(1, "Other");
        var rows = new System.Collections.ObjectModel.ObservableCollection<Row> { other, selected };
        var events = new List<System.Collections.Specialized.NotifyCollectionChangedEventArgs>();
        rows.CollectionChanged += (_, args) => events.Add(args);
        SelectionRetention.Synchronize(rows, new[] { new Row(3, "New"), selected });
        Assert.Same(selected, rows[1]);
        Assert.DoesNotContain(events, item => item.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset);
        Assert.DoesNotContain(events, item => item.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove && item.OldItems!.Contains(selected));
    }
}
