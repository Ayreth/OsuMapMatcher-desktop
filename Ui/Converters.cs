using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MapMatcher.Ui;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var on = value is true;
        if (Invert) on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// The group-share bar: one segment per feature group, sized by how much of
/// the distance it accounts for.
/// </summary>
public sealed class ShareBarView : Grid
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(IReadOnlyList<ShareBar>), typeof(ShareBarView),
        new PropertyMetadata(null, (d, _) => ((ShareBarView)d).Build()));

    public IReadOnlyList<ShareBar>? Bars
    {
        get => (IReadOnlyList<ShareBar>?)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public ShareBarView()
    {
        Height = 6;
        ClipToBounds = true;
    }

    private void Build()
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        if (Bars is null) return;
        var i = 0;
        foreach (var bar in Bars)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = bar.Width });
            var segment = new Border
            {
                Background = bar.Brush,
                Margin = new Thickness(i == 0 ? 0 : 1, 0, 0, 0),
                CornerRadius = new CornerRadius(1),
                ToolTip = bar.Tip,
            };
            SetColumn(segment, i++);
            Children.Add(segment);
        }
    }
}
