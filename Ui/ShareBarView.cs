using Avalonia;
using Avalonia.Controls;

namespace MapMatcher.Ui;

/// <summary>
/// The group-share bar: one segment per feature group, sized by how much of
/// the distance it accounts for.
/// </summary>
public sealed class ShareBarView : Grid
{
    public static readonly StyledProperty<IReadOnlyList<ShareBar>?> BarsProperty =
        AvaloniaProperty.Register<ShareBarView, IReadOnlyList<ShareBar>?>(nameof(Bars));

    public IReadOnlyList<ShareBar>? Bars
    {
        get => GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public ShareBarView()
    {
        Height = 6;
        ClipToBounds = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BarsProperty) Build();
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
            };
            ToolTip.SetTip(segment, bar.Tip);
            SetColumn(segment, i++);
            Children.Add(segment);
        }
    }
}
