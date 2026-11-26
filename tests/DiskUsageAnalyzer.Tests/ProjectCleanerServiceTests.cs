using System.Diagnostics;
using DevTools.Services;
using Xunit;

namespace DiskUsageAnalyzer.Tests;

public sealed class ProjectCleanerServiceTests
{
    [Fact]
    public async Task ScanAndClean_HandlesDotNetSolutionNodeRustPythonAndSkipsJunctions()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "devtools-proj-test-" + Guid.NewGuid().ToString("N")[..8]);
        var externalTarget = Path.Combine(Path.GetTempPath(), "devtools-proj-junction-target-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(externalTarget);
            var externalSentinel = Path.Combine(externalTarget, "keep-me.txt");
            File.WriteAllText(externalSentinel, new string('X', 2048));

            // 1. C# .sln with nested .csproj bin/obj + .vs
            var slnDir = Path.Combine(tempRoot, "MyDotNetApp");
            var apiDir = Path.Combine(slnDir, "src", "Api");
            Directory.CreateDirectory(Path.Combine(apiDir, "bin", "Debug"));
            Directory.CreateDirectory(Path.Combine(apiDir, "obj", "Debug"));
            Directory.CreateDirectory(Path.Combine(slnDir, ".vs"));
            File.WriteAllText(Path.Combine(slnDir, "MyDotNetApp.sln"), "Microsoft Visual Studio Solution File");
            File.WriteAllText(Path.Combine(apiDir, "Api.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(apiDir, "Program.cs"), "Console.WriteLine();");
            File.WriteAllBytes(Path.Combine(apiDir, "bin", "Debug", "Api.dll"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(apiDir, "obj", "Debug", "Api.cache"), new byte[2048]);
            File.WriteAllBytes(Path.Combine(slnDir, ".vs", "state.bin"), new byte[1024]);

            // 2. Node.js project with .next (build output) and node_modules (dependencies)
            var nodeDir = Path.Combine(tempRoot, "MyWebApp");
            Directory.CreateDirectory(Path.Combine(nodeDir, ".next"));
            Directory.CreateDirectory(Path.Combine(nodeDir, "node_modules", "pkg"));
            File.WriteAllText(Path.Combine(nodeDir, "package.json"), "{}");
            File.WriteAllText(Path.Combine(nodeDir, "index.ts"), "export {};");
            File.WriteAllBytes(Path.Combine(nodeDir, ".next", "bundle.js"), new byte[3072]);
            File.WriteAllBytes(Path.Combine(nodeDir, "node_modules", "pkg", "index.js"), new byte[8192]);

            // 3. Node.js project with a JUNCTION node_modules pointing outside (must never delete external target!)
            var junctionNodeDir = Path.Combine(tempRoot, "JunctionWebApp");
            Directory.CreateDirectory(Path.Combine(junctionNodeDir, "dist"));
            File.WriteAllText(Path.Combine(junctionNodeDir, "package.json"), "{}");
            File.WriteAllBytes(Path.Combine(junctionNodeDir, "dist", "out.js"), new byte[1024]);
            var junctionPath = Path.Combine(junctionNodeDir, "node_modules");
            CreateJunction(junctionPath, externalTarget);

            // 4. Rust project with target/
            var rustDir = Path.Combine(tempRoot, "MyRustCrate");
            Directory.CreateDirectory(Path.Combine(rustDir, "target", "release"));
            File.WriteAllText(Path.Combine(rustDir, "Cargo.toml"), "[package]");
            File.WriteAllBytes(Path.Combine(rustDir, "target", "release", "app.exe"), new byte[5120]);

            // 5. Python project with __pycache__ and .venv
            var pyDir = Path.Combine(tempRoot, "MyPyTool");
            Directory.CreateDirectory(Path.Combine(pyDir, "__pycache__"));
            Directory.CreateDirectory(Path.Combine(pyDir, ".venv", "Lib"));
            File.WriteAllText(Path.Combine(pyDir, "pyproject.toml"), "[project]");
            File.WriteAllBytes(Path.Combine(pyDir, "__pycache__", "main.pyc"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(pyDir, ".venv", "Lib", "mod.py"), new byte[4096]);

            var snapshot = await ProjectCleanerService.ScanAsync(tempRoot, sizeResolver: null, progress: null, CancellationToken.None);

            Assert.Equal(5, snapshot.Projects.Count);

            var dotNet = Assert.Single(snapshot.Projects, p => p.Ecosystem == ProjectEcosystem.DotNet);
            Assert.Equal("MyDotNetApp", dotNet.Name);
            Assert.Equal(4096 + 2048 + 1024, dotNet.BuildOutputBytes);

            var node = Assert.Single(snapshot.Projects, p => p.RootPath.Equals(nodeDir, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(3072, node.BuildOutputBytes);
            Assert.Equal(8192, node.DependencyBytes);

            var junctionNode = Assert.Single(snapshot.Projects, p => p.RootPath.Equals(junctionNodeDir, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1024, junctionNode.BuildOutputBytes);
            Assert.Equal(0, junctionNode.DependencyBytes); // Junction counted as 0 and marked reparse point.

            // Clean build outputs ONLY first: bin/obj/.vs/.next/dist/target/__pycache__ deleted, node_modules & .venv kept!
            var buildClean = await ProjectCleanerService.CleanAsync(snapshot.Projects, includeDependencies: false, progress: null, CancellationToken.None);
            Assert.True(buildClean.FoldersDeleted >= 6);
            Assert.False(Directory.Exists(Path.Combine(apiDir, "bin")));
            Assert.False(Directory.Exists(Path.Combine(apiDir, "obj")));
            Assert.True(File.Exists(Path.Combine(apiDir, "Program.cs")));
            Assert.False(Directory.Exists(Path.Combine(nodeDir, ".next")));
            Assert.True(Directory.Exists(Path.Combine(nodeDir, "node_modules")));
            Assert.False(Directory.Exists(Path.Combine(rustDir, "target")));
            Assert.False(Directory.Exists(Path.Combine(pyDir, "__pycache__")));
            Assert.True(Directory.Exists(Path.Combine(pyDir, ".venv")));

            // Now clean WITH dependencies: node_modules and .venv removed, but external junction target is untouched!
            var depClean = await ProjectCleanerService.CleanAsync(snapshot.Projects, includeDependencies: true, progress: null, CancellationToken.None);
            Assert.True(depClean.FoldersDeleted >= 2);
            Assert.False(Directory.Exists(Path.Combine(nodeDir, "node_modules")));
            Assert.False(Directory.Exists(Path.Combine(pyDir, ".venv")));
            Assert.True(File.Exists(externalSentinel));
        }
        finally
        {
            FastPathDeleter.Delete(tempRoot);
            FastPathDeleter.Delete(externalTarget);
        }
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        proc?.WaitForExit(10_000);
    }
}
