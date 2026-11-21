using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MapMatcher.Ui;

/// <summary>A stroked lucide icon drawn on its 24 px grid, in the colour of the text around it.</summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> StrokeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Stroke), 1.75);

    /// <summary>Inherited like text colour, so an icon inside a button follows the button.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon()
    {
        AffectsRender<Icon>(DataProperty, StrokeProperty, ForegroundProperty);
        WidthProperty.OverrideDefaultValue<Icon>(16);
        HeightProperty.OverrideDefaultValue<Icon>(16);
        FocusableProperty.OverrideDefaultValue<Icon>(false);
    }

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Data is not { } data || Foreground is not { } brush) return;
        // uniform scale from the 24 px grid, centred, stroke scaled with it
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        if (scale <= 0) return;
        var dx = (Bounds.Width - 24 * scale) / 2;
        var dy = (Bounds.Height - 24 * scale) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy)))
        {
            var pen = new Pen(brush, Stroke, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, pen, data);
        }
    }
}
