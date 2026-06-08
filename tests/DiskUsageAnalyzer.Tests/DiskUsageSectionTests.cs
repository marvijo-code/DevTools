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
