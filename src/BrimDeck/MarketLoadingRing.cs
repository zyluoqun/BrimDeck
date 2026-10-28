using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace BrimDeck;

internal sealed class MarketLoadingRing : Grid
{
    private readonly RotateTransform _rotation = new();
    internal bool IsLoading { get; private set; }
    internal MarketLoadingRing(double scale = 1)
    {
        Width = Height = 12 * scale; Margin = new Thickness(0, 0, 3 * scale, 0);
        Children.Add(new Path { Data = Geometry.Parse("M 6,1 A 5,5 0 1 1 1,6"), Stretch = Stretch.Uniform,
            Stroke = UI.Brush(UI.Accent), StrokeThickness = 1.5 * scale,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        RenderTransformOrigin = new Point(.5, .5); RenderTransform = _rotation;
        Visibility = Visibility.Hidden; IsHitTestVisible = false;
        Loaded += (_, _) => Animate(); Unloaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        IsVisibleChanged += (_, _) => Animate();
    }
    internal void SetLoading(bool loading)
    {
        IsLoading = loading; Visibility = loading ? Visibility.Visible : Visibility.Hidden; Animate();
    }
    private void Animate()
    {
        _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        if (IsLoading && IsLoaded && IsVisible && SystemParameters.ClientAreaAnimation)
            _rotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(800)) { RepeatBehavior = RepeatBehavior.Forever });
    }
}
