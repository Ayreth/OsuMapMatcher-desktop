using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using MapMatcher.Core;

namespace MapMatcher.Ui;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Fmt
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Time(double seconds)
    {
        var s = (int)Math.Round(seconds);
        return $"{s / 60}:{s % 60:00}";
    }

    public static string Stars(double stars) => stars.ToString("0.00", Inv);
    public static string Bpm(double bpm) => Math.Round(bpm).ToString(Inv);
    public static string Count(long n) => n.ToString("#,0", Inv);
}

/// <summary>The five feature groups and the muted hue each one gets.</summary>
public static class Groups
{
    public static readonly (string Key, string Label, string Brush, string Help)[] All =
    [
        ("aim", "Aim", "Ghost1",
            "Cursor movement: how far and how sharply you have to move between objects, how much the aim load spikes, and how much of it sits on sliders."),
        ("speed", "Speed", "Ghost2",
            "Tapping: stream density, sustained finger speed, and how the tapping load is spread across the map rather than packed into one section."),
        ("rhythm", "Rhythm", "Ghost3",
            "Timing feel: BPM and BPM changes, the gaps between objects, and how much of the map is streamed versus spaced out."),
        ("spacing", "Spacing", "Ghost4",
            "Raw geometry: distances between objects relative to circle size, the angles they form, and the cursor velocity the patterns demand."),
        ("parameters", "Parameters", "Ghost5",
            "Map settings and shape: CS, AR, OD, HP, slider ratio, object count, length, break time and overall density."),
    ];

    public static Brush BrushFor(string resourceKey) => (Brush)Application.Current.Resources[resourceKey];

    public static string Label(string key) => All.FirstOrDefault(g => g.Key == key).Label ?? key;
}

/// <summary>A labelled slider row: a weight, a filter or a steer axis.</summary>
public sealed class SliderRow : Observable
{
    private double _value;
    private readonly Func<double, string> _format;

    public SliderRow(string key, string label, string help, double min, double max, double step,
                     double value, double @default, Func<double, string> format, Brush? swatch = null)
    {
        Key = key; Label = label; Help = help; Min = min; Max = max; Step = step;
        Default = @default; _format = format; Swatch = swatch; _value = value;
    }

    public string Key { get; }
    public string Label { get; }
    public string Help { get; }
    public double Min { get; }
    public double Max { get; }
    public double Step { get; }
    public double Default { get; }
    public Brush? Swatch { get; }
    public bool HasSwatch => Swatch is not null;

    public double Value
    {
        get => _value;
        set
        {
            value = Math.Round(value / Step) * Step;
            value = Math.Round(value, 3);
            if (Set(ref _value, value))
            {
                Raise(nameof(Display));
                Raise(nameof(IsOff));
                Changed?.Invoke(this);
            }
        }
    }

    public string Display => _format(_value);
    public bool IsOff => Math.Abs(_value - Default) > 1e-9;

    public event Action<SliderRow>? Changed;

    public void Refresh() => Raise(nameof(Display));
}

/// <summary>One suggestion in the list.</summary>
public sealed class MatchVm : Observable
{
    private bool _isOpen;

    public MatchVm(Match m)
    {
        Model = m;
        var shares = Groups.All.Select(g => (g, share: m.GroupShare.TryGetValue(g.Key, out var v) ? v : 0)).ToList();
        Bars = shares.Select(x => new ShareBar(x.g.Label, Groups.BrushFor(x.g.Brush), x.share)).ToList();
        WhyDifferent = string.Join("  ·  ", m.Differences);
        WhySame = string.Join("  ·  ", m.Similarities);
    }

    public Match Model { get; }

    public string Rank => Model.Rank.ToString(Fmt.Inv);
    public string Name => $"{Model.Artist} - {Model.Title}";
    public string Version => $"[{Model.Version}]";
    public string Creator => Model.Creator;
    public string Stars => Fmt.Stars(Model.Stars);
    public string Bpm => Fmt.Bpm(Model.Bpm);
    public string Length => Fmt.Time(Model.LengthS);
    public string MatchPct => Math.Round(100 * Model.Similarity).ToString(Fmt.Inv);
    public double MatchFill => Math.Clamp(Model.Similarity, 0, 1) * 40;
    public string Distance => Model.Distance.ToString("0.00", Fmt.Inv);
    private ImageSource? _thumb;
    public ImageSource? Thumb
    {
        get
        {
            if (_thumb is null && !_thumbAsked && !string.IsNullOrEmpty(Model.UrlCover))
            {
                _thumbAsked = true;
                _ = LoadThumbAsync(Model.UrlCover);
            }
            return _thumb;
        }
    }
    private bool _thumbAsked;

    /// <summary>"game" or "web": what the osu! button on every row does.</summary>
    public static string OsuTarget { get; set; } = "game";
    public string OsuHelp => OsuTarget == "web" ? "Open the beatmap page in the browser" : "Open it in game to download";
    public void RefreshOsuHelp() => Raise(nameof(OsuHelp));

    private async Task LoadThumbAsync(string url)
    {
        // the wide cover fits the row; the square list one is the fallback
        var wide = url.Replace("list@2x", "cover").Replace("/list.", "/cover.");
        var image = wide != url ? await ImageLoader.LoadAsync(wide, 420) : null;
        image ??= await ImageLoader.LoadAsync(url, 200);
        if (image is null) return;
        _thumb = image;
        Raise(nameof(Thumb));
    }

    // ranked is the normal case and stays quiet; the rest get a small tag
    public string? StatusTag => Model.Status switch
    {
        "loved" => "loved",
        "graveyard" => "graveyard",
        "pending" or "wip" => "pending",
        "qualified" => "qualified",
        _ => null,
    };
    public bool HasStatusTag => StatusTag is not null;
    public string StatusHelp
    {
        get
        {
            var label = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(Model.Status ?? "");
            var plays = Model.Playcount is > 0 ? $" · {Fmt.Count(Model.Playcount.Value)} plays" : "";
            return Model.IsUnranked
                ? $"{label}{plays}. Not ranked: it is in the pool because it is heavily played, and it may not import the usual way."
                : label + plays;
        }
    }

    public IReadOnlyList<ShareBar> Bars { get; }
    public string WhyDifferent { get; }
    public string WhySame { get; }
    public bool HasWhyDifferent => WhyDifferent.Length > 0;
    public bool HasWhySame => WhySame.Length > 0;
    public bool NoWhy => !HasWhyDifferent && !HasWhySame;

    public bool IsOpen
    {
        get => _isOpen;
        set => Set(ref _isOpen, value);
    }
}

public sealed record ShareBar(string Label, Brush Brush, double Share)
{
    public GridLength Width => new(Math.Max(Share, 0.0001), GridUnitType.Star);
    public string Pct => $"{Math.Round(100 * Share).ToString(Fmt.Inv)}%";
    public string Tip => $"{Label} {Pct} of the distance";
}
