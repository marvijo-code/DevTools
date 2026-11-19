namespace DevTools.Models;

/// <summary>
/// Fixed-capacity ring buffer of samples backing the "over time" charts. Capacity is bounded so a
/// monitor left open all day cannot itself become a memory problem.
/// </summary>
public sealed class ResourceHistory
{
    private ResourceSample[]? _samples;
    private int _nextIndex;

    public ResourceHistory(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        Capacity = capacity;
    }

    public int Capacity { get; }

    public int Count { get; private set; }

    public ResourceSample? Latest { get; private set; }

    internal ResourceSample? Oldest => Count == 0 ? null : _samples![Count == Capacity ? _nextIndex : 0];

    public void Add(ResourceSample sample)
    {
        var samples = _samples ??= new ResourceSample[Capacity];
        samples[_nextIndex] = sample;
        _nextIndex = (_nextIndex + 1) % Capacity;
        Count = Math.Min(Count + 1, Capacity);
        Latest = sample;
    }

    public void Clear()
    {
        if (_samples is { } samples)
        {
            Array.Clear(samples);
        }

        _nextIndex = 0;
        Count = 0;
        Latest = null;
    }

    /// <summary>Samples in chronological order, oldest first.</summary>
    public IReadOnlyList<ResourceSample> Snapshot()
    {
        if (Count == 0)
        {
            return [];
        }

        var ordered = new ResourceSample[Count];
        var start = Count == Capacity ? _nextIndex : 0;

        for (var i = 0; i < Count; i++)
        {
            ordered[i] = _samples![(start + i) % Capacity];
        }

        return ordered;
    }

    /// <summary>Projects one metric across the whole window for charting, oldest first.</summary>
    public IReadOnlyList<double> Series(Func<ResourceSample, double> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        if (Count == 0)
        {
            return [];
        }

        var values = new double[Count];
        var start = Count == Capacity ? _nextIndex : 0;

        for (var i = 0; i < Count; i++)
        {
            values[i] = selector(_samples![(start + i) % Capacity]);
        }

        return values;
    }

    /// <summary>Mean of a metric over the most recent <paramref name="sampleCount"/> samples.</summary>
    public double Average(Func<ResourceSample, double> selector, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(selector);

        if (Count == 0)
        {
            return 0;
        }

        var window = Math.Clamp(sampleCount, 1, Count);

        var total = 0d;
        for (var i = 0; i < window; i++)
        {
            var index = ((_nextIndex - 1 - i) % Capacity + Capacity) % Capacity;
            total += selector(_samples![index]);
        }

        return total / window;
    }

    /// <summary>Peak of a metric over the most recent <paramref name="sampleCount"/> samples.</summary>
    public double Peak(Func<ResourceSample, double> selector, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(selector);

        if (Count == 0)
        {
            return 0;
        }

        var window = Math.Clamp(sampleCount, 1, Count);

        var peak = double.MinValue;
        for (var i = 0; i < window; i++)
        {
            var index = ((_nextIndex - 1 - i) % Capacity + Capacity) % Capacity;
            peak = Math.Max(peak, selector(_samples![index]));
        }

        return peak;
    }
}
