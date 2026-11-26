using System.Diagnostics;
using DevTools.Services;
using DevTools.Services.Native;
using Xunit;
using FileAttributes = System.IO.FileAttributes;

namespace DiskUsageAnalyzer.Tests;

public sealed class NativeFastEngineAndDeleterTests
{
    [Fact]
    public void NativeRustLibraryLoadsAndWalksTreeAccuratelyWithDeterministicHash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(NativeFastEngine.IsAvailable, "Expected devtools_native.dll to be built and copied beside test binary.");

        var root = Path.Combine(Path.GetTempPath(), "devtools-native-walk-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var dirA = Path.Combine(root, "alpha");
            var dirB = Path.Combine(root, "alpha", "nested");
            var dirC = Path.Combine(root, "beta");
            Directory.CreateDirectory(dirB);
            Directory.CreateDirectory(dirC);

            File.WriteAllBytes(Path.Combine(root, "root.bin"), new byte[100]);
            File.WriteAllBytes(Path.Combine(dirA, "a.bin"), new byte[250]);
            File.WriteAllBytes(Path.Combine(dirB, "b.bin"), new byte[500]);
            File.WriteAllBytes(Path.Combine(dirC, "c.bin"), new byte[1000]);

            var walk1 = NativeFastEngine.TryWalkTreeNative(root, workerCount: 4);
            Assert.NotNull(walk1);
            var rootNode = walk1!.Tree.Find(root);
            Assert.True(rootNode >= 0);
            Assert.Equal(1850, walk1.Tree.GetTotalBytes(rootNode));
            Assert.Equal(4, walk1.Tree.GetFileCount(rootNode));
            Assert.Equal(750, walk1.Tree.GetTotalBytes(walk1.Tree.Find(dirA)));
            Assert.Equal(500, walk1.Tree.GetTotalBytes(walk1.Tree.Find(dirB)));
            Assert.Equal(1000, walk1.Tree.GetTotalBytes(walk1.Tree.Find(dirC)));
            Assert.NotEqual(0UL, walk1.TreeHash);

            var measured = NativeFastEngine.TryMeasureDirectoryNative(root, workerCount: 4);
            Assert.NotNull(measured);
            Assert.Equal(1850, measured!.Value.TotalBytes);
            Assert.Equal(4, measured.Value.TotalFiles);

            // Mutating a file changes the tree hash.
            File.WriteAllBytes(Path.Combine(dirC, "c.bin"), new byte[2000]);
            var walk2 = NativeFastEngine.TryWalkTreeNative(root, workerCount: 4);
            Assert.NotNull(walk2);
            Assert.Equal(2850, walk2!.Tree.GetTotalBytes(walk2.Tree.Find(root)));
            Assert.NotEqual(walk1.TreeHash, walk2.TreeHash);
        }
        finally
        {
            FastPathDeleter.Delete(root);
        }
    }

    [Fact]
    public void FastPathDeleter_DeletesReadOnlyFilesAndUnlinksJunctionWithoutTouchingTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-fastdel-" + Guid.NewGuid().ToString("N")[..8]);
        var target = Path.Combine(Path.GetTempPath(), "devtools-fastdel-target-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git", "objects", "aa"));
            Directory.CreateDirectory(target);
            var sentinel = Path.Combine(target, "important.txt");
            File.WriteAllText(sentinel, "do not delete");

            var roFile = Path.Combine(root, ".git", "objects", "aa", "blob");
            File.WriteAllBytes(roFile, new byte[4096]);
            File.SetAttributes(roFile, FileAttributes.ReadOnly | FileAttributes.Hidden);

            var junction = Path.Combine(root, "linked_modules");
            using (var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junction}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            }))
            {
                proc?.WaitForExit(10_000);
            }

            var outcome = FastPathDeleter.Delete(root, keepRoot: false);

            Assert.True(outcome.RootRemoved);
            Assert.False(Directory.Exists(root));
            Assert.True(File.Exists(sentinel));
            Assert.Equal("do not delete", File.ReadAllText(sentinel));
        }
        finally
        {
            FastPathDeleter.Delete(root);
            FastPathDeleter.Delete(target);
        }
    }

    [Fact]
    public void FastPathDeleter_KeepRootEmptiesContentsAndPreservesFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "devtools-keeproot-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            File.WriteAllBytes(Path.Combine(root, "a.txt"), new byte[128]);
            File.WriteAllBytes(Path.Combine(root, "sub", "b.txt"), new byte[256]);

            var outcome = FastPathDeleter.Delete(root, keepRoot: true);

            Assert.True(Directory.Exists(root));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
            Assert.Equal(2, outcome.FilesDeleted);
            Assert.Equal(384, outcome.BytesFreed);
        }
        finally
        {
            FastPathDeleter.Delete(root);
        }
    }
}
