using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace DevTools.Infrastructure;

/// <summary>
/// Minimal time-series chart: a filled area plus a stroked line, oldest sample on the left. Built as a
/// <see cref="Grid"/> subclass with shapes rather than a templated control so it stays bindable from XAML
/// without dragging a charting package into the app.
/// </summary>
public sealed partial class SparklineChart : Grid
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values),
        typeof(IReadOnlyList<double>),
        typeof(SparklineChart),
        new PropertyMetadata(null, OnRenderPropertyChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(SparklineChart),
        new PropertyMetadata(100d, OnRenderPropertyChanged));

    public static readonly DependencyProperty AutoScaleToRangeProperty = DependencyProperty.Register(
        nameof(AutoScaleToRange),
        typeof(bool),
        typeof(SparklineChart),
        new PropertyMetadata(false, OnRenderPropertyChanged));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush),
        typeof(Brush),
        typeof(SparklineChart),
        new PropertyMetadata(null, OnBrushChanged));

    public static readonly DependencyProperty AreaBrushProperty = DependencyProperty.Register(
        nameof(AreaBrush),
        typeof(Brush),
        typeof(SparklineChart),
        new PropertyMetadata(null, OnBrushChanged));

    private readonly Polygon _area;
    private readonly Polyline _line;

    public SparklineChart()
    {
        _area = new Polygon
        {
            StrokeThickness = 0,
            IsHitTestVisible = false
        };

        _line = new Polyline
        {
            StrokeThickness = 1.6,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };

        Children.Add(_area);
        Children.Add(_line);

        SizeChanged += (_, _) => Render();
    }

    /// <summary>Samples in chronological order. Replaced wholesale on each tick.</summary>
    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>Top of the vertical scale. Series that exceed it rescale to their own peak.</summary>
    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>
    /// Scales between the series' own minimum and maximum instead of from zero. Needed for values that sit
    /// far from zero and move only slightly - free disk space would otherwise render as a solid block and
    /// hide the very trend worth watching.
    /// </summary>
    public bool AutoScaleToRange
    {
        get => (bool)GetValue(AutoScaleToRangeProperty);
        set => SetValue(AutoScaleToRangeProperty, value);
    }

    public Brush? LineBrush
    {
        get => (Brush?)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public Brush? AreaBrush
    {
        get => (Brush?)GetValue(AreaBrushProperty);
        set => SetValue(AreaBrushProperty, value);
    }

    private static void OnRenderPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        (sender as SparklineChart)?.Render();
    }

    private static void OnBrushChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not SparklineChart chart)
        {
            return;
        }

        chart._line.Stroke = chart.LineBrush;
        chart._area.Fill = chart.AreaBrush;
    }

    private void Render()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var values = Values;

        if (width <= 1 || height <= 1 || values is null || values.Count == 0)
        {
            _line.Points.Clear();
            _area.Points.Clear();
            return;
        }

        var peak = double.MinValue;
        var trough = double.MaxValue;
        for (var i = 0; i < values.Count; i++)
        {
            peak = Math.Max(peak, values[i]);
            trough = Math.Min(trough, values[i]);
        }

        double floor;
        double scale;

        if (AutoScaleToRange)
        {
            // Pad the range so a dead-flat series lands mid-height rather than on an edge.
            var span = peak - trough;
            var padding = span > 0 ? span * 0.25 : Math.Max(Math.Abs(peak) * 0.01, 1);
            floor = trough - padding;
            scale = (peak + padding) - floor;
        }
        else
        {
            // Rescale when the live series overshoots the nominal maximum (disk rates have no ceiling).
            floor = 0;
            scale = Math.Max(Maximum, peak);
        }

        if (scale <= 0)
        {
            scale = 1;
        }

        // A single sample has no span to interpolate across, so pin it to the right edge.
        var step = values.Count > 1 ? width / (values.Count - 1) : width;

        var linePoints = new PointCollection();
        var areaPoints = new PointCollection
        {
            new Point(0, height)
        };

        for (var i = 0; i < values.Count; i++)
        {
            var x = values.Count > 1 ? i * step : width;
            var normalized = Math.Clamp((values[i] - floor) / scale, 0, 1);
            var y = height - (normalized * height);

            var point = new Point(x, y);
            linePoints.Add(point);
            areaPoints.Add(point);
        }

        areaPoints.Add(new Point(width, height));

        _line.Points = linePoints;
        _area.Points = areaPoints;
    }
}
