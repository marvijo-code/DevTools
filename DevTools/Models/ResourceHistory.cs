namespace DevTools.Models;

/// <summary>
/// Fixed-capacity ring buffer of samples backing the "over time" charts. Capacity is bounded so a
/// monitor left open all day cannot itself become a memory problem.
/// </summary>
public sealed class ResourceHistory
{
    private readonly ResourceSample[] _samples;
    private int _nextIndex;

    public ResourceHistory(int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");
        }

        Capacity = capacity;
        _samples = new ResourceSample[capacity];
    }

    public int Capacity { get; }

    public int Count { get; private set; }

    public ResourceSample? Latest { get; private set; }

    public void Add(ResourceSample sample)
    {
        _samples[_nextIndex] = sample;
        _nextIndex = (_nextIndex + 1) % Capacity;
        Count = Math.Min(Count + 1, Capacity);
        Latest = sample;
    }

    public void Clear()
    {
        Array.Clear(_samples);
        _nextIndex = 0;
        Count = 0;
        Latest = null;
    }

    /// <summary>Samples in chronological order, oldest first.</summary>
    public IReadOnlyList<ResourceSample> Snapshot()
    {
        var ordered = new ResourceSample[Count];
        var start = Count == Capacity ? _nextIndex : 0;

        for (var i = 0; i < Count; i++)
        {
            ordered[i] = _samples[(start + i) % Capacity];
        }

        return ordered;
    }

    /// <summary>Projects one metric across the whole window for charting, oldest first.</summary>
    public IReadOnlyList<double> Series(Func<ResourceSample, double> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var values = new double[Count];
        var start = Count == Capacity ? _nextIndex : 0;

        for (var i = 0; i < Count; i++)
        {
            values[i] = selector(_samples[(start + i) % Capacity]);
        }

        return values;
    }

    /// <summary>Mean of a metric over the most recent <paramref name="sampleCount"/> samples.</summary>
    public double Average(Func<ResourceSample, double> selector, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var window = Math.Clamp(sampleCount, 1, Count);
        if (window == 0 || Count == 0)
        {
            return 0;
        }

        var total = 0d;
        for (var i = 0; i < window; i++)
        {
            var index = ((_nextIndex - 1 - i) % Capacity + Capacity) % Capacity;
            total += selector(_samples[index]);
        }

        return total / window;
    }

    /// <summary>Peak of a metric over the most recent <paramref name="sampleCount"/> samples.</summary>
    public double Peak(Func<ResourceSample, double> selector, int sampleCount)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var window = Math.Clamp(sampleCount, 1, Count);
        if (window == 0 || Count == 0)
        {
            return 0;
        }

        var peak = double.MinValue;
        for (var i = 0; i < window; i++)
        {
            var index = ((_nextIndex - 1 - i) % Capacity + Capacity) % Capacity;
            peak = Math.Max(peak, selector(_samples[index]));
        }

        return peak;
    }
}
