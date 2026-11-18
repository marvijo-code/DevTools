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
    public async Task Walker_AttributesTheDirectoryIndexEvenWhenEveryFileIsEmpty()
    {
        var root = Directory.CreateTempSubdirectory("devtools-directory-index-");
        try
        {
            var catalog = root.CreateSubdirectory("catalog");
            var fileCount = 400;
            for (var i = 0; i < fileCount; i++)
            {
                using var file = File.Create(Path.Combine(catalog.FullName, $"fixture-{i:D4}"));
            }

            var tree = await new ParallelFolderWalker().ScanAsync(root.FullName, null, CancellationToken.None, workerCount: 2);
            var node = tree.Find(catalog.FullName);
            Assert.Equal(fileCount, tree.GetFileCount(node));
            Assert.Equal(0, tree.GetTotalBytes(node));
            Assert.True(tree.GetAllocatedBytes(node) > 0, "The NTFS directory index consumes space.");
            Assert.True(tree.GetAllocatedBytes(tree.Find(root.FullName)) >= tree.GetAllocatedBytes(node));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Walker_DoesNotCountResidentDataAsSeparateClusters()
    {
        var root = Directory.CreateTempSubdirectory("devtools-resident-");
        try
        {
            var path = Path.Combine(root.FullName, "tiny.bin");
            var bytes = new byte[] { 42 };
            await File.WriteAllBytesAsync(path, bytes);
            WriteNamedStream(path + ":resident", bytes);
            WriteNamedStream(root.FullName + ":resident", bytes);

            var tree = await new ParallelFolderWalker().ScanAsync(root.FullName, null, CancellationToken.None, workerCount: 1);
            var node = tree.Find(root.FullName);
            Assert.Equal(1L, tree.GetTotalBytes(node));
            Assert.Equal(0L, tree.GetAllocatedBytes(node));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Walker_AccountsForNamedFileAndDirectoryStreamsWithoutCountingHardLinksTwice()
    {
        var root = Directory.CreateTempSubdirectory("devtools-streams-");
        try
        {
            var source = root.CreateSubdirectory("source");
            var duplicate = root.CreateSubdirectory("duplicate");
            var path = Path.Combine(source.FullName, "named-only.bin");
            using (File.Create(path))
            {
            }

            var bytes = new byte[64 * 1024];
            new Random(89).NextBytes(bytes);
            foreach (var suffix in new[] { ":payload", ":" + new string('a', 240), ":" + new string('b', 240) })
            {
                WriteNamedStream(path + suffix, bytes);
            }

            WriteNamedStream(source.FullName + ":folder-payload", bytes);
            Assert.True(CreateHardLink(Path.Combine(duplicate.FullName, "linked.bin"), path, IntPtr.Zero));
            var withDefaultStream = Path.Combine(source.FullName, "with-default.bin");
            await File.WriteAllBytesAsync(withDefaultStream, bytes);
            WriteNamedStream(withDefaultStream + ":payload", bytes);

            Assert.True(GetDiskFreeSpace(root.Root.FullName, out var sectorsPerCluster, out var bytesPerSector, out _, out _));
            var clusterBytes = (long)sectorsPerCluster * bytesPerSector;
            var allocationPerStream = (1 + (bytes.Length - 1) / clusterBytes) * clusterBytes;
            var tree = await new ParallelFolderWalker().ScanAsync(root.FullName, null, CancellationToken.None, workerCount: 2);
            var node = tree.Find(root.FullName);
            Assert.Equal(bytes.Length, tree.GetTotalBytes(node));
            Assert.Equal(3, tree.GetFileCount(node));
            Assert.Equal(allocationPerStream * 6, tree.GetAllocatedBytes(node));
        }
        finally
        {
            root.Delete(recursive: true);
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

    [Fact]
    public void AllocationIndex_CountsFullIdentitiesOnceAcrossWorkersAndSlotReuse()
    {
        var index = new DirectoryAllocationIndex(16_000, recordCapacity: 128);
        var ids = new UInt128[]
        {
            (42UL << 48) | 9,
            (43UL << 48) | 9,
            ((UInt128)1 << 64) | ((42UL << 48) | 9),
            (42UL << 48) | 200
        };
        var results = new bool[4096];
        Parallel.For(0, results.Length, i => results[i] = index.TryClaim(ids[i % ids.Length]));

        Assert.Equal(ids.Length, results.Count(result => result));
        Assert.All(ids, id => Assert.False(index.TryClaim(id)));
        Assert.False(index.TryClaim(0));
    }

    private static void WriteNamedStream(string path, byte[] bytes)
    {
        using var stream = CreateFile(path, 0x40000000, 7, IntPtr.Zero, 2, 0, IntPtr.Zero);
        Assert.False(stream.IsInvalid, $"Could not create the fixture stream. Windows error {Marshal.GetLastWin32Error()}.");
        RandomAccess.Write(stream, bytes, 0);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector,
        out uint freeClusters, out uint totalClusters);

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
