using System.Collections;
using System.Collections.Concurrent;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DevTools.Services.BiggestFiles.Ntfs;

namespace DevTools.Services.FolderSizes;

internal readonly record struct NtfsRecordAllocation(long ReservedBytes, long UnlinkedBytes,
    NtfsBitmapAllocation? Bitmap = null, int IncompleteRecordCount = 0);

/// <summary>
/// Immutable in-memory size tree for every folder on one drive. Node 0 is the drive root.
/// Children are stored once in a compact index (CSR layout) so a drive with a million folders
/// stays small, and any folder can be listed instantly without touching the disk again.
/// </summary>
public sealed class FolderTree
{
    private const int MinimumIndexedChildren = 1024;

    private readonly int[] _parent;
    private readonly string[] _names;
    private readonly long[] _totalBytes;
    private readonly long[] _fileCount;
    private readonly long[]? _allocatedBytes;
    private readonly int[] _childStart;
    private readonly int[]? _childIndex;
    private readonly BitArray? _incompleteSubtrees;
    private readonly BitArray? _incompleteAllocationSubtrees;
    private ConcurrentDictionary<int, ChildNameIndex>? _childNameIndexes;
    private Comparison<int>? _compareLogicalChildren;
    private Comparison<int>? _compareAllocatedChildren;

    internal FolderTree(
        string rootPath,
        int[] parent,
        string[] names,
        long[] totalBytes,
        long[] fileCount,
        string engineName,
        TimeSpan elapsed,
        long[]? allocatedBytes = null,
        NtfsRecordAllocation? ntfsAllocation = null,
        BitArray? incompleteSubtrees = null,
        int incompleteDirectoryCount = 0,
        BitArray? incompleteAllocationSubtrees = null,
        int incompleteAllocationDirectoryCount = 0,
        int[]? contiguousChildStarts = null)
    {
        if (parent.Length == 0 || parent.Length != names.Length || parent.Length != totalBytes.Length ||
            parent.Length != fileCount.Length || (allocatedBytes is not null && parent.Length != allocatedBytes.Length))
        {
            throw new ArgumentException("Folder tree arrays must be non-empty and the same length.");
        }

        RootPath = NormalizeRoot(rootPath);
        _parent = parent;
        _names = names;
        _totalBytes = totalBytes;
        _fileCount = fileCount;
        _allocatedBytes = allocatedBytes;
        _incompleteSubtrees = incompleteSubtrees;
        IncompleteDirectoryCount = incompleteDirectoryCount;
        _incompleteAllocationSubtrees = incompleteAllocationSubtrees;
        IncompleteAllocationDirectoryCount = incompleteAllocationDirectoryCount;
        NtfsAllocation = ntfsAllocation;
        EngineName = engineName;
        Elapsed = elapsed;

        var count = parent.Length;
        if (contiguousChildStarts is not null)
        {
            if (contiguousChildStarts.Length != count + 1 ||
                contiguousChildStarts[0] != 0 || contiguousChildStarts[count] != count - 1)
            {
                throw new ArgumentException("The contiguous child ranges must cover all non-root folders.", nameof(contiguousChildStarts));
            }

            _childStart = contiguousChildStarts;
            return;
        }

        // Counting sort by parent when the caller has not already grouped each parent's children.
        _childStart = new int[count + 1];
        var previousParent = 0;
        var firstInterleaved = 1;
        for (; firstInterleaved < count; firstInterleaved++)
        {
            var p = parent[firstInterleaved];
            if (p < previousParent || p >= count || p == firstInterleaved)
            {
                break;
            }

            _childStart[p]++;
            previousParent = p;
        }

        var childCount = 0;
        if (firstInterleaved == count)
        {
            for (var i = 0; i < count; i++)
            {
                var groupLength = _childStart[i];
                _childStart[i] = childCount;
                childCount += groupLength;
            }

            _childStart[count] = childCount;
            return;
        }

        for (var i = firstInterleaved; i < count; i++)
        {
            var p = parent[i];
            if (p >= 0 && p < count && p != i)
            {
                _childStart[p]++;
            }
        }

        for (var i = 0; i < count; i++)
        {
            childCount += _childStart[i];
            _childStart[i] = childCount;
        }

        _childStart[count] = childCount;
        _childIndex = new int[childCount];

        // Each offset starts at the end of its group. Filling in reverse leaves the offsets
        // at the group starts and preserves the original child order without a second array.
        for (var i = count - 1; i > 0; i--)
        {
            var p = parent[i];
            if (p >= 0 && p < count && p != i)
            {
                _childIndex[--_childStart[p]] = i;
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

    internal NtfsRecordAllocation? NtfsAllocation { get; }

    internal int IncompleteDirectoryCount { get; }

    internal bool HasIncompleteScan(int node) => _incompleteSubtrees?[node] ?? false;

    internal int IncompleteAllocationDirectoryCount { get; }

    internal bool HasIncompleteAllocation(int node) => _incompleteAllocationSubtrees?[node] ?? false;

    public long? GetAllocatedBytes(int node) => _allocatedBytes?[node];

    public long GetTotalBytes(int node) => _totalBytes[node];

    public long GetFileCount(int node) => _fileCount[node];

    public string GetName(int node) => node == 0 ? RootPath : _names[node];

    public int GetParent(int node) => node == 0 ? -1 : _parent[node];

    public bool HasChildFolders(int node) => GetChildFolderCount(node) > 0;
    public int GetChildFolderCount(int node) => _childStart[node + 1] - _childStart[node];

    /// <summary>Bytes held by files directly in this folder.</summary>
    public long GetDirectFileBytes(int node) => GetDirectChildRemainder(node, _totalBytes);

    /// <summary>Number of files directly in this folder.</summary>
    public long GetDirectFileCount(int node) => GetDirectChildRemainder(node, _fileCount);

    internal (long Bytes, long FileCount) GetDirectFileStatistics(int node)
    {
        var start = _childStart[node];
        var count = _childStart[node + 1] - start;
        var bytes = _totalBytes[node];
        var files = _fileCount[node];
        if (_childIndex is null)
        {
            bytes -= SumContiguousChildren(_totalBytes.AsSpan(start + 1, count));
            files -= SumContiguousChildren(_fileCount.AsSpan(start + 1, count));
        }
        else
        {
            foreach (var child in _childIndex.AsSpan(start, count))
            {
                bytes -= _totalBytes[child];
                files -= _fileCount[child];
            }
        }

        return (Math.Max(0, bytes), Math.Max(0, files));
    }

    internal long? GetDirectAllocatedBytes(int node) =>
        _allocatedBytes is null ? null : GetDirectChildRemainder(node, _allocatedBytes);

    private long GetDirectChildRemainder(int node, long[] totals)
    {
        var start = _childStart[node];
        var count = _childStart[node + 1] - start;
        var total = totals[node];
        if (_childIndex is null)
        {
            total -= SumContiguousChildren(totals.AsSpan(start + 1, count));
        }
        else
        {
            foreach (var child in _childIndex.AsSpan(start, count))
            {
                total -= totals[child];
            }
        }

        return Math.Max(0, total);
    }

    private static long SumContiguousChildren(ReadOnlySpan<long> sizes)
    {
        long total = 0;
        if (Vector.IsHardwareAccelerated && sizes.Length >= 4 * Vector<long>.Count)
        {
            var vectors = MemoryMarshal.Cast<long, Vector<long>>(sizes);
            var sums = Vector<long>.Zero;
            foreach (var vector in vectors)
            {
                sums += vector;
            }

            total = Vector.Sum(sums);
            sizes = sizes[(vectors.Length * Vector<long>.Count)..];
        }

        foreach (var size in sizes)
        {
            total += size;
        }

        return total;
    }

    /// <summary>Child folders of <paramref name="node"/>, largest first.</summary>
    public IReadOnlyList<int> GetChildren(int node, int max = int.MaxValue) =>
        GetChildren(node, max, _totalBytes);

    internal IReadOnlyList<int> GetChildrenByAllocation(int node, int max) =>
        GetChildren(node, max, _allocatedBytes);

    private IReadOnlyList<int> GetChildren(int node, int max, long[]? sizes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(max);
        var start = _childStart[node];
        var count = GetChildFolderCount(node);
        if (max == 0 || count == 0 || sizes is null)
        {
            return [];
        }

        if (count >= 1024 && max <= count / 4)
        {
            // Retain only the requested children of wide folders. The lowest-ranked entry leaves first.
            var heap = new int[max];
            CopyChildRange(start, heap);
            for (var i = heap.Length / 2 - 1; i >= 0; i--)
            {
                SiftWorstDown(heap, i, heap[i], sizes);
            }

            var worst = heap[0];
            var cutoff = sizes[worst];
            var end = start + count;
            if (_childIndex is { } indexes)
            {
                for (var slot = start + max; slot < end; slot++)
                {
                    var child = indexes[slot];
                    var size = sizes[child];
                    if (size > cutoff || (size == cutoff && CompareChildNames(child, worst) < 0))
                    {
                        SiftWorstDown(heap, 0, child, sizes);
                        worst = heap[0];
                        cutoff = sizes[worst];
                    }
                }
            }
            else
            {
                for (var child = start + max + 1; child <= end; child++)
                {
                    var size = sizes[child];
                    if (size > cutoff || (size == cutoff && CompareChildNames(child, worst) < 0))
                    {
                        SiftWorstDown(heap, 0, child, sizes);
                        worst = heap[0];
                        cutoff = sizes[worst];
                    }
                }
            }

            SortChildren(heap, sizes);
            return heap;
        }

        var children = new int[count];
        CopyChildRange(start, children);
        SortChildren(children, sizes);
        return count <= max ? children : children[..max];
    }


    private void CopyChildRange(int start, Span<int> destination)
    {
        if (_childIndex is not null)
        {
            _childIndex.AsSpan(start, destination.Length).CopyTo(destination);
            return;
        }

        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = start + index + 1;
        }
    }

    private void SiftWorstDown(int[] heap, int position, int node, long[] sizes)
    {
        var nodeSize = sizes[node];
        while (position < heap.Length / 2)
        {
            var child = position * 2 + 1;
            var worstChild = heap[child];
            var right = child + 1;
            if (right < heap.Length && CompareChildren(heap[right], worstChild, sizes) > 0)
            {
                child = right;
                worstChild = heap[child];
            }

            var childSize = sizes[worstChild];
            if (childSize > nodeSize || (childSize == nodeSize && CompareChildNames(worstChild, node) <= 0))
            {
                break;
            }

            heap[position] = worstChild;
            position = child;
        }

        heap[position] = node;
    }

    private void SortChildren(int[] children, long[] sizes)
    {
        var compare = ReferenceEquals(sizes, _totalBytes)
            ? _compareLogicalChildren ??= CompareLogicalChildren
            : _compareAllocatedChildren ??= CompareAllocatedChildren;
        Array.Sort(children, compare);
    }

    private int CompareLogicalChildren(int left, int right) => CompareChildren(left, right, _totalBytes);

    private int CompareAllocatedChildren(int left, int right) => CompareChildren(left, right, _allocatedBytes!);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int CompareChildren(int left, int right, long[] sizes)
    {
        var bySize = sizes[right].CompareTo(sizes[left]);
        return bySize != 0 ? bySize : CompareChildNames(left, right);
    }

    private int CompareChildNames(int left, int right)
    {
        var byName = string.Compare(_names[left], _names[right], StringComparison.OrdinalIgnoreCase);
        return byName != 0 ? byName : left.CompareTo(right);
    }

    public string GetPath(int node)
    {
        if (node <= 0)
        {
            return RootPath;
        }

        if (_parent[node] <= 0)
        {
            return string.Concat(RootPath, _names[node]);
        }

        var length = RootPath.Length - 1;
        var count = 0;
        for (var current = node; current > 0 && count < 4096; current = _parent[current])
        {
            length = checked(length + _names[current].AsSpan().Length + 1);
            count++;
        }

        // Write leaf-to-root into the final string without building a stack or a relative path.
        return string.Create(length, (Tree: this, Node: node, Count: count), static (destination, state) =>
        {
            var offset = destination.Length;
            var current = state.Node;
            for (var i = 0; i < state.Count; i++)
            {
                var name = state.Tree._names[current].AsSpan();
                offset -= name.Length;
                name.CopyTo(destination[offset..]);
                destination[--offset] = Path.DirectorySeparatorChar;
                current = state.Tree._parent[current];
            }

            // The root already ends in a separator, so let it replace the first one.
            state.Tree.RootPath.AsSpan().CopyTo(destination);
        });
    }

    /// <summary>Finds the node for an absolute folder path on this drive, or -1.</summary>
    public int Find(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return -1;
        }

        var full = path.AsSpan().Trim();
        var relativeStart = GetRelativeStart(RootPath, full);
        if (relativeStart < 0)
        {
            return -1;
        }

        var relative = full[relativeStart..];
        var current = 0;
        while (!relative.IsEmpty)
        {
            var separator = relative.IndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var part = separator < 0 ? relative : relative[..separator];
            relative = separator < 0 ? [] : relative[(separator + 1)..];
            if (part.IsEmpty)
            {
                continue;
            }

            var children = GetChildRange(current);
            var next = children.Length < MinimumIndexedChildren
                ? FindChildByName(children, _names, part)
                : FindIndexedChild(current, children, part);
            if (next < 0)
            {
                return -1;
            }

            current = next;
        }

        return current;
    }

    private int FindIndexedChild(int parent, ChildRange children, ReadOnlySpan<char> searchName)
    {
        var indexes = LazyInitializer.EnsureInitialized(ref _childNameIndexes);
        var index = indexes.GetOrAdd(parent, static (node, tree) => new ChildNameIndex(tree, node), this);
        if (index.GetHashes() is not { } hashes)
        {
            return FindChildByName(children, _names, searchName);
        }

        // The hash narrows the search. Verify the actual name, including case-sensitive siblings.
        var wanted = string.GetHashCode(searchName, StringComparison.OrdinalIgnoreCase);
        Span<int> candidates = stackalloc int[32];
        var count = 0;
        var start = 0;
        while (true)
        {
            var offset = hashes.AsSpan(start).IndexOf(wanted);
            if (offset < 0)
            {
                return FindChildByName(candidates[..count], _names, searchName);
            }

            start += offset;
            var child = children[start++];
            if (searchName.SequenceEqual(_names[child]))
            {
                return child;
            }

            if (count == candidates.Length)
            {
                return FindChildByName(children, _names, searchName);
            }

            candidates[count++] = child;
        }
    }

    private int[] BuildChildNameHashes(int parent)
    {
        var children = GetChildRange(parent);
        var hashes = new int[children.Length];
        for (var index = 0; index < hashes.Length; index++)
        {
            hashes[index] = string.GetHashCode(_names[children[index]].AsSpan(), StringComparison.OrdinalIgnoreCase);
        }

        return hashes;
    }

    private sealed class ChildNameIndex(FolderTree tree, int parent)
    {
        private readonly Lazy<int[]> _hashes = new(() => tree.BuildChildNameHashes(parent));
        private int _lookups;

        // Let one-off path probes use the existing child span. The retained index costs four bytes per child.
        public int[]? GetHashes() =>
            _hashes.IsValueCreated || Interlocked.Increment(ref _lookups) >= 4 ? _hashes.Value : null;
    }

    private ChildRange GetChildRange(int parent)
    {
        var start = _childStart[parent];
        var count = GetChildFolderCount(parent);
        return new ChildRange(_childIndex is null ? default : _childIndex.AsSpan(start, count), start + 1, count);
    }

    // A grouped tree can use consecutive node numbers without storing an index for each child.
    private readonly ref struct ChildRange(ReadOnlySpan<int> indexes, int first, int length)
    {
        private readonly ReadOnlySpan<int> _indexes = indexes;
        private readonly int _first = first;

        public int Length { get; } = length;
        public int this[int index] => _indexes.IsEmpty ? _first + index : _indexes[index];
    }

    internal static int FindChildByName(ReadOnlySpan<int> children, string?[] names, ReadOnlySpan<char> searchName) =>
        FindChildByName(new ChildRange(children, 0, children.Length), names, searchName);

    private static int FindChildByName(ChildRange children, string?[] names, ReadOnlySpan<char> searchName)
    {
        var match = -1;
        var ambiguous = false;
        for (var index = 0; index < children.Length; index++)
        {
            var child = children[index];
            var name = names[child];
            if (!searchName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (searchName.SequenceEqual(name))
            {
                return child;
            }

            ambiguous = match >= 0;
            match = child;
        }

        return ambiguous ? -1 : match;
    }

    internal static string NormalizeRoot(string rootPath)
    {
        var root = Path.GetPathRoot(rootPath);
        if (root is { Length: >= 2 } && root[1] == ':')
        {
            return char.ToUpperInvariant(root[0]) + @":\";
        }

        if (WindowsVolumePaths.GetVolumeGuid(root).HasValue)
        {
            return root!;
        }

        throw new ArgumentException($"A local volume path is required, got '{rootPath}'.", nameof(rootPath));
    }

    internal static int GetRelativeStart(string root, ReadOnlySpan<char> path)
    {
        var rootLength = root.Length - 1;
        if (!path.StartsWith(root.AsSpan(0, rootLength), StringComparison.OrdinalIgnoreCase))
        {
            return -1;
        }

        if (root[1] != ':' && path.Length > rootLength && !Path.EndsInDirectorySeparator(path[..(rootLength + 1)]))
        {
            return -1;
        }

        return rootLength;
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
    private readonly List<long>? _ownAllocatedBytes;
    private List<long>? _knownTotals;
    private List<int>? _incompleteDirectories;
    private HashSet<int>? _incompleteAllocationDirectories;
    private readonly Dictionary<(int Parent, string Name), int> _lookup = new(new NodeKeyComparer());
    private string? _lastDirectory;
    private int _lastDirectoryNode;

    public FolderTreeBuilder(string rootPath, bool trackAllocation = false)
    {
        _root = FolderTree.NormalizeRoot(rootPath);
        _ownAllocatedBytes = trackAllocation ? [0] : null;
    }

    public long FilesAdded { get; private set; }

    public int FolderCount => _parent.Count;

    internal string GetPath(int node)
    {
        var names = new Stack<string>();
        for (var current = node; current > 0; current = _parent[current])
        {
            names.Push(_names[current]);
        }

        return string.Concat(_root, string.Join(Path.DirectorySeparatorChar, names));
    }

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
            _knownTotals ??= Enumerable.Repeat(-1L, _parent.Count).ToList();
            _knownTotals[node] = knownTotalBytes;
        }

        return node;
    }

    /// <summary>Adds a folder below a known parent node (used by the directory walker).</summary>
    public int AddChildFolder(int parentNode, string name)
    {
        return GetOrAddChild(parentNode, name);
    }

    /// <summary>Appends the children returned by a single directory listing, preserving their names exactly.</summary>
    internal int AppendEnumeratedChildren(int parentNode, List<string> names)
    {
        var first = _parent.Count;
        if (names.Count < 8)
        {
            foreach (var name in names)
            {
                AppendChild(parentNode, name);
            }

            return first;
        }

        var count = checked(first + names.Count);
        ExtendColumn(_parent, count, parentNode);
        _names.AddRange(names);
        ExtendColumn(_ownBytes, count, 0L);
        ExtendColumn(_ownFiles, count, 0L);
        if (_knownTotals is not null)
        {
            ExtendColumn(_knownTotals, count, -1L);
        }

        if (_ownAllocatedBytes is not null)
        {
            ExtendColumn(_ownAllocatedBytes, count, 0L);
        }

        return first;
    }

    private static void ExtendColumn<T>(List<T> column, int count, T value)
    {
        var first = column.Count;
        CollectionsMarshal.SetCount(column, count);
        CollectionsMarshal.AsSpan(column)[first..].Fill(value);
    }

    public void AddFilesToFolder(int node, long bytes, long files, long allocatedBytes = 0)
    {
        _ownBytes[node] += Math.Max(0, bytes);
        _ownFiles[node] += files;
        if (_ownAllocatedBytes is not null)
        {
            _ownAllocatedBytes[node] += allocatedBytes;
        }

        FilesAdded += files;
    }

    internal void MarkIncompleteDirectory(int node)
    {
        (_incompleteDirectories ??= []).Add(node);
    }

    internal void MarkIncompleteAllocation(int node)
    {
        (_incompleteAllocationDirectories ??= []).Add(node);
    }

    public FolderTree Build(string engineName, TimeSpan elapsed)
    {
        var count = _parent.Count;
        var parent = _parent.ToArray();
        var totals = _ownBytes.ToArray();
        var files = _ownFiles.ToArray();
        var allocations = _ownAllocatedBytes?.ToArray();
        var knownTotals = _knownTotals;

        // Children always get a higher index than their parent, so one reverse pass rolls
        // every folder up into its parent.
        for (var i = count - 1; i > 0; i--)
        {
            var parentNode = parent[i];
            if (knownTotals is null)
            {
                totals[parentNode] += totals[i];
            }
            else
            {
                var knownSize = knownTotals[i];
                if (knownSize >= 0)
                {
                    totals[i] = knownSize;
                }

                if (knownTotals[parentNode] < 0)
                {
                    totals[parentNode] += totals[i];
                }
            }

            files[parentNode] += files[i];
            if (allocations is not null)
            {
                allocations[parentNode] += allocations[i];
            }
        }

        if (knownTotals is not null && knownTotals[0] >= 0)
        {
            totals[0] = knownTotals[0];
        }

        return new FolderTree(_root, parent, _names.ToArray(), totals, files, engineName, elapsed, allocations,
            incompleteSubtrees: BuildIncompleteSubtrees(parent, _incompleteDirectories),
            incompleteDirectoryCount: _incompleteDirectories?.Count ?? 0,
            incompleteAllocationSubtrees: BuildIncompleteSubtrees(parent, _incompleteAllocationDirectories),
            incompleteAllocationDirectoryCount: _incompleteAllocationDirectories?.Count ?? 0);
    }

    private static BitArray? BuildIncompleteSubtrees(int[] parent, IEnumerable<int>? directories)
    {
        if (directories is null)
        {
            return null;
        }

        var incomplete = new BitArray(parent.Length);
        foreach (var node in directories)
        {
            incomplete[node] = true;
        }

        for (var node = parent.Length - 1; node > 0; node--)
        {
            if (incomplete[node])
            {
                incomplete[parent[node]] = true;
            }
        }

        return incomplete;
    }

    private int EnsureFolder(string fullPath)
    {
        var position = FolderTree.GetRelativeStart(_root, fullPath);
        if (position < 0)
        {
            return -1;
        }

        var node = 0;
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

        var node = AppendChild(parentNode, name);
        _lookup[(parentNode, name)] = node;
        return node;
    }

    private int AppendChild(int parentNode, string name)
    {
        var node = _parent.Count;
        _parent.Add(parentNode);
        _names.Add(name);
        _ownBytes.Add(0);
        _ownFiles.Add(0);
        _knownTotals?.Add(-1);
        _ownAllocatedBytes?.Add(0);
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
