using System.Diagnostics;
using DevTools.Infrastructure;
using DevTools.Services.Native;
using FileAttributes = System.IO.FileAttributes;

namespace DevTools.Services;

public enum ProjectEcosystem
{
    DotNet,
    Node,
    Rust,
    Python,
    JavaGradle,
    CMake,
    Unity
}

public enum ProjectArtifactKind
{
    /// <summary>Generated build or cache folders (bin, obj, target, .next, dist, __pycache__) that rebuild automatically.</summary>
    BuildOutput,

    /// <summary>Restored dependency folders (node_modules, .venv, packages, Unity Library) that require reinstall or reimport.</summary>
    Dependencies
}

public sealed record ProjectArtifact(
    string Path,
    string RelativePath,
    string FolderName,
    ProjectArtifactKind Kind,
    long SizeBytes,
    bool IsReparsePoint);

public sealed record CleanableProject(
    string Name,
    string RootPath,
    ProjectEcosystem Ecosystem,
    string MarkerFile,
    IReadOnlyList<ProjectArtifact> Artifacts)
{
    public long BuildOutputBytes => Artifacts
        .Where(a => a.Kind == ProjectArtifactKind.BuildOutput && !a.IsReparsePoint)
        .Sum(a => a.SizeBytes);

    public long DependencyBytes => Artifacts
        .Where(a => a.Kind == ProjectArtifactKind.Dependencies && !a.IsReparsePoint)
        .Sum(a => a.SizeBytes);

    public long GetReclaimableBytes(bool includeDependencies) =>
        includeDependencies ? BuildOutputBytes + DependencyBytes : BuildOutputBytes;

    public IReadOnlyList<ProjectArtifact> GetTargetArtifacts(bool includeDependencies) =>
        Artifacts
            .Where(a => !a.IsReparsePoint && (includeDependencies || a.Kind == ProjectArtifactKind.BuildOutput))
            .ToList();
}

public sealed record ProjectScanProgress(string StatusText, int ProjectsFound, int MeasuredCount);

public sealed record ProjectScanSnapshot(
    DateTimeOffset CapturedAt,
    string RootPath,
    IReadOnlyList<CleanableProject> Projects,
    TimeSpan Elapsed);

public sealed record ProjectCleanProgress(int CompletedFolders, int TotalFolders, long BytesFreed, string CurrentPath);

public sealed record ProjectCleanResult(
    int ProjectsCleaned,
    int FoldersDeleted,
    long FilesDeleted,
    long BytesFreed,
    IReadOnlyList<(string Path, string Error)> Failed,
    TimeSpan Elapsed);

/// <summary>
/// Discovers developer projects (C# .NET .sln/.csproj, Node package.json, Rust Cargo.toml, Python,
/// Java/Gradle/Maven, CMake, Unity), measures their cleanable build/dependency directories, and
/// cleans selected targets using the fast parallel native deleter while never recursing into junctions.
/// </summary>
public static class ProjectCleanerService
{
    private const int MaxDiscoveryDepth = 5;
    private const int MaxSolutionChildDepth = 6;

    private static readonly HashSet<string> SkipTraversalDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".hg",
        ".svn",
        "node_modules",
        "bin",
        "obj",
        "target",
        ".venv",
        "venv",
        "__pycache__",
        ".next",
        ".nuxt",
        ".vite",
        ".turbo",
        ".svelte-kit",
        ".angular",
        ".gradle",
        ".vs",
        "packages",
        "TestResults",
        "BenchmarkDotNet.Artifacts",
        "Library",
        "Temp",
        "dist",
        "build",
        "out",
        "artifacts",
        ".cache",
        ".pytest_cache",
        ".mypy_cache",
        ".ruff_cache",
        "$Recycle.Bin",
        "System Volume Information"
    };

    private static readonly HashSet<string> DotNetBuildFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin",
        "obj",
        "TestResults",
        "BenchmarkDotNet.Artifacts",
        ".vs"
    };

    private static readonly HashSet<string> DotNetDependencyFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "packages"
    };

    private static readonly HashSet<string> NodeBuildFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".next",
        ".nuxt",
        ".vite",
        ".turbo",
        ".parcel-cache",
        ".svelte-kit",
        ".angular",
        ".cache",
        ".eslintcache",
        "coverage",
        "dist",
        "build",
        "out"
    };

    private static readonly HashSet<string> PythonBuildFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "__pycache__",
        ".pytest_cache",
        ".mypy_cache",
        ".ruff_cache",
        ".tox",
        "htmlcov",
        "dist",
        "build"
    };

    private static readonly HashSet<string> PythonDependencyFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".venv",
        "venv"
    };

    private static readonly HashSet<string> JavaBuildFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "target",
        "build",
        ".gradle"
    };

    private static readonly HashSet<string> UnityBuildFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Temp",
        "Obj",
        "Logs"
    };

    public static string EcosystemDisplayName(ProjectEcosystem ecosystem) => ecosystem switch
    {
        ProjectEcosystem.DotNet => "C# / .NET",
        ProjectEcosystem.Node => "Node.js",
        ProjectEcosystem.Rust => "Rust",
        ProjectEcosystem.Python => "Python",
        ProjectEcosystem.JavaGradle => "Java / Gradle",
        ProjectEcosystem.CMake => "C++ / CMake",
        ProjectEcosystem.Unity => "Unity",
        _ => ecosystem.ToString()
    };

    public static async Task<ProjectScanSnapshot> ScanAsync(
        string rootPath,
        Func<string, CancellationToken, Task<long?>>? sizeResolver,
        IProgress<ProjectScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var fullRoot = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullRoot))
        {
            clock.Stop();
            return new ProjectScanSnapshot(DateTimeOffset.Now, fullRoot, [], clock.Elapsed);
        }

        progress?.Report(new ProjectScanProgress($"Scanning for projects under {fullRoot}...", 0, 0));

        var rawProjects = await Task.Run(() => DiscoverProjects(fullRoot, cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        progress?.Report(new ProjectScanProgress(
            $"Found {rawProjects.Count} project(s). Measuring cleanable folders...",
            rawProjects.Count,
            0));

        // Collect all unique artifact paths across projects so each folder is measured at most once.
        var allArtifacts = rawProjects
            .SelectMany(p => p.Artifacts)
            .Where(a => !a.IsReparsePoint)
            .Select(a => a.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sizes = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var measured = 0;
        var workerLimit = Math.Clamp(Environment.ProcessorCount, 4, 16);

        await Parallel.ForEachAsync(
            allArtifacts,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = workerLimit,
                CancellationToken = cancellationToken
            },
            async (artifactPath, ct) =>
            {
                long bytes = 0;
                if (sizeResolver is not null)
                {
                    bytes = await sizeResolver(artifactPath, ct).ConfigureAwait(false) ?? 0;
                }
                else
                {
                    bytes = MeasureFolderFast(artifactPath, ct);
                }

                sizes[artifactPath] = bytes;
                var done = Interlocked.Increment(ref measured);
                if ((done & 7) == 0 || done == allArtifacts.Count)
                {
                    progress?.Report(new ProjectScanProgress(
                        $"Measured {done} of {allArtifacts.Count} cleanable folder(s) across {rawProjects.Count} project(s)...",
                        rawProjects.Count,
                        done));
                }
            }).ConfigureAwait(false);

        var finalized = new List<CleanableProject>(rawProjects.Count);
        foreach (var project in rawProjects)
        {
            var sizedArtifacts = project.Artifacts
                .Select(a => a with { SizeBytes = a.IsReparsePoint ? 0 : sizes.GetValueOrDefault(a.Path, 0) })
                .OrderByDescending(a => a.SizeBytes)
                .ThenBy(a => a.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sizedArtifacts.Any(a => !a.IsReparsePoint && a.SizeBytes > 0))
            {
                finalized.Add(project with { Artifacts = sizedArtifacts });
            }
        }

        finalized.Sort((a, b) =>
        {
            var cmp = b.GetReclaimableBytes(includeDependencies: true)
                .CompareTo(a.GetReclaimableBytes(includeDependencies: true));
            return cmp != 0 ? cmp : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        });

        clock.Stop();
        progress?.Report(new ProjectScanProgress(
            $"Found {finalized.Count} cleanable project(s) under {fullRoot} in {clock.Elapsed.TotalSeconds:F1}s.",
            finalized.Count,
            allArtifacts.Count));

        return new ProjectScanSnapshot(DateTimeOffset.Now, fullRoot, finalized, clock.Elapsed);
    }

    public static async Task<ProjectCleanResult> CleanAsync(
        IReadOnlyList<CleanableProject> selectedProjects,
        bool includeDependencies,
        IProgress<ProjectCleanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var targets = new List<ProjectArtifact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in selectedProjects)
        {
            foreach (var artifact in project.GetTargetArtifacts(includeDependencies))
            {
                if (artifact.IsReparsePoint || !Directory.Exists(artifact.Path))
                {
                    continue;
                }

                if (seen.Add(artifact.Path))
                {
                    targets.Add(artifact);
                }
            }
        }

        // Skip any target that is already inside another target (nested artifacts).
        var filteredTargets = FilterNonOverlappingPaths(targets);

        var completed = 0;
        var deletedFolders = 0;
        long filesDeleted = 0;
        long bytesFreed = 0;
        var failed = new List<(string Path, string Error)>();
        var sync = new object();

        using var gate = new SemaphoreSlim(4, 4);
        var tasks = filteredTargets.Select(async artifact =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = await FastPathDeleter.DeleteAsync(artifact.Path, keepRoot: false, workerCount: 0, cancellationToken)
                    .ConfigureAwait(false);
                lock (sync)
                {
                    completed++;
                    filesDeleted += outcome.FilesDeleted;
                    bytesFreed += outcome.BytesFreed > 0 ? outcome.BytesFreed : (outcome.RootRemoved ? artifact.SizeBytes : 0);
                    if (outcome.RootRemoved || !Directory.Exists(artifact.Path))
                    {
                        deletedFolders++;
                    }
                    else
                    {
                        failed.Add((artifact.Path, $"{outcome.Skipped} locked or protected item(s) remained."));
                    }

                    progress?.Report(new ProjectCleanProgress(completed, filteredTargets.Count, bytesFreed, artifact.Path));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lock (sync)
                {
                    completed++;
                    failed.Add((artifact.Path, ex.Message));
                    progress?.Report(new ProjectCleanProgress(completed, filteredTargets.Count, bytesFreed, artifact.Path));
                }
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        clock.Stop();
        AppLog.Info($"Project clean finished: {deletedFolders}/{filteredTargets.Count} folder(s), {filesDeleted:N0} files, {CleanupRules.FormatBytes(bytesFreed)} in {clock.Elapsed.TotalSeconds:F2}s.");
        return new ProjectCleanResult(selectedProjects.Count, deletedFolders, filesDeleted, bytesFreed, failed, clock.Elapsed);
    }

    internal static List<CleanableProject> DiscoverProjects(string rootPath, CancellationToken cancellationToken)
    {
        var results = new List<CleanableProject>();
        var claimedDotNetRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claimedArtifactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(string dir, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > MaxDiscoveryDepth || IsReparseDirectory(dir))
            {
                return;
            }

            string[] files;
            string[] subdirs;
            try
            {
                files = Directory.GetFiles(dir);
                subdirs = Directory.GetDirectories(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            var fileNames = files.Select(Path.GetFileName).OfType<string>().ToArray();
            var subdirNames = subdirs.Select(Path.GetFileName).OfType<string>().ToArray();

            // 1. C# / .NET solution (.sln / .slnx)
            var slnFile = fileNames.FirstOrDefault(f =>
                f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
            if (slnFile is not null)
            {
                claimedDotNetRoots.Add(dir);
                var artifacts = CollectDotNetArtifacts(dir, claimedArtifactPaths, cancellationToken);
                if (artifacts.Count > 0)
                {
                    results.Add(new CleanableProject(
                        Path.GetFileNameWithoutExtension(slnFile),
                        dir,
                        ProjectEcosystem.DotNet,
                        slnFile,
                        artifacts));
                }
            }
            else if (!IsUnderAny(dir, claimedDotNetRoots))
            {
                var projFile = fileNames.FirstOrDefault(f =>
                    f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                    f.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase));
                if (projFile is not null)
                {
                    claimedDotNetRoots.Add(dir);
                    var artifacts = CollectDotNetArtifacts(dir, claimedArtifactPaths, cancellationToken);
                    if (artifacts.Count > 0)
                    {
                        results.Add(new CleanableProject(
                            Path.GetFileNameWithoutExtension(projFile),
                            dir,
                            ProjectEcosystem.DotNet,
                            projFile,
                            artifacts));
                    }
                }
            }

            // 2. Node.js (package.json)
            if (fileNames.Any(f => f.Equals("package.json", StringComparison.OrdinalIgnoreCase)))
            {
                var artifacts = CollectDirectArtifacts(dir, NodeBuildFolders, ["node_modules"], claimedArtifactPaths);
                if (artifacts.Count > 0)
                {
                    results.Add(new CleanableProject(
                        BuildFriendlyProjectName(rootPath, dir),
                        dir,
                        ProjectEcosystem.Node,
                        "package.json",
                        artifacts));
                }
            }

            // 3. Rust (Cargo.toml)
            if (fileNames.Any(f => f.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase)))
            {
                var artifacts = CollectDirectArtifacts(dir, ["target"], [], claimedArtifactPaths);
                if (artifacts.Count > 0)
                {
                    results.Add(new CleanableProject(
                        BuildFriendlyProjectName(rootPath, dir),
                        dir,
                        ProjectEcosystem.Rust,
                        "Cargo.toml",
                        artifacts));
                }
            }

            // 4. Python (pyproject.toml / requirements.txt / setup.py / Pipfile)
            var pythonMarker = fileNames.FirstOrDefault(f =>
                f.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("setup.py", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("Pipfile", StringComparison.OrdinalIgnoreCase));
            if (pythonMarker is not null)
            {
                var artifacts = CollectDirectArtifacts(dir, PythonBuildFolders, PythonDependencyFolders, claimedArtifactPaths);
                if (artifacts.Count > 0)
                {
                    results.Add(new CleanableProject(
                        BuildFriendlyProjectName(rootPath, dir),
                        dir,
                        ProjectEcosystem.Python,
                        pythonMarker,
                        artifacts));
                }
            }

            // 5. Java / Gradle / Maven
            var javaMarker = fileNames.FirstOrDefault(f =>
                f.Equals("pom.xml", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("build.gradle", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("build.gradle.kts", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("settings.gradle", StringComparison.OrdinalIgnoreCase) ||
                f.Equals("settings.gradle.kts", StringComparison.OrdinalIgnoreCase));
            if (javaMarker is not null)
            {
                var artifacts = CollectDirectArtifacts(dir, JavaBuildFolders, [], claimedArtifactPaths);
                if (artifacts.Count > 0)
                {
                    results.Add(new CleanableProject(
                        BuildFriendlyProjectName(rootPath, dir),
                        dir,
                        ProjectEcosystem.JavaGradle,
                        javaMarker,
                        artifacts));
                }
            }

            // 6. Unity (ProjectSettings/ProjectVersion.txt)
            if (subdirNames.Any(d => d.Equals("ProjectSettings", StringComparison.OrdinalIgnoreCase)) &&
                File.Exists(Path.Combine(dir, "ProjectSettings", "ProjectVersion.txt")))
            {
                var artifacts = CollectDirectArtifacts(dir, UnityBuildFolders, ["Library"], claimedArtifactPaths);
                if (artifacts.Count > 0)
                {
                    results.Add(new CleanableProject(
                        BuildFriendlyProjectName(rootPath, dir),
                        dir,
                        ProjectEcosystem.Unity,
                        "ProjectSettings/ProjectVersion.txt",
                        artifacts));
                }
            }

            // 7. CMake (CMakeLists.txt)
            if (fileNames.Any(f => f.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)))
            {
                var cmakeArtifacts = new List<ProjectArtifact>();
                foreach (var sub in subdirs)
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase) &&
                        claimedArtifactPaths.Add(sub))
                    {
                        cmakeArtifacts.Add(new ProjectArtifact(sub, name, name, ProjectArtifactKind.BuildOutput, 0, IsReparseDirectory(sub)));
                    }
                }

                if (cmakeArtifacts.Count > 0)
                {
                    results.Add(new CleanableProject(
                        BuildFriendlyProjectName(rootPath, dir),
                        dir,
                        ProjectEcosystem.CMake,
                        "CMakeLists.txt",
                        cmakeArtifacts));
                }
            }

            if (depth == MaxDiscoveryDepth)
            {
                return;
            }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(name) || SkipTraversalDirectories.Contains(name) || name.StartsWith('.'))
                {
                    continue;
                }

                Walk(sub, depth + 1);
            }
        }

        Walk(rootPath, 0);
        return results;
    }

    private static List<ProjectArtifact> CollectDotNetArtifacts(
        string solutionRoot,
        HashSet<string> claimedArtifactPaths,
        CancellationToken cancellationToken)
    {
        var artifacts = new List<ProjectArtifact>();

        void Recurse(string current, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > MaxSolutionChildDepth || IsReparseDirectory(current))
            {
                return;
            }

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            var hasProjOrSln = false;
            try
            {
                hasProjOrSln = Directory.EnumerateFiles(current)
                    .Any(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
                              f.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase) ||
                              f.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase) ||
                              f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                              f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
            }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                // Solution-level artifacts (.vs, TestResults, packages, BenchmarkDotNet.Artifacts) or project-level bin/obj.
                var isRootOnly = depth == 0 && (name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                                                name.Equals("packages", StringComparison.OrdinalIgnoreCase) ||
                                                name.Equals("TestResults", StringComparison.OrdinalIgnoreCase) ||
                                                name.Equals("BenchmarkDotNet.Artifacts", StringComparison.OrdinalIgnoreCase));

                if ((hasProjOrSln && DotNetBuildFolders.Contains(name)) || isRootOnly)
                {
                    var kind = DotNetDependencyFolders.Contains(name)
                        ? ProjectArtifactKind.Dependencies
                        : ProjectArtifactKind.BuildOutput;
                    if (claimedArtifactPaths.Add(sub))
                    {
                        var rel = Path.GetRelativePath(solutionRoot, sub);
                        artifacts.Add(new ProjectArtifact(sub, rel, name, kind, 0, IsReparseDirectory(sub)));
                    }

                    continue;
                }

                if (SkipTraversalDirectories.Contains(name) || name.StartsWith('.'))
                {
                    continue;
                }

                Recurse(sub, depth + 1);
            }
        }

        Recurse(solutionRoot, 0);
        return artifacts;
    }

    private static List<ProjectArtifact> CollectDirectArtifacts(
        string projectDir,
        IEnumerable<string> buildFolderNames,
        IEnumerable<string> dependencyFolderNames,
        HashSet<string> claimedArtifactPaths)
    {
        var buildSet = buildFolderNames is HashSet<string> bs ? bs : new HashSet<string>(buildFolderNames, StringComparer.OrdinalIgnoreCase);
        var depSet = dependencyFolderNames is HashSet<string> ds ? ds : new HashSet<string>(dependencyFolderNames, StringComparer.OrdinalIgnoreCase);
        var artifacts = new List<ProjectArtifact>();

        string[] subdirs;
        try
        {
            subdirs = Directory.GetDirectories(projectDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return artifacts;
        }

        foreach (var sub in subdirs)
        {
            var name = Path.GetFileName(sub);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            ProjectArtifactKind? kind = buildSet.Contains(name)
                ? ProjectArtifactKind.BuildOutput
                : depSet.Contains(name)
                    ? ProjectArtifactKind.Dependencies
                    : null;

            if (kind is { } artifactKind && claimedArtifactPaths.Add(sub))
            {
                artifacts.Add(new ProjectArtifact(sub, name, name, artifactKind, 0, IsReparseDirectory(sub)));
            }
        }

        return artifacts;
    }

    private static List<ProjectArtifact> FilterNonOverlappingPaths(List<ProjectArtifact> artifacts)
    {
        var ordered = artifacts.OrderBy(a => a.Path.Length).ToList();
        var kept = new List<ProjectArtifact>(ordered.Count);
        foreach (var candidate in ordered)
        {
            var covered = kept.Any(parent =>
                candidate.Path.StartsWith(parent.Path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (!covered)
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }

    private static long MeasureFolderFast(string path, CancellationToken cancellationToken)
    {
        // Use workerCount: 1 (zero-thread-spawn inline Rust walk) because ScanAsync already runs across CPU cores.
        if (NativeFastEngine.TryMeasureDirectoryNative(path, workerCount: 1, cancellationToken) is { } native)
        {
            return native.TotalBytes;
        }

        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = stack.Pop();
            try
            {
                var entries = new System.IO.Enumeration.FileSystemEnumerable<(string Name, bool IsDir, bool Reparse, long Length)>(
                    current,
                    (ref System.IO.Enumeration.FileSystemEntry entry) =>
                        (entry.FileName.ToString(), entry.IsDirectory, (entry.Attributes & FileAttributes.ReparsePoint) != 0, entry.Length),
                    options);
                foreach (var entry in entries)
                {
                    if (entry.IsDir)
                    {
                        if (!entry.Reparse)
                        {
                            stack.Push(Path.Combine(current, entry.Name));
                        }
                    }
                    else
                    {
                        total += Math.Max(0, entry.Length);
                    }
                }
            }
            catch
            {
            }
        }

        return total;
    }

    private static bool IsReparseDirectory(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnderAny(string dir, HashSet<string> ancestors)
    {
        foreach (var root in ancestors)
        {
            if (dir.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildFriendlyProjectName(string scanRoot, string projectDir)
    {
        var rel = Path.GetRelativePath(scanRoot, projectDir);
        if (rel == "." || string.IsNullOrWhiteSpace(rel))
        {
            return Path.GetFileName(projectDir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }
}
