using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MapMatcher.Ui;

/// <summary>A stroked lucide icon drawn on its 24 px grid.</summary>
public sealed class Icon : Control
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(Geometry), typeof(Icon));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(double), typeof(Icon), new PropertyMetadata(1.75));

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Stroke
    {
        get => (double)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }
}
