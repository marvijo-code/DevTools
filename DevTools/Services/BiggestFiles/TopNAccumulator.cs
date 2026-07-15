namespace DevTools.Services.BiggestFiles;

/// <summary>
/// Thread-safe bounded min-heap that keeps the <c>capacity</c> largest items seen.
/// Callers should consult <see cref="Threshold"/> (a lock-free volatile read) BEFORE
/// materializing expensive item data — once the heap is full, the vast majority of
/// candidates are rejected without ever taking the lock.
/// </summary>
public sealed class TopNAccumulator<T>
{
    private readonly int _capacity;
    private readonly PriorityQueue<T, long> _heap;
    private readonly object _gate = new();

    // Smallest size currently retained once full; long.MinValue while the heap has spare capacity
    // (so every non-negative file size passes the pre-check until we fill up).
    private long _threshold = long.MinValue;

    public TopNAccumulator(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
        _heap = new PriorityQueue<T, long>(capacity);
    }

    /// <summary>
    /// The current cutoff: an item must exceed this to have any chance of being retained.
    /// Reads without locking so hot scan loops can reject cheaply.
    /// </summary>
    public long Threshold => Volatile.Read(ref _threshold);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _heap.Count;
            }
        }
    }

    /// <summary>Returns true if the item was retained.</summary>
    public bool TryAdd(long sizeBytes, T item)
    {
        if (sizeBytes <= Volatile.Read(ref _threshold))
        {
            return false;
        }

        lock (_gate)
        {
            if (_heap.Count < _capacity)
            {
                _heap.Enqueue(item, sizeBytes);
                if (_heap.Count == _capacity && _heap.TryPeek(out _, out var min))
                {
                    Volatile.Write(ref _threshold, min);
                }

                return true;
            }

            // Re-check under the lock: another thread may have raised the threshold.
            if (sizeBytes <= _threshold)
            {
                return false;
            }

            _heap.EnqueueDequeue(item, sizeBytes);
            if (_heap.TryPeek(out _, out var newMin))
            {
                Volatile.Write(ref _threshold, newMin);
            }

            return true;
        }
    }

    /// <summary>Snapshot of the retained items, largest first.</summary>
    public IReadOnlyList<(long SizeBytes, T Item)> Snapshot()
    {
        lock (_gate)
        {
            var items = new List<(long SizeBytes, T Item)>(_heap.Count);
            foreach (var (element, priority) in _heap.UnorderedItems)
            {
                items.Add((priority, element));
            }

            items.Sort((a, b) => b.SizeBytes.CompareTo(a.SizeBytes));
            return items;
        }
    }
}
