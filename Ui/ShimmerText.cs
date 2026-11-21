using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MapMatcher.Ui;

/// <summary>
/// Text with a light band sweeping across it: the loading state. A style
/// animates <see cref="Phase"/> from -1 to 1; the band follows it.
/// </summary>
public sealed class ShimmerText : TextBlock
{
    public static readonly StyledProperty<double> PhaseProperty =
        AvaloniaProperty.Register<ShimmerText, double>(nameof(Phase), -1);

    public static readonly StyledProperty<Color> BaseColorProperty =
        AvaloniaProperty.Register<ShimmerText, Color>(nameof(BaseColor), Colors.Gray);

    public static readonly StyledProperty<Color> GlowColorProperty =
        AvaloniaProperty.Register<ShimmerText, Color>(nameof(GlowColor), Colors.White);

    private readonly LinearGradientBrush _brush = new();

    public ShimmerText()
    {
        _brush.GradientStops.Add(new GradientStop(Colors.Gray, 0));
        _brush.GradientStops.Add(new GradientStop(Colors.White, 0.5));
        _brush.GradientStops.Add(new GradientStop(Colors.Gray, 1));
        Foreground = _brush;
        Update();
    }

    public double Phase
    {
        get => GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    public Color BaseColor
    {
        get => GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    public Color GlowColor
    {
        get => GetValue(GlowColorProperty);
        set => SetValue(GlowColorProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PhaseProperty || change.Property == BaseColorProperty || change.Property == GlowColorProperty)
            Update();
    }

    private void Update()
    {
        // the gradient spans one text width and slides by Phase widths; outside it, the base colour
        _brush.StartPoint = new RelativePoint(Phase, 0, RelativeUnit.Relative);
        _brush.EndPoint = new RelativePoint(Phase + 1, 0, RelativeUnit.Relative);
        _brush.GradientStops[0].Color = BaseColor;
        _brush.GradientStops[1].Color = GlowColor;
        _brush.GradientStops[2].Color = BaseColor;
        InvalidateVisual();
    }
}
