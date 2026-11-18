using System.Runtime.InteropServices;
using DevTools.Services.FolderSizes;
using Xunit;

namespace DiskUsageAnalyzer.Tests.FolderSizes;

public sealed class DirectoryAllocationTests
{
    [Fact]
    public async Task Walker_ReportsLogicalLengthsAndCountsHardLinkAllocationOnce()
    {
        var root = Directory.CreateTempSubdirectory("devtools-allocation-");
        try
        {
            var first = root.CreateSubdirectory("first");
            var second = root.CreateSubdirectory("second");
            var path = Path.Combine(first.FullName, "content.bin");
            var bytes = new byte[8193];
            new Random(19).NextBytes(bytes);
            await File.WriteAllBytesAsync(path, bytes);
            var walker = new ParallelFolderWalker();

            var tree = await walker.ScanAsync(root.FullName, null, CancellationToken.None, workerCount: 2);
            var singleAllocation = tree.GetAllocatedBytes(tree.Find(root.FullName));
            Assert.True(singleAllocation > 0, "Expected storage allocation from the local NTFS filesystem.");

            Assert.True(CreateHardLink(Path.Combine(second.FullName, "linked.bin"), path, IntPtr.Zero));
            tree = await walker.ScanAsync(root.FullName, null, CancellationToken.None, workerCount: 2);

            var node = tree.Find(root.FullName);
            Assert.Equal(bytes.Length * 2L, tree.GetTotalBytes(node));
            Assert.Equal(singleAllocation, tree.GetAllocatedBytes(node));
            Assert.Equal(singleAllocation,
                tree.GetAllocatedBytes(tree.Find(first.FullName)) + tree.GetAllocatedBytes(tree.Find(second.FullName)));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string existingPath, IntPtr security);
}
