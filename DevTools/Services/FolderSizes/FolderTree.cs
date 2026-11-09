namespace DevTools.Services.FolderSizes;

/// <summary>
/// Immutable in-memory size tree for every folder on one drive. Node 0 is the drive root.
/// Children are stored once in a compact index (CSR layout) so a drive with a million folders
/// stays small, and any folder can be listed instantly without touching the disk again.
/// </summary>
public sealed class FolderTree
{
    private readonly int[] _parent;
    private readonly string[] _names;
    private readonly long[] _totalBytes;
    private readonly long[] _fileCount;
    private readonly int[] _childStart;
    private readonly int[] _childIndex;

    internal FolderTree(
        string rootPath,
        int[] parent,
        string[] names,
        long[] totalBytes,
        long[] fileCount,
        string engineName,
        TimeSpan elapsed)
    {
        if (parent.Length == 0 || parent.Length != names.Length || parent.Length != totalBytes.Length || parent.Length != fileCount.Length)
        {
            throw new ArgumentException("Folder tree arrays must be non-empty and the same length.");
        }

        RootPath = NormalizeRoot(rootPath);
        _parent = parent;
        _names = names;
        _totalBytes = totalBytes;
        _fileCount = fileCount;
        EngineName = engineName;
        Elapsed = elapsed;

        // Counting sort of nodes by parent: children of node p live in
        // _childIndex[_childStart[p] .. _childStart[p + 1]).
        var count = parent.Length;
        _childStart = new int[count + 1];
        for (var i = 1; i < count; i++)
        {
            var p = parent[i];
            if (p >= 0 && p < count && p != i)
            {
                _childStart[p + 1]++;
            }
        }

        for (var i = 0; i < count; i++)
        {
            _childStart[i + 1] += _childStart[i];
        }

        _childIndex = new int[_childStart[count]];
        var cursor = (int[])_childStart.Clone();
        for (var i = 1; i < count; i++)
        {
            var p = parent[i];
            if (p >= 0 && p < count && p != i)
            {
                _childIndex[cursor[p]++] = i;
            }
        }
    }

    /// <summary>Drive root, for example <c>C:\</c>.</summary>
    public string RootPath { get; }

    public string EngineName { get; }

    public TimeSpan Elapsed { get; }

    public int FolderCount => _parent.Length;

    public long TotalBytes => _totalBytes[0];

    public long TotalFiles => _fileCount[0];

    public long GetTotalBytes(int node) => _totalBytes[node];

    public long GetFileCount(int node) => _fileCount[node];

    public string GetName(int node) => node == 0 ? RootPath : _names[node];

    public int GetParent(int node) => node == 0 ? -1 : _parent[node];

    public bool HasChildFolders(int node) => _childStart[node + 1] > _childStart[node];

    /// <summary>Bytes held by files that sit directly in this folder (not in a subfolder).</summary>
    public long GetDirectFileBytes(int node)
    {
        var total = _totalBytes[node];
        for (var i = _childStart[node]; i < _childStart[node + 1]; i++)
        {
            total -= _totalBytes[_childIndex[i]];
        }

        return Math.Max(0, total);
    }

    /// <summary>Child folders of <paramref name="node"/>, largest first.</summary>
    public IReadOnlyList<int> GetChildren(int node, int max = int.MaxValue)
    {
        var start = _childStart[node];
        var end = _childStart[node + 1];
        var children = new int[end - start];
        Array.Copy(_childIndex, start, children, 0, children.Length);
        Array.Sort(children, (a, b) =>
        {
            var bySize = _totalBytes[b].CompareTo(_totalBytes[a]);
            return bySize != 0 ? bySize : string.Compare(_names[a], _names[b], StringComparison.OrdinalIgnoreCase);
        });
        return children.Length <= max ? children : children[..max];
    }

    public string GetPath(int node)
    {
        if (node == 0)
        {
            return RootPath;
        }

        var parts = new Stack<string>();
        var current = node;
        var guard = 0;
        while (current > 0 && guard++ < 4096)
        {
            parts.Push(_names[current]);
            current = _parent[current];
        }

        return RootPath + string.Join(Path.DirectorySeparatorChar, parts);
    }

    /// <summary>Finds the node for an absolute folder path on this drive, or -1.</summary>
    public int Find(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return -1;
        }

        var full = path.Trim();
        if (!full.StartsWith(RootPath[..2], StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        var relative = full[2..].Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Length == 0)
        {
            return 0;
        }



        var current = 0;
        foreach (var part in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var next = -1;
            for (var i = _childStart[current]; i < _childStart[current + 1]; i++)
            {
                var child = _childIndex[i];
                if (string.Equals(_names[child], part, StringComparison.OrdinalIgnoreCase))
                {
                    next = child;
                    break;
                }
            }

            if (next < 0)
            {
                return -1;
            }

            current = next;
        }

        return current;
    }

    internal static string NormalizeRoot(string rootPath)
    {
        var root = Path.GetPathRoot(rootPath);
        if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
        {
            throw new ArgumentException($"A drive-letter root is required, got '{rootPath}'.", nameof(rootPath));
        }

        return char.ToUpperInvariant(root[0]) + @":\";
    }
}

/// <summary>
/// Builds a <see cref="FolderTree"/> from absolute paths. Files add their size to every folder
/// above them; folders with a known total (for example from the raw MFT reader) can be added directly.
/// Not thread safe: feed it from one thread.
/// </summary>
public sealed class FolderTreeBuilder
{
    private readonly string _root;
    private readonly List<int> _parent = [0];
    private readonly List<string> _names = [string.Empty];
    private readonly List<long> _ownBytes = [0];
    private readonly List<long> _ownFiles = [0];
    private readonly List<long> _knownTotal = [-1];
    private readonly Dictionary<(int Parent, string Name), int> _lookup = new(new NodeKeyComparer());
    private string? _lastDirectory;
    private int _lastDirectoryNode;

    public FolderTreeBuilder(string rootPath)
    {
        _root = FolderTree.NormalizeRoot(rootPath);
    }

    public long FilesAdded { get; private set; }

    public int FolderCount => _parent.Count;

    /// <summary>Adds a file. Paths outside the root are ignored.</summary>
    public void AddFile(string fullPath, long sizeBytes)
    {
        var separator = fullPath.LastIndexOf(Path.DirectorySeparatorChar);
        if (separator < 2)
        {
            return;
        }

        var directory = fullPath[..separator];
        int node;
        if (_lastDirectory is not null && string.Equals(_lastDirectory, directory, StringComparison.Ordinal))
        {
            node = _lastDirectoryNode;
        }
        else
        {
            node = EnsureFolder(directory);
            if (node < 0)
            {
                return;
            }

            _lastDirectory = directory;
            _lastDirectoryNode = node;
        }

        _ownBytes[node] += Math.Max(0, sizeBytes);
        _ownFiles[node]++;
        FilesAdded++;
    }

    /// <summary>Adds a folder. A non-negative <paramref name="knownTotalBytes"/> sets its total directly.</summary>
    public int AddFolder(string fullPath, long knownTotalBytes = -1)
    {
        var node = EnsureFolder(fullPath);
        if (node >= 0 && knownTotalBytes >= 0)
        {
            _knownTotal[node] = knownTotalBytes;
        }

        return node;
    }

    /// <summary>Adds a folder below a known parent node (used by the directory walker).</summary>
    public int AddChildFolder(int parentNode, string name)
    {
        return GetOrAddChild(parentNode, name);
    }

    public void AddFilesToFolder(int node, long bytes, long files)
    {
        _ownBytes[node] += Math.Max(0, bytes);
        _ownFiles[node] += files;
        FilesAdded += files;
    }

    public FolderTree Build(string engineName, TimeSpan elapsed)
    {
        var count = _parent.Count;
        var parent = _parent.ToArray();
        var totals = new long[count];
        var files = new long[count];
        var known = _knownTotal.ToArray();
        for (var i = 0; i < count; i++)
        {
            totals[i] = _ownBytes[i];
            files[i] = _ownFiles[i];
        }

        // Children always get a higher index than their parent, so one reverse pass rolls
        // every folder up into its parent.
        for (var i = count - 1; i > 0; i--)
        {
            if (known[i] >= 0)
            {
                totals[i] = known[i];
            }

            totals[parent[i]] += known[parent[i]] >= 0 ? 0 : totals[i];
            files[parent[i]] += files[i];
        }

        if (known[0] >= 0)
        {
            totals[0] = known[0];
        }

        return new FolderTree(_root, parent, _names.ToArray(), totals, files, engineName, elapsed);
    }

    private int EnsureFolder(string fullPath)
    {
        if (fullPath.Length < 2 || char.ToUpperInvariant(fullPath[0]) != _root[0] || fullPath[1] != ':')
        {
            return -1;
        }

        var node = 0;
        var position = 2;
        while (position < fullPath.Length)
        {
            while (position < fullPath.Length && fullPath[position] == Path.DirectorySeparatorChar)
            {
                position++;
            }

            if (position >= fullPath.Length)
            {
                break;
            }

            var next = fullPath.IndexOf(Path.DirectorySeparatorChar, position);
            if (next < 0)
            {
                next = fullPath.Length;
            }

            node = GetOrAddChild(node, fullPath[position..next]);
            position = next + 1;
        }

        return node;
    }

    private int GetOrAddChild(int parentNode, string name)
    {
        if (_lookup.TryGetValue((parentNode, name), out var existing))
        {
            return existing;
        }

        var node = _parent.Count;
        _parent.Add(parentNode);
        _names.Add(name);
        _ownBytes.Add(0);
        _ownFiles.Add(0);
        _knownTotal.Add(-1);
        _lookup[(parentNode, name)] = node;
        return node;
    }

    private sealed class NodeKeyComparer : IEqualityComparer<(int Parent, string Name)>
    {
        public bool Equals((int Parent, string Name) x, (int Parent, string Name) y)
        {
            return x.Parent == y.Parent && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((int Parent, string Name) obj)
        {
            return HashCode.Combine(obj.Parent, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name));
        }
    }
}
