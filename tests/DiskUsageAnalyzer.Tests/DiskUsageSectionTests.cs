using DevTools.Models;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class DiskUsageSectionTests
{
    [Fact]
    public void UpdateFrom_DeduplicatesIncomingEntriesAndKeepsLatestOrder()
    {
        var section = new DiskUsageSection(
            "Docker",
            "Before",
            [
                DiskUsageEntry.CreateCompleted("C:\\Docker", true, 10, "10 B"),
                DiskUsageEntry.CreateCompleted("C:\\Docker\\wsl", true, 20, "20 B")
            ]);
        var incoming = new DiskUsageSection(
            "Docker",
            "After",
            [
                DiskUsageEntry.CreateCompleted("C:\\Docker\\wsl", true, 25, "25 B"),
                DiskUsageEntry.CreateCompleted("C:\\Docker\\wsl", true, 30, "30 B"),
                DiskUsageEntry.CreateCompleted("C:\\Docker", true, 15, "15 B")
            ]);

        var exception = Record.Exception(() => section.UpdateFrom(incoming));

        Assert.Null(exception);
        Assert.Equal("After", section.Summary);
        Assert.Collection(
            section.Entries,
            entry =>
            {
                Assert.Equal("C:\\Docker\\wsl", entry.Path);
                Assert.Equal(25, entry.SizeBytes);
            },
            entry =>
            {
                Assert.Equal("C:\\Docker", entry.Path);
                Assert.Equal(15, entry.SizeBytes);
            });
    }

    [Fact]
    public void UpdateFrom_ReordersAndAddsEntriesWithoutThrowing()
    {
        var section = new DiskUsageSection(
            "User Profile",
            "Before",
            [
                DiskUsageEntry.CreateCompleted("R:\\Profile\\Downloads", true, 10, "10 B"),
                DiskUsageEntry.CreateCompleted("R:\\Profile\\Documents", true, 20, "20 B"),
                DiskUsageEntry.CreateCompleted("R:\\Profile\\Pictures", true, 30, "30 B")
            ]);
        var incoming = new DiskUsageSection(
            "User Profile",
            "After",
            [
                DiskUsageEntry.CreateCompleted("R:\\Profile\\Pictures", true, 35, "35 B"),
                DiskUsageEntry.CreateCompleted("R:\\Profile\\Desktop", true, 40, "40 B"),
                DiskUsageEntry.CreateCompleted("R:\\Profile\\Documents", true, 25, "25 B")
            ]);

        var exception = Record.Exception(() => section.UpdateFrom(incoming));

        Assert.Null(exception);
        Assert.Equal("After", section.Summary);
        Assert.Collection(
            section.Entries,
            entry =>
            {
                Assert.Equal("R:\\Profile\\Pictures", entry.Path);
                Assert.Equal(35, entry.SizeBytes);
            },
            entry =>
            {
                Assert.Equal("R:\\Profile\\Desktop", entry.Path);
                Assert.Equal(40, entry.SizeBytes);
            },
            entry =>
            {
                Assert.Equal("R:\\Profile\\Documents", entry.Path);
                Assert.Equal(25, entry.SizeBytes);
            });
    }

    [Fact]
    public void UpdateFromScan_ReusesUnchangedSectionsAndAppliesNewResults()
    {
        const string path = @"C:\Data";
        var displayEntry = DiskUsageEntry.CreateCompleted(path, true, 100, "100 B");
        var sourceEntry = DiskUsageEntry.CreateCompleted(path, true, 100, "100 B");
        sourceEntry.SetExpandable(true);
        var display = new DiskUsageSection("Largest Folders", "Original", [displayEntry]);
        var source = new DiskUsageSection(display.Title, "Scanned", [sourceEntry]);
        display.UpdateFromScan(source);

        var expandedChild = DiskUsageEntry.CreateCompleted(path + @"\Project", true, 40, "40 B");
        displayEntry.SetChildren([expandedChild]);
        displayEntry.IsExpanded = true;
        var notifications = new List<string?>();
        displayEntry.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        display.UpdateFromScan(source);

        Assert.Empty(notifications);
        Assert.True(displayEntry.IsExpanded);
        Assert.True(displayEntry.HasLoadedChildren);
        Assert.Same(expandedChild, Assert.Single(displayEntry.Children));

        var replacement = DiskUsageEntry.CreateCompleted(path, true, 250, "250 B");
        display.UpdateFromScan(new DiskUsageSection(display.Title, "Rescanned", [replacement]));

        Assert.Equal("Rescanned", display.Summary);
        Assert.Same(displayEntry, Assert.Single(display.Entries));
        Assert.Equal(250, displayEntry.SizeBytes);
        Assert.Contains(nameof(DiskUsageEntry.SizeBytes), notifications);
    }

    [Fact]
    public void SetChildren_OrdersFilesAndFoldersBySizeDescending()
    {
        var entry = DiskUsageEntry.CreateCompleted("R:\\Repos", true, 100, "100 B");

        entry.SetChildren(
            [
                DiskUsageEntry.CreateCompleted("R:\\Repos\\small.txt", true, 10, "10 B"),
                DiskUsageEntry.CreateCompleted("R:\\Repos\\large", true, 40, "40 B"),
                DiskUsageEntry.CreateCompleted("R:\\Repos\\medium.zip", true, 25, "25 B")
            ]);

        Assert.Collection(
            entry.Children,
            child => Assert.Equal("R:\\Repos\\large", child.Path),
            child => Assert.Equal("R:\\Repos\\medium.zip", child.Path),
            child => Assert.Equal("R:\\Repos\\small.txt", child.Path));
    }
}
