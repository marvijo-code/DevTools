using System.Runtime.InteropServices;
using DevTools.Models;
using DevTools.Services.FolderSizes;
using Microsoft.Win32.SafeHandles;
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

    [Fact]
    public async Task Walker_MatchesWindowsAllocationForSparseFileWithTwoWrittenRanges()
    {
        var root = Directory.CreateTempSubdirectory("devtools-sparse-");
        var path = Path.Combine(root.FullName, "sparse.bin");
        const long secondRange = 8 * 1024 * 1024;
        var block = new byte[64 * 1024];
        new Random(31).NextBytes(block);

        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                const uint fsctlSetSparse = 0x000900C4;
                Assert.True(DeviceIoControl(stream.SafeFileHandle, fsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero));
                stream.Write(block);
                stream.Position = secondRange;
                stream.Write(block);
            }

            var expectedLogical = secondRange + block.Length;
            var expectedAllocated = (long)GetCompressedFileSize(path, out var high);
            Assert.Equal(0u, high);
            Assert.InRange(expectedAllocated, 1, expectedLogical - 1);

            var tree = await new ParallelFolderWalker().ScanAsync(root.FullName, null, CancellationToken.None, workerCount: 1);
            var node = tree.Find(root.FullName);

            Assert.Equal(expectedLogical, tree.GetTotalBytes(node));
            Assert.Equal(expectedAllocated, tree.GetAllocatedBytes(node));
        }
        finally
        {
            File.Delete(path);
            root.Delete();
        }
    }

    [Fact]
    public async Task Walker_CanReadAnNtfsFolderByVolumeGuidAndCleanupRemainsDisabled()
    {
        var fixture = Directory.CreateTempSubdirectory("devtools-volume-guid-");
        var fixtureDrive = Path.GetPathRoot(fixture.FullName)!;
        var volumeRoot = Assert.IsType<string>(WindowsVolumePaths.GetVolumeName(fixtureDrive));
        var folderPath = Path.Combine(volumeRoot, Path.GetRelativePath(fixtureDrive, fixture.FullName));
        var filePath = Path.Combine(fixture.FullName, "content.bin");

        try
        {
            var bytes = new byte[8193];
            new Random(13).NextBytes(bytes);
            await File.WriteAllBytesAsync(filePath, bytes);
            var tree = await new ParallelFolderWalker().ScanAsync(folderPath, null, CancellationToken.None, workerCount: 1);
            var node = tree.Find(folderPath);

            Assert.Equal(volumeRoot, tree.RootPath);
            Assert.Equal(folderPath, tree.GetPath(node));
            Assert.Equal(bytes.Length, tree.GetTotalBytes(node));
            Assert.True(tree.GetAllocatedBytes(node) > 0);
            Assert.Equal(-1, tree.Find(fixture.FullName));
            Assert.Equal(volumeRoot[..^1], WindowsVolumePaths.GetDevicePath(volumeRoot));
            Assert.False(DiskUsageEntry.IsClearableDirectory(folderPath));
        }
        finally
        {
            File.Delete(filePath);
            fixture.Delete();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSize(string path, out uint high);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle file, uint code, IntPtr input, uint inputSize, IntPtr output,
        uint outputSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string path, string existingPath, IntPtr security);
}
