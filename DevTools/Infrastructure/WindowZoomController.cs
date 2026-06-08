using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DevTools.Infrastructure;

public sealed class WindowZoomController
{
    private const double MinScale = 0.75;
    private const double MaxScale = 2.0;
    private const double Step = 0.1;

    private readonly FrameworkElement _target;
    private double _scale = 1.0;

    public WindowZoomController(FrameworkElement target)
    {
        _target = target;
    }

    public double Scale => _scale;

    public void Increase()
    {
        Apply(_scale + Step);
    }

    public void Decrease()
    {
        Apply(_scale - Step);
    }

    private void Apply(double scale)
    {
        _scale = Math.Clamp(scale, MinScale, MaxScale);
        _target.RenderTransform = new ScaleTransform
        {
            ScaleX = _scale,
            ScaleY = _scale
        };
        _target.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
    }
}
