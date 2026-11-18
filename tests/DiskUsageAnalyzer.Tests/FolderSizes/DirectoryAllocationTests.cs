using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using DevTools.Models;
using DevTools.Services.FolderSizes;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace DiskUsageAnalyzer.Tests.FolderSizes;

public sealed class DirectoryAllocationTests
{
    [Theory]
    [InlineData("FAT", true)]
    [InlineData("FAT32", true)]
    [InlineData("exFat", true)]
    [InlineData("NTFS", false)]
    [InlineData("ReFS", false)]
    [InlineData("UDF", false)]
    [InlineData("", false)]
    public void SupportsAllocationWithoutFileIds_OnlySelectsTheFatFamily(string fileSystem, bool expected)
    {
        Assert.Equal(expected, DirectoryAllocationReader.SupportsAllocationWithoutFileIds(fileSystem));
    }

    [Fact]
    public async Task Reader_MeasuresAllocationFromDirectoryRepliesWithoutFileIds()
    {
        var root = Directory.CreateTempSubdirectory("devtools-full-dir-info-");
        try
        {
            root.CreateSubdirectory("résumé");
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, "payload.bin"), new byte[8193]);
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, "empty.bin"), []);
            var clusterBytes = WindowsVolumePaths.GetAllocationUnitBytes(root.Root.FullName);
            Assert.True(clusterBytes > 0);

            var reader = new DirectoryAllocationReader(identities: null, allocationUnitBytes: 0);
            var folders = new List<string>();
            Assert.True(reader.TryRead(root.FullName, 0, folders, null, CancellationToken.None,
                out var logical, out var allocated, out var files, out var incomplete));

            Assert.Equal(["résumé"], folders);
            Assert.Equal(2, files);
            Assert.Equal(8193, logical);
            Assert.Equal((1 + (logical - 1) / clusterBytes) * clusterBytes, allocated);
            Assert.False(incomplete);

            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
            var fallback = ParallelFolderWalker.ReadManagedDirectory(root.FullName, folders, options, reader,
                CancellationToken.None);
            Assert.Equal((logical, files, allocated, false, false), fallback);
            Assert.Equal(["résumé"], folders);

            var file = Path.Combine(root.FullName, "payload.bin");
            Assert.False(reader.TryReadPathAllocation(file, logical + 1, CancellationToken.None, out var staleBytes));
            Assert.Equal(0, staleBytes);

            var missing = ParallelFolderWalker.ReadManagedDirectory(Path.Combine(root.FullName, "missing"),
                folders, options, reader, CancellationToken.None);
            Assert.Equal((0L, 0L, 0L, true, true), missing);
            Assert.Empty(folders);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

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
    public async Task Walker_BatchJobsAccountForEveryFolderWithDifferentWorkerCounts()
    {
        const int folderCount = 96;
        const int fileBytes = 8193;
        var root = Directory.CreateTempSubdirectory("devtools-batched-walk-");
        try
        {
            var files = new string[folderCount];
            var contents = new byte[fileBytes];
            for (var index = 0; index < folderCount; index++)
            {
                var folder = root.CreateSubdirectory($"folder {index:D2}");
                files[index] = Path.Combine(folder.FullName, "payload.bin");
                await File.WriteAllBytesAsync(files[index], contents);
            }

            Assert.True(CreateHardLink(Path.Combine(Path.GetDirectoryName(files[^1])!, "linked.bin"), files[0], IntPtr.Zero));
            WriteNamedStream(files[0] + ":extra", contents);
            Assert.True(ParallelFolderWalker.GetDirectoryBatchSize(folderCount, 4) > 1);

            var walker = new ParallelFolderWalker();
            var single = await walker.ScanAsync(root.FullName, null, CancellationToken.None, workerCount: 1,
                prioritize: true, measureAllocation: true);
            var parallel = await walker.ScanAsync(root.FullName, null, CancellationToken.None, workerCount: 4,
                prioritize: true, measureAllocation: true);
            var rootNode = parallel.Find(root.FullName);
            Assert.True(rootNode >= 0);
            Assert.Equal(folderCount, parallel.GetChildFolderCount(rootNode));
            Assert.Equal(folderCount + 1L, parallel.TotalFiles);
            Assert.Equal((folderCount + 1L) * fileBytes, parallel.TotalBytes);
            Assert.True(parallel.GetAllocatedBytes(rootNode) > 0);
            Assert.Equal(single.GetAllocatedBytes(single.Find(root.FullName)), parallel.GetAllocatedBytes(rootNode));
            Assert.False(parallel.HasIncompleteScan(rootNode));
            Assert.False(parallel.HasIncompleteAllocation(rootNode));
            for (var index = 0; index < folderCount; index++)
            {
                var node = parallel.Find(Path.GetDirectoryName(files[index])!);
                Assert.True(node >= 0);
                Assert.Equal(index == folderCount - 1 ? 2 * fileBytes : fileBytes, parallel.GetTotalBytes(node));
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PriorityAllocation_UpgradesLogicalFolderWhileBackgroundScansArePaused()
    {
        var root = Directory.CreateTempSubdirectory("devtools-priority-allocation-");
        var child = root.CreateSubdirectory("child");
        var file = Path.Combine(child.FullName, "data.bin");
        var contents = new byte[65537];
        new Random(27).NextBytes(contents);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            await File.WriteAllBytesAsync(file, contents, timeout.Token);
            WriteNamedStream(file + ":payload", contents);
            Assert.True(GetDiskFreeSpace(root.Root.FullName, out var sectorsPerCluster, out var bytesPerSector, out _, out _));
            var clusterBytes = (long)sectorsPerCluster * bytesPerSector;
            Assert.True(clusterBytes > 0);
            var fileAllocation = 2 * ((contents.Length + clusterBytes - 1) / clusterBytes) * clusterBytes;
            var analyzer = new DevTools.Services.DiskUsageAnalyzer();

            using var pause = ScanGate.Pause();
            Assert.True(await analyzer.SizeFolderNowAsync(root.FullName, timeout.Token));
            Assert.True(analyzer.TryFindNode(child.FullName, out var logical, out var logicalNode));
            Assert.Null(logical!.GetAllocatedBytes(logicalNode));

            Assert.True(await analyzer.SizeFolderNowAsync(child.FullName, timeout.Token, measureAllocation: true));
            Assert.True(analyzer.TryFindNode(child.FullName, preferAllocation: true, out var allocated, out var allocatedNode));
            Assert.NotSame(logical, allocated);
            Assert.Equal(contents.Length, allocated!.GetTotalBytes(allocatedNode));
            Assert.Equal(fileAllocation, allocated.GetAllocatedBytes(allocatedNode));
            Assert.False(allocated.HasIncompleteAllocation(allocatedNode));

            Assert.True(await analyzer.SizeFolderNowAsync(child.FullName, timeout.Token, measureAllocation: true));
            Assert.True(analyzer.TryFindNode(child.FullName, preferAllocation: true, out var cached, out var cachedNode));
            Assert.Same(allocated, cached);
            Assert.Equal(allocatedNode, cachedNode);
        }
        finally
        {
            File.Delete(file);
            child.Delete();
            root.Delete();
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
                stream.Flush(flushToDisk: true);
            }

            var expectedLogical = secondRange + block.Length;
            var expectedAllocated = (long)GetCompressedFileSize(path, out var high);
            Assert.Equal(0u, high);
            Assert.InRange(expectedAllocated, 1, expectedLogical - 1);

            var tree = await new ParallelFolderWalker().ScanAsync(root.FullName, null, CancellationToken.None, workerCount: 1);
            var node = tree.Find(root.FullName);

            Assert.Equal(expectedLogical, tree.GetTotalBytes(node));
            Assert.Equal(expectedAllocated, tree.GetAllocatedBytes(node));

            var clusterBytes = WindowsVolumePaths.GetAllocationUnitBytes(root.Root.FullName);
            Assert.True(clusterBytes > 0);
            var constrained = new DirectoryAllocationReader(new DirectoryAllocationIndex(0), clusterBytes);
            Assert.True(constrained.TryRead(root.FullName, 0, [], null, CancellationToken.None,
                out var logical, out var allocated, out _, out var incomplete));
            Assert.Equal(expectedLogical, logical);
            Assert.Equal(expectedAllocated, allocated);
            Assert.False(incomplete);
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

    [Theory]
    [InlineData(96)]
    [InlineData(512)]
    public async Task Walker_ParallelStreamBatchesPreserveWideDirectoryTotals(int fileCount)
    {
        var root = Directory.CreateTempSubdirectory("devtools-stream-batches-");
        try
        {
            var wide = root.CreateSubdirectory("wide");
            const int streamStride = 31;
            var payload = new byte[8192];
            for (var i = 0; i < fileCount; i++)
            {
                var path = Path.Combine(wide.FullName, $"file-{i:D4}.bin");
                using (File.Create(path))
                {
                }

                if (i % streamStride == 0)
                {
                    WriteNamedStream(path + ":payload", payload);
                }
            }

            var original = Path.Combine(wide.FullName, "file-0000.bin");
            Assert.True(CreateHardLink(Path.Combine(wide.FullName, "link.bin"), original, IntPtr.Zero));
            var walker = new ParallelFolderWalker();
            var sequential = await walker.ScanAsync(root.FullName, null, CancellationToken.None, workerCount: 1);
            var parallel = await walker.ScanAsync(root.FullName, null, CancellationToken.None, workerCount: 4);
            var sequentialNode = sequential.Find(wide.FullName);
            var parallelNode = parallel.Find(wide.FullName);

            Assert.Equal(fileCount + 1, parallel.GetFileCount(parallelNode));
            Assert.Equal(0, parallel.GetTotalBytes(parallelNode));
            var streamCount = (fileCount - 1) / streamStride + 1;
            Assert.True(sequential.GetAllocatedBytes(sequentialNode) >= streamCount * payload.Length);
            Assert.Equal(sequential.GetAllocatedBytes(sequentialNode), parallel.GetAllocatedBytes(parallelNode));
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
            Assert.False(tree.HasIncompleteAllocation(node));
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
    public async Task Walker_AttributesTheDirectoryStreamWhenWindowsDeniesListing()
    {
        var root = Directory.CreateTempSubdirectory("devtools-restricted-directory-");
        try
        {
            var restricted = root.CreateSubdirectory("restricted");
            var file = Path.Combine(restricted.FullName, "content.bin");
            await File.WriteAllBytesAsync(file, new byte[1024 * 1024]);
            var payload = new byte[64 * 1024];
            new Random(71).NextBytes(payload);
            var streamPath = restricted.FullName + ":payload";
            WriteNamedStream(streamPath, payload);
            var expected = GetCompressedFileSize(streamPath, out var high);
            Assert.Equal(0u, high);
            Assert.Equal((uint)payload.Length, expected);

            using var current = WindowsIdentity.GetCurrent();
            var sid = Assert.IsType<SecurityIdentifier>(current.User);
            var acl = restricted.GetAccessControl(AccessControlSections.Access);
            var original = acl.GetSecurityDescriptorBinaryForm();
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ListDirectory, AccessControlType.Deny));

            try
            {
                restricted.SetAccessControl(acl);
                Assert.Throws<UnauthorizedAccessException>(() => Directory.GetFileSystemEntries(restricted.FullName));
                var tree = await new ParallelFolderWalker().ScanAsync(root.FullName, null, CancellationToken.None, workerCount: 1);
                var node = tree.Find(restricted.FullName);

                Assert.True(tree.HasIncompleteScan(node));
                Assert.True(tree.HasIncompleteAllocation(node));
                Assert.Equal(0, tree.GetFileCount(node));
                Assert.Equal(0, tree.GetTotalBytes(node));
                Assert.Equal((long)expected, tree.GetAllocatedBytes(node));
            }
            finally
            {
                acl.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access);
                restricted.SetAccessControl(acl);
            }
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
        var results = new DirectoryAllocationIndex.ClaimResult[4096];
        Parallel.For(0, results.Length, i => results[i] = index.Claim(ids[i % ids.Length]));

        Assert.Equal(ids.Length, results.Count(result => result == DirectoryAllocationIndex.ClaimResult.New));
        Assert.All(ids, id => Assert.Equal(DirectoryAllocationIndex.ClaimResult.AlreadyCounted, index.Claim(id)));
        Assert.Equal(DirectoryAllocationIndex.ClaimResult.Unavailable, index.Claim(0));
    }

    [Fact]
    public void AllocationIndex_DistinguishesDuplicatesFromExhaustedCapacity()
    {
        var index = new DirectoryAllocationIndex(32);
        const ulong first = 1;
        var shard = ((UInt128)first).GetHashCode() & 31;
        var sameShard = Enumerable.Range(2, 1024).Select(value => (ulong)value)
            .First(id => (((UInt128)id).GetHashCode() & 31) == shard);

        Assert.Equal(DirectoryAllocationIndex.ClaimResult.New, index.Claim(first));
        Assert.Equal(DirectoryAllocationIndex.ClaimResult.AlreadyCounted, index.Claim(first));
        Assert.Equal(DirectoryAllocationIndex.ClaimResult.Unavailable, index.Claim(sameShard));
    }

    [Fact]
    public void AllocationIndex_DividesTheIdentityBudgetAmongOverlappingVolumes()
    {
        const long memoryBytes = 32 * 48 * 8 * 4;
        var resources = new ScanResources(8, memoryBytes);
        var alone = Assert.IsType<DirectoryAllocationIndex>(DirectoryAllocationIndex.Create(@"C:\", resources, fullVolume: false));
        var shared = Assert.IsType<DirectoryAllocationIndex>(DirectoryAllocationIndex.Create(@"C:\", resources, fullVolume: false, volumeShare: 4));
        const ulong first = 1;
        var shard = ((UInt128)first).GetHashCode() & 31;
        var second = Enumerable.Range(2, 1024).Select(value => (ulong)value)
            .First(id => (((UInt128)id).GetHashCode() & 31) == shard);

        Assert.Equal(DirectoryAllocationIndex.ClaimResult.New, alone.Claim(first));
        Assert.Equal(DirectoryAllocationIndex.ClaimResult.New, alone.Claim(second));
        Assert.Equal(DirectoryAllocationIndex.ClaimResult.New, shared.Claim(first));
        Assert.Equal(DirectoryAllocationIndex.ClaimResult.Unavailable, shared.Claim(second));
        Assert.Equal(DirectoryAllocationIndex.ClaimResult.AlreadyCounted, shared.Claim(first));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllocationIndex_SharesTheShardLimitAcrossBothIdWidths(bool extendedFirst)
    {
        const ulong ordinary = 1;
        var shard = ((UInt128)ordinary).GetHashCode() & 31;
        var extended = Enumerable.Range(1, 1024).Select(value => new UInt128(1, (ulong)value))
            .First(id => (id.GetHashCode() & 31) == shard);
        var index = new DirectoryAllocationIndex(32);
        var first = extendedFirst ? extended : ordinary;
        var second = extendedFirst ? ordinary : extended;

        Assert.Equal(DirectoryAllocationIndex.ClaimResult.New, index.Claim(first));
        Assert.Equal(DirectoryAllocationIndex.ClaimResult.Unavailable, index.Claim(second));
        Assert.Equal(DirectoryAllocationIndex.ClaimResult.AlreadyCounted, index.Claim(first));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reader_CountsSingleLinkAllocationWhenTheIndexIsFull(bool includeSharedFile)
    {
        var root = Directory.CreateTempSubdirectory("devtools-full-index-");
        var unique = Path.Combine(root.FullName, "unique.bin");
        var shared = Path.Combine(root.FullName, "shared.bin");
        var link = Path.Combine(root.FullName, "link.bin");
        var data = new byte[65_537];
        new Random(22).NextBytes(data);
        try
        {
            await File.WriteAllBytesAsync(unique, data);
            WriteNamedStream(unique + ":payload", data);
            if (includeSharedFile)
            {
                await File.WriteAllBytesAsync(shared, data);
                Assert.True(CreateHardLink(link, shared, IntPtr.Zero));
            }

            var clusterBytes = WindowsVolumePaths.GetAllocationUnitBytes(root.Root.FullName);
            Assert.True(clusterBytes > 0);
            var reader = new DirectoryAllocationReader(new DirectoryAllocationIndex(0), clusterBytes);
            Assert.True(reader.TryRead(root.FullName, 0, [], null, CancellationToken.None,
                out var logical, out var allocated, out var files, out var incomplete));

            var expectedFiles = includeSharedFile ? 3 : 1;
            Assert.Equal(expectedFiles, files);
            Assert.Equal(expectedFiles * data.LongLength, logical);
            Assert.Equal(2 * ((data.LongLength + clusterBytes - 1) / clusterBytes) * clusterBytes, allocated);
            Assert.Equal(includeSharedFile, incomplete);
        }
        finally
        {
            File.Delete(link);
            File.Delete(shared);
            File.Delete(unique);
            root.Delete();
        }
    }

    [Fact]
    public void StreamReader_FlagsAFileWhoseMetadataCannotBeOpened()
    {
        var root = Directory.CreateTempSubdirectory("devtools-unavailable-streams-");
        try
        {
            using var directory = CreateFile(root.FullName, 1, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            Assert.False(directory.IsInvalid);
            var reader = new DirectoryAllocationReader(new DirectoryAllocationIndex(32), 4096);
            var incomplete = false;

            Assert.Equal(0, reader.ReadFileStreams(directory, [ulong.MaxValue], CancellationToken.None, ref incomplete));
            Assert.True(incomplete);
        }
        finally
        {
            root.Delete();
        }
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
