using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using MapMatcher.Core;
using MapMatcher.Ui;
using Match = MapMatcher.Core.Match;

namespace MapMatcher;

public partial class MainWindow : Window
{
    /* The steer axes shown here. The API opens fifteen, but fifteen sliders
       are an interface nobody opens: these six are the ones a player has an
       immediate feel for. The WORDS come from /meta, so a "denser" slider and a
       "denser" explanation under a result mean the same thing. */
    private static readonly (string Axis, string Label, string Help)[] SteerRows =
    [
        ("object_density", "Density", "How many objects per second the map throws at you."),
        ("dt_frac_lt100", "Streams", "How much of the map is streamed rather than spaced out."),
        ("sp_p50", "Jump size", "Typical distance between objects, relative to circle size."),
        ("vel_p50", "Cursor speed", "How fast the cursor has to travel to keep up."),
        ("aim_speed_ratio", "Aim vs tapping", "Which of the two the map leans on."),
        ("drain_time_s", "Length", "How long the map is."),
    ];
    // 0.1 is barely noticeable, 0.3 is where the engine calls a gap worth saying,
    // 0.5 is the last step where ordinary maps are still found
    private static readonly (double Limit, string Word)[] SteerSteps = [(0.2, "slightly "), (0.4, ""), (double.PositiveInfinity, "much ")];
    private const double SteerMax = 0.5;

    private readonly Settings _settings;
    private readonly bool _online;
    private readonly ApiClient _api = new();
    private readonly MapFeed _feed;
    private readonly ObservableCollection<SliderRow> _weights = [];
    private readonly ObservableCollection<SliderRow> _filters = [];
    private readonly ObservableCollection<SliderRow> _steer = [];
    private readonly ObservableCollection<MatchVm> _matches = [];
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _saveTimer;
    private Dictionary<string, SteerAxis> _steerWords = [];

    private LiveMap? _live;
    private PinnedMap? _pinned;
    private QueryBlock? _lastQuery;
    private string _lastQueryKey = "";
    private string _heroImageKey = "";
    private int _searchToken;
    private CancellationTokenSource? _searchCts;
    private bool _ready;

    public MainWindow() : this(Settings.Load(), online: true) { }

    /// <summary>
    /// <paramref name="online"/> false: no game, no network, nothing saved. For
    /// rendering the window from made-up data (tests, screenshots).
    /// </summary>
    internal MainWindow(Settings settings, bool online)
    {
        _settings = settings;
        _online = online;
        InitializeComponent();
        Icon = new WindowIcon(AssetLoader.Open(new Uri(OperatingSystem.IsWindows()
            ? "avares://MapMatcher/Assets/app.ico" : "avares://MapMatcher/Assets/icon-256.png")));
        SetUpChrome();

        _feed = new MapFeed(_settings.TosuHost);
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _ = SearchAsync(); };
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };

        BuildSettings();
        RestorePlacement();

        ResultsList.ItemsSource = _matches;
        WeightsList.ItemsSource = _weights;
        FiltersList.ItemsSource = _filters;
        SteerList.ItemsSource = _steer;

        Opened += OnLoaded;
        Closing += (_, _) =>
        {
            SaveSettings();
            _feed.Dispose();
        };
        AddHandler(DragDrop.DragEnterEvent, OnDragEnter);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => DropOverlay.IsVisible = false);
        AddHandler(DragDrop.DropEvent, OnDrop);
        // tunnel: seen before a focused control can swallow it, like WPF's PreviewKeyDown
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    // ================================================================ boot

    private static string PasteKey => OperatingSystem.IsMacOS() ? "Cmd+V" : "Ctrl+V";

    private static string NoMapText =>
        $"Open osu!, lazer or Torii and pick a map in song select. You can also drop a .osu file on this window, or paste a beatmap link with {PasteKey}.";

    private async void OnLoaded(object? sender, EventArgs e)
    {
        _ready = true;
        SyncSummaries();
        RenderHeader();
        ShowEmpty("No map yet", NoMapText);
        if (!_online) return;

        _feed.StatusChanged += (on, text) => Dispatcher.UIThread.Post(() => SetStatus(on, text));
        _feed.MapChanged += map => Dispatcher.UIThread.Post(() => OnLiveMap(map));
        _feed.Start();

        await LoadMetaAsync();
        _ = CheckForUpdateAsync();

        // "Open with" a .osu, or a link passed on the command line
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length > 0) Lookup(string.Join(" ", args));
    }

    private async Task LoadMetaAsync()
    {
        EngineText.Text = "finding a server";
        var meta = await _api.PickAsync(_settings.ApiOverride);
        EngineText.Text = _api.IsLocal ? "local · " + ApiClient.Pretty(_api.BaseUrl) : ApiClient.Pretty(_api.BaseUrl);
        if (meta is not null && meta.Total > 0) EngineText.Text += $" · {Fmt.Count(meta.Total)} maps";
        BuildSteer(meta);
        SyncSummaries();
        _lastQueryKey = "";
        if (ActiveSource()) await SearchAsync();
    }

    private void SetStatus(bool on, string text)
    {
        StatusDot.Fill = Res(on ? "Ok" : "Alert");
        StatusText.Text = text;
    }

    // ================================================================ settings

    private void BuildSettings()
    {
        foreach (var (key, label, brush, help) in Groups.All)
        {
            var value = _settings.Weights.GetValueOrDefault(key, 1.0);
            var row = new SliderRow(key, label, help, 0, 2, 0.1, value, 1, v => v.ToString("0.0", Fmt.Inv), Groups.BrushFor(brush));
            row.Changed += _ => SettingChanged();
            _weights.Add(row);
        }

        DivergeBox.Items.Add(new ComboBoxItem { Content = "Nothing (nearest neighbours)", Tag = "" });
        foreach (var (key, label, _, _) in Groups.All) DivergeBox.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        DivergeBox.SelectedIndex = Math.Max(0, Array.FindIndex(Groups.All, g => g.Key == _settings.Diverge) + 1);

        _filters.Add(Filter("star_range", "Star range", "Only consider maps within this many stars of the current one.",
            0.1, 3, 0.1, _settings.StarRange, 0.5, v => "±" + v.ToString("0.0", Fmt.Inv)));
        _filters.Add(Filter("length_min", "Min length", "Shortest map to allow, as a fraction of the current map's drain time.",
            0.1, 1, 0.05, _settings.LengthMin, 0.5, v => "×" + v.ToString("0.00", Fmt.Inv)));
        _filters.Add(Filter("length_max", "Max length", "Longest map to allow, as a multiple of the current map's drain time.",
            1, 5, 0.1, _settings.LengthMax, 2, v => "×" + v.ToString("0.0", Fmt.Inv)));
        _filters.Add(Filter("k", "Results", "How many recommendations to show.",
            5, 60, 5, _settings.Results, 20, v => v.ToString("0", Fmt.Inv)));

        SameSetBox.IsChecked = _settings.ExcludeSameSet;
        OnePerSongBox.IsChecked = _settings.OnePerSong;
        UnrankedBox.IsChecked = _settings.IncludeUnranked;
        TosuBox.Text = _settings.TosuHost == "127.0.0.1:24050" ? "" : _settings.TosuHost;
        ApiBox.Text = _settings.ApiOverride;
        OnTopBox.IsChecked = _settings.AlwaysOnTop;
        MatchVm.OsuTarget = _settings.OsuButton == "web" ? "web" : "game";
        OsuTargetBox.SelectedIndex = MatchVm.OsuTarget == "web" ? 1 : 0;
        Topmost = _settings.AlwaysOnTop;

        WeightsToggle.IsChecked = _settings.OpenSections.Contains("weights");
        SteerToggle.IsChecked = _settings.OpenSections.Contains("steer");
        FiltersToggle.IsChecked = _settings.OpenSections.Contains("filters");
        ConnectionToggle.IsChecked = _settings.OpenSections.Contains("connection");
    }

    private SliderRow Filter(string key, string label, string help, double min, double max, double step,
                             double value, double @default, Func<double, string> format)
    {
        var row = new SliderRow(key, label, help, min, max, step, value, @default, format);
        row.Changed += _ => SettingChanged();
        return row;
    }

    /// <summary>
    /// Built from /meta, not from a list written here: an older server does not
    /// know `steer`, and a slider it cannot serve would end in a 400.
    /// </summary>
    private void BuildSteer(Meta? meta)
    {
        var before = _steer.ToDictionary(r => r.Key, r => r.Value);
        foreach (var (k, v) in _settings.Steer) before.TryAdd(k, v);

        _steerWords = (meta?.SteerAxes ?? []).ToDictionary(a => a.Axis);
        var usable = meta is not null && meta.Supports.Contains("steer");
        _steer.Clear();
        if (usable)
        {
            foreach (var (axis, label, help) in SteerRows)
            {
                if (!_steerWords.ContainsKey(axis)) continue;
                var value = Math.Clamp(before.GetValueOrDefault(axis), -SteerMax, SteerMax);
                var row = new SliderRow(axis, label, help, -SteerMax, SteerMax, 0.1, value, 0,
                    v => SteerPhrase(axis, v) is { Length: > 0 } p ? p : "straight");
                row.Changed += _ => SettingChanged();
                _steer.Add(row);
            }
        }
        // emptied, not just hidden: a hidden slider still set would filter the list invisibly
        SteerSection.IsVisible = _steer.Count > 0;
    }

    private string SteerPhrase(string axis, double amount)
    {
        if (!_steerWords.TryGetValue(axis, out var words) || Math.Abs(amount) < 1e-9) return "";
        var grade = SteerSteps.First(s => Math.Abs(amount) <= s.Limit + 1e-9).Word;
        return grade + (amount > 0 ? words.More : words.Less);
    }

    private void SettingChanged()
    {
        if (!_ready) return;
        SyncSummaries();
        _searchTimer.Stop();
        _searchTimer.Start();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Filter_Changed(object? sender, RoutedEventArgs e) => SettingChanged();
    private void Diverge_Changed(object? sender, SelectionChangedEventArgs e) => SettingChanged();
    private string Diverge => (DivergeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    /// <summary>A folded panel must still say, at a glance, whether something is off default.</summary>
    private void SyncSummaries()
    {
        var offW = _weights.Where(r => r.IsOff).Select(r => $"{r.Label} {r.Display}").ToList();
        if (Diverge.Length > 0) offW.Add($"diverging on {Groups.Label(Diverge)}");
        Summary(WeightsSummary, offW, "default");

        Summary(SteerSummary, _steer.Where(r => r.IsOff).Select(r => SteerPhrase(r.Key, r.Value)).ToList(), "straight ahead");

        var offF = new List<string>();
        var star = _filters[0]; var lmin = _filters[1]; var lmax = _filters[2]; var k = _filters[3];
        if (star.IsOff) offF.Add($"{star.Display}★");
        if (lmin.IsOff || lmax.IsOff) offF.Add($"{lmin.Value.ToString("0.00", Fmt.Inv)}–{lmax.Value.ToString("0.0", Fmt.Inv)}× length");
        if (k.IsOff) offF.Add($"{k.Display} results");
        if (SameSetBox.IsChecked != true) offF.Add("same set kept");
        if (OnePerSongBox.IsChecked != true) offF.Add("duplicates kept");
        if (UnrankedBox.IsChecked != true) offF.Add("ranked only");
        Summary(FiltersSummary, offF, "default");
    }

    private void Summary(TextBlock block, List<string> parts, string fallback)
    {
        block.Text = parts.Count > 0 ? string.Join(", ", parts) : fallback;
        block.Foreground = Res(parts.Count > 0 ? "Ink" : "Ink3");
    }

    private IBrush? Res(string key) => this.TryFindResource(key, out var value) ? value as IBrush : null;

    private void ResetWeights_Click(object? sender, RoutedEventArgs e)
    {
        _ready = false;
        foreach (var r in _weights) r.Value = r.Default;
        DivergeBox.SelectedIndex = 0;
        _ready = true;
        SettingChanged();
    }

    private void ResetSteer_Click(object? sender, RoutedEventArgs e)
    {
        _ready = false;
        foreach (var r in _steer) r.Value = 0;
        _ready = true;
        SettingChanged();
    }

    private void ResetFilters_Click(object? sender, RoutedEventArgs e)
    {
        _ready = false;
        foreach (var r in _filters) r.Value = r.Default;
        SameSetBox.IsChecked = OnePerSongBox.IsChecked = UnrankedBox.IsChecked = true;
        _ready = true;
        SettingChanged();
    }

    private void Section_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton t || t.Tag is not string key) return;
        _settings.OpenSections.Remove(key);
        if (t.IsChecked == true) _settings.OpenSections.Add(key);
        if (_ready) { _saveTimer.Stop(); _saveTimer.Start(); }
    }

    private void OnTop_Changed(object? sender, RoutedEventArgs e)
    {
        Topmost = OnTopBox.IsChecked == true;
        _settings.AlwaysOnTop = Topmost;
        if (_ready) { _saveTimer.Stop(); _saveTimer.Start(); }
    }

    private async void ApplyConnection_Click(object? sender, RoutedEventArgs e)
    {
        _settings.TosuHost = TosuClient.Normalise(TosuBox.Text ?? "");
        _settings.ApiOverride = (ApiBox.Text ?? "").Trim();
        _feed.SetTosuHost(_settings.TosuHost);
        SaveSettings();
        await LoadMetaAsync();
    }

    private void SaveSettings()
    {
        // offline (tests, screenshots): the player's real settings file is never touched
        if (!_online) return;
        foreach (var r in _weights) _settings.Weights[r.Key] = r.Value;
        _settings.Steer = _steer.Where(r => r.IsOff).ToDictionary(r => r.Key, r => r.Value);
        _settings.Diverge = Diverge;
        _settings.StarRange = _filters[0].Value;
        _settings.LengthMin = _filters[1].Value;
        _settings.LengthMax = _filters[2].Value;
        _settings.Results = (int)_filters[3].Value;
        _settings.ExcludeSameSet = SameSetBox.IsChecked == true;
        _settings.OnePerSong = OnePerSongBox.IsChecked == true;
        _settings.IncludeUnranked = UnrankedBox.IsChecked == true;
        _settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            // kept in device-independent units, as the Windows-only versions did
            var scale = DesktopScaling > 0 ? DesktopScaling : 1;
            _settings.Width = ClientSize.Width; _settings.Height = ClientSize.Height;
            _settings.Left = Position.X / scale; _settings.Top = Position.Y / scale;
        }
        _settings.Save();
    }

    private void RestorePlacement()
    {
        if (_settings.LayoutVersion < 2)
        {
            // the first portrait layout was taller: start again from the new shape
            _settings.Width = 560; _settings.Height = 780; _settings.LayoutVersion = 2;
        }
        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);
        if (_online && _settings.Left is { } left && _settings.Top is { } top)
        {
            // only where a screen still is: a monitor may have gone since
            var scale = DesktopScaling > 0 ? DesktopScaling : 1;
            var at = new PixelPoint((int)Math.Round(left * scale), (int)Math.Round(top * scale));
            var onScreen = Screens.All.Any(sc =>
                at.X > sc.Bounds.X - 100 * scale && at.Y > sc.Bounds.Y - 20 * scale
                && at.X < sc.Bounds.Right - 100 * scale && at.Y < sc.Bounds.Bottom - 100 * scale);
            if (onScreen)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = at;
            }
        }
        if (_online && _settings.Maximized) WindowState = WindowState.Maximized;
    }

    // ================================================================ source map

    private void OnLiveMap(LiveMap map)
    {
        var changed = _live is null || _live.Checksum != map.Checksum || _live.Mods != map.Mods;
        _live = map;
        if (!changed) return;
        _pinned = null;     // picking a map in game takes precedence over a pinned one
        _lastQuery = null;
        RenderHeader();
        _ = SearchAsync();
    }

    private bool ActiveSource() => _pinned is not null || _live is not null;

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        _pinned = null;
        _lastQuery = null;
        RenderHeader();
        if (_live is null)
        {
            _matches.Clear();
            ShowEmpty("No map yet", NoMapText);
            SummaryText.Text = "";
            return;
        }
        _ = SearchAsync();
    }

    private void RenderHeader()
    {
        string artist, title, version, mapper = "";
        double stars = 0, bpm = 0, length = 0;
        string? mods = null;
        string? image = null, imageKey = null;
        var notice = "";

        if (_pinned is { } p)
        {
            BackButton.IsVisible = _live is not null;
            artist = p.Artist; title = p.Title; version = p.Version; mapper = p.Creator;
            stars = p.Stars; bpm = p.Bpm; length = p.LengthS;
            image = p.BackgroundPath ?? Cover(p.CoverUrl);
            imageKey = image;
        }
        else if (_live is { } l)
        {
            BackButton.IsVisible = false;
            artist = l.Artist; title = l.Title; version = l.Version; mapper = l.Mapper;
            stars = l.Stars; bpm = l.Bpm; length = l.LengthS;
            if (l.ModsKnown) mods = string.IsNullOrEmpty(l.Mods) ? (Math.Abs(l.Rate - 1) > 0.01 ? $"×{l.Rate.ToString("0.##", Fmt.Inv)}" : "NM") : l.Mods;
            if (l.BackgroundPath is not null && File.Exists(l.BackgroundPath)) image = l.BackgroundPath;
            else if (_feed.Tosu.Connected) image = _feed.Tosu.BackgroundUrl(l.Checksum);
            else if (l.BeatmapsetId > 0) image = $"https://assets.ppy.sh/beatmaps/{l.BeatmapsetId}/covers/cover@2x.jpg";
            imageKey = l.Checksum;
            if (!l.IsStandard) notice = "osu!standard only · this map was skipped";
        }
        else
        {
            BackButton.IsVisible = false;
            artist = "Pick a map in song select";
            title = "Waiting for a map"; version = "";
        }

        // the server's numbers win once a search has answered: they include mods
        if (_lastQuery is { } q)
        {
            if (q.Stars > 0) stars = q.Stars;
            if (q.Bpm > 0) bpm = q.Bpm;
            if (q.LengthS > 0) length = q.LengthS;
        }

        HeroArtist.Text = artist;
        HeroTitleRun.Text = title;
        // the gaps the WPF layout put between its runs
        HeroVersionRun.Text = version.Length > 0 ? $"  [{version}]" : " ";
        HeroMapperRun.Text = mapper.Length > 0 ? $"   by {mapper}" : "";

        var stats = new InlineCollection();
        HeroMods.IsVisible = false;
        if (_pinned is not null || _live is not null)
        {
            var dim = Res("Ink4");
            void Part(string value, string unit, bool gap)
            {
                if (gap) stats.Add(new Run("    "));
                stats.Add(new Run(value));
                if (unit.Length > 0) stats.Add(new Run(unit) { Foreground = dim, FontSize = 13 });
            }
            Part(stars > 0 ? Fmt.Stars(stars) : "–", "★", false);
            Part(bpm > 0 ? Fmt.Bpm(bpm) : "–", " bpm", true);
            Part(length > 0 ? Fmt.Time(length) : "–", "", true);
            if (mods is not null && mods != "NM")
            {
                HeroModsText.Text = mods;
                HeroMods.IsVisible = true;
            }
        }
        HeroStats.Inlines = stats;

        HeroNotice.IsVisible = notice.Length > 0;
        HeroNoticeText.Text = notice;
        SetHeroImage(image, imageKey);
    }

    private static string? Cover(string? listCover) =>
        listCover?.Replace("list@2x", "cover@2x").Replace("/list.", "/cover.");

    private Transitions? _heroFade;

    private async void SetHeroImage(string? source, string? key)
    {
        if (key == _heroImageKey && source is not null) return;
        _heroImageKey = key ?? "";
        // out at once, in with a fade
        _heroFade ??= HeroImage.Transitions;
        HeroImage.Transitions = null;
        HeroImage.Opacity = 0;
        HeroImage.Fill = null;
        HeroImage.Transitions = _heroFade;
        if (source is null) return;

        Bitmap? image;
        if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            image = await ImageLoader.LoadAsync(source, 1600);
        else
        {
            try { image = File.Exists(source) ? ImageLoader.Decode(await File.ReadAllBytesAsync(source), 1600) : null; }
            catch { image = null; }
        }
        // a newer map may have been picked while this one loaded
        if (image is null || _heroImageKey != (key ?? "")) return;
        HeroImage.Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Center };
        HeroImage.Opacity = 0.62;
    }

    // ================================================================ search

    private List<KeyValuePair<string, string>>? BuildQuery()
    {
        var q = new List<KeyValuePair<string, string>>();
        void Add(string k, string v) => q.Add(new(k, v));

        if (_pinned is { } p)
        {
            if (!string.IsNullOrEmpty(p.Checksum)) Add("checksum", p.Checksum);
            else if (p.BeatmapId > 0) Add("beatmap_id", p.BeatmapId.ToString(Fmt.Inv));
            else return null;
        }
        else if (_live is { IsStandard: true } l) Add("checksum", l.Checksum);
        else return null;

        Add("k", ((int)_filters[3].Value).ToString(Fmt.Inv));
        Add("star_range", _filters[0].Value.ToString(Fmt.Inv));
        Add("length_min", _filters[1].Value.ToString(Fmt.Inv));
        Add("length_max", _filters[2].Value.ToString(Fmt.Inv));
        Add("exclude_same_set", SameSetBox.IsChecked == true ? "true" : "false");
        Add("max_per_song", OnePerSongBox.IsChecked == true ? "1" : "0");
        Add("allow_unranked", UnrankedBox.IsChecked == true ? "true" : "false");
        if (Diverge.Length > 0) Add("diverge", Diverge);
        var steer = string.Join(",", _steer.Where(r => r.IsOff).Select(r => $"{r.Key}:{r.Value.ToString(Fmt.Inv)}"));
        if (steer.Length > 0) Add("steer", steer);
        foreach (var w in _weights) Add($"w_{w.Key}", w.Value.ToString(Fmt.Inv));
        // the mods of the game apply to whatever is searched from
        Add("mods", _live?.Mods ?? "");
        Add("rate", (_live?.Rate ?? 1).ToString(Fmt.Inv));
        return q;
    }

    private async Task SearchAsync()
    {
        if (!_ready || !_online) return;
        var query = BuildQuery();
        if (query is null)
        {
            if (_live is { IsStandard: false } && _pinned is null)
            {
                _matches.Clear();
                ShowEmpty("This map cannot be used", "Only osu!standard maps are indexed. Pick another map, or paste a link above.");
            }
            return;
        }

        var key = string.Join("&", query.Select(p => p.Key + "=" + p.Value));
        if (key == _lastQueryKey) return;   // nothing changed, no need to ask again
        _lastQueryKey = key;

        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var token = ++_searchToken;
        ShowLoading();

        // a map the server has never seen: send its .osu, from disk or from tosu
        Func<CancellationToken, Task<byte[]?>>? fallback = null;
        var osuPath = _pinned?.OsuPath ?? (_pinned is null ? _live?.OsuPath : null);
        if (osuPath is not null)
            fallback = async ct => File.Exists(osuPath) ? await File.ReadAllBytesAsync(osuPath, ct) : null;
        else if (_pinned is null && _live is not null && _feed.Tosu.Connected)
            fallback = ct => _feed.Tosu.GetBeatmapFileAsync(_api.Http, ct);

        SearchResult result;
        try
        {
            result = await _api.SimilarAsync(query, fallback, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (token != _searchToken) return;   // superseded

        if (result.Data is null)
        {
            _lastQueryKey = "";   // allow the same question again
            _matches.Clear();
            ShowEmpty("No answer", result.Error ?? "Search failed.");
            if (result.Error?.StartsWith("Cannot reach") == true) _ = LoadMetaAsync();
            return;
        }
        Render(result.Data);
    }

    private void Render(SimilarResponse data)
    {
        _lastQuery = data.Query;
        // a pasted link knows only a number until the server names it
        if (_pinned is { Origin: "link" } p && p.Title.Length == 0) _pinned = FromLabel(p, data.Query);
        RenderHeader();

        var bits = new List<string>();
        if (!string.IsNullOrEmpty(data.Diverge)) bits.Add($"diverging on {Groups.Label(data.Diverge)}");
        // said HERE too: the panel folds, and a list filtered by something invisible is a trap
        foreach (var (axis, amount) in data.Steer ?? []) bits.Add(SteerPhrase(axis, amount) is { Length: > 0 } s ? s : axis);
        if (!string.IsNullOrEmpty(data.Variant) && data.Variant != "nomod") bits.Add($"{data.Variant} index");
        if (!data.Query.InCorpus) bits.Add("map not indexed, analysed from its file");
        SummaryText.Text = string.Join(" · ", bits);
        SummaryText.IsVisible = bits.Count > 0;

        _matches.Clear();
        foreach (var m in data.Matches) _matches.Add(new MatchVm(m));
        ResultsScroll.ScrollToHome();
        HideLoading();

        if (data.Matches.Count == 0)
        {
            var asked = (data.Steer ?? []).Select(kv => SteerPhrase(kv.Key, kv.Value)).Where(s => s.Length > 0).ToList();
            if (asked.Count > 0)
                ShowEmpty("Nothing goes that far",
                    $"Nothing indexed is {string.Join(", ", asked)} and still resembles this map. Ease the Steer sliders.");
            else
                ShowEmpty("No candidate left", "Widen the star range or the length filter.");
        }
        else
        {
            EmptyPanel.IsVisible = false;
            ResultsScroll.IsVisible = true;
        }
    }

    /// <summary>Tests and screenshots: a map and an answer, without a game or a server.</summary>
    internal void ShowPreview(LiveMap map, SimilarResponse data, Meta? meta, string status)
    {
        BuildSteer(meta);
        SyncSummaries();
        _live = map;
        _pinned = null;
        Render(data);
        SetStatus(true, status);
    }

    internal IReadOnlyList<MatchVm> Matches => _matches;
    internal IReadOnlyList<SliderRow> SteerSliders => _steer;

    private static PinnedMap FromLabel(PinnedMap p, QueryBlock q)
    {
        // "Artist - Title [Version]"
        var label = q.Label ?? "";
        string artist = "", title = label, version = "";
        var open = label.LastIndexOf(" [", StringComparison.Ordinal);
        if (open > 0 && label.EndsWith(']')) { version = label[(open + 2)..^1]; title = label[..open]; }
        var dash = title.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) { artist = title[..dash]; title = title[(dash + 3)..]; }
        return p with { Artist = artist, Title = title, Version = version, Checksum = q.Checksum ?? p.Checksum };
    }

    private void ShowLoading()
    {
        EmptyPanel.IsVisible = false;
        LoadingPanel.IsVisible = true;
        ResultsScroll.Opacity = 0.35;
        ShimmerText.Classes.Set("running", true);
        TrackBar.Classes.Set("running", true);
    }

    private void HideLoading()
    {
        ShimmerText.Classes.Set("running", false);
        TrackBar.Classes.Set("running", false);
        LoadingPanel.IsVisible = false;
        ResultsScroll.Opacity = 1;
    }

    private void ShowEmpty(string title, string text)
    {
        HideLoading();
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        EmptyPanel.IsVisible = true;
        ResultsScroll.IsVisible = _matches.Count > 0;
    }

    // ================================================================ rows

    private void Row_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (sender is Control { DataContext: MatchVm vm } row)
        {
            vm.IsOpen = !vm.IsOpen;
            row.Focus();
        }
    }

    private void Row_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || e.Source != sender) return;
        if (sender is Control { DataContext: MatchVm vm }) vm.IsOpen = !vm.IsOpen;
        e.Handled = true;
    }

    private static Match? MatchOf(object? sender) => (sender as Control)?.DataContext is MatchVm vm ? vm.Model : null;

    private void Osu_Click(object? sender, RoutedEventArgs e)
    {
        if (MatchOf(sender) is { } m) Open(MatchVm.OsuTarget == "web" ? m.UrlWeb : m.UrlDirect);
    }

    private void OsuTarget_Changed(object? sender, SelectionChangedEventArgs e)
    {
        MatchVm.OsuTarget = (OsuTargetBox.SelectedItem as ComboBoxItem)?.Tag as string == "web" ? "web" : "game";
        _settings.OsuButton = MatchVm.OsuTarget;
        foreach (var vm in _matches) vm.RefreshOsuHelp();
        if (_ready) { _saveTimer.Stop(); _saveTimer.Start(); }
    }

    private void Pivot_Click(object? sender, RoutedEventArgs e)
    {
        if (MatchOf(sender) is not { } m) return;
        _pinned = new PinnedMap(m.Checksum, m.BeatmapId, m.Artist, m.Title, m.Version, m.Creator,
                                m.Stars, m.Bpm, m.LengthS, m.UrlCover) { Origin = "suggestion" };
        _lastQuery = null;
        RenderHeader();
        _ = SearchAsync();
    }

    /// <summary>osu:// goes to whichever osu! is installed, https:// to the browser, on every OS.</summary>
    private void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("osu://") && !url.StartsWith("https://")) return;
        if (!_online) { OpenedOffline?.Invoke(url); return; }
        _ = OpenAsync(url);
    }

    /// <summary>Offline (tests): what would have been opened.</summary>
    internal event Action<string>? OpenedOffline;

    private async Task OpenAsync(string url)
    {
        try
        {
            if (Launcher is { } launcher && await launcher.LaunchUriAsync(new Uri(url))) return;
        }
        catch { /* fall back below */ }
        try
        {
            if (OperatingSystem.IsLinux()) Process.Start(new ProcessStartInfo("xdg-open", url) { UseShellExecute = false });
            else if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("open", url) { UseShellExecute = false });
            else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* no handler for osu:// when osu! is not installed */ }
    }

    // ================================================================ link / id / file

    private static readonly Regex BeatmapLink = new(
        @"(?:/b/|/beatmaps/|#(?:osu|taiko|fruits|mania)/|/p/beatmap\?b=)(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Md5 = new(@"^[0-9a-f]{32}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Paste anywhere (Ctrl+V, Cmd+V on macOS): a beatmap link, id, checksum or .osu path.</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsButton.IsChecked == true)
        {
            SettingsButton.IsChecked = false;
            e.Handled = true;
            return;
        }
        if (IsPaste(e) && FocusManager?.GetFocusedElement() is not TextBox)
        {
            e.Handled = true;
            _ = PasteAsync();
        }
    }

    private bool IsPaste(KeyEventArgs e)
    {
        var gestures = Application.Current?.PlatformSettings?.HotkeyConfiguration.Paste;
        if (gestures is { Count: > 0 }) return gestures.Any(g => g.Matches(e));
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        return e.Key == Key.V && e.KeyModifiers == command;
    }

    private async Task PasteAsync()
    {
        if (Clipboard is not { } clipboard) return;
        try
        {
            // a .osu copied in the file manager comes as a file, anything else as text
            var files = await clipboard.TryGetFilesAsync();
            var file = files?.Select(f => f.TryGetLocalPath())
                             .FirstOrDefault(f => f is not null && f.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
            if (file is not null) { Lookup(file); return; }
            if (files is { Length: > 0 }) return;
            if (await clipboard.TryGetTextAsync() is { } text) Lookup(text);
        }
        catch { /* clipboard busy */ }
    }

    private void Lookup(string raw)
    {
        var text = raw.Trim().Trim('"');
        if (text.Length == 0) return;
        if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(text, UriKind.Absolute, out var uri))
            text = uri.LocalPath;

        if (File.Exists(text) && text.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)) { PinFile(text); return; }

        string? checksum = null;
        var id = 0;
        var link = BeatmapLink.Match(text);
        if (link.Success) id = int.Parse(link.Groups[1].Value, Fmt.Inv);
        else if (int.TryParse(text, out var n) && n > 0) id = n;
        else if (Md5.IsMatch(text)) checksum = text.ToLowerInvariant();
        else
        {
            ShowEmpty("Not a beatmap link",
                $"Copy a difficulty link such as osu.ppy.sh/beatmaps/129891, a beatmap id or a .osu file, then press {PasteKey} here.");
            return;
        }

        SettingsButton.IsChecked = false;
        _pinned = new PinnedMap(checksum, id, "", "", "", "", 0, 0, 0, null) { Origin = "link" };
        _lastQuery = null;
        RenderHeader();
        if (id > 0) { HeroTitleRun.Text = $"Beatmap {id}"; HeroArtist.Text = "Looking it up"; }
        FocusManager?.ClearFocus();
        _ = SearchAsync();
    }

    private void PinFile(string path)
    {
        var file = OsuFile.TryLoad(path);
        if (file is null)
        {
            ShowEmpty("Unreadable file", "This .osu file could not be read.");
            return;
        }
        if (file.Mode != 0)
        {
            ShowEmpty("This map cannot be used", "Only osu!standard maps are indexed.");
            return;
        }
        _pinned = new PinnedMap(file.Checksum, file.BeatmapId, file.Artist, file.Title, file.Version, file.Creator,
                                0, file.Bpm, file.LengthS,
                                file.BeatmapsetId > 0 ? $"https://assets.ppy.sh/beatmaps/{file.BeatmapsetId}/covers/cover@2x.jpg" : null)
        {
            Origin = "file",
            OsuPath = path,
            BackgroundPath = file.BackgroundPath is { } bg && File.Exists(bg) ? bg : null,
        };
        _lastQuery = null;
        RenderHeader();
        _ = SearchAsync();
    }

    private static string? DroppedOsu(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .FirstOrDefault(f => f is not null && f.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        var ok = DroppedOsu(e) is not null;
        e.DragEffects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.IsVisible = ok;
        e.Handled = true;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedOsu(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropOverlay.IsVisible = false;
        if (DroppedOsu(e) is { } path) PinFile(path);
    }

    // ================================================================ updates

    private string? _updateUrl;

    /// <summary>A strip at the top when a newer release is on GitHub. Checked at start, then every six hours.</summary>
    private async Task CheckForUpdateAsync()
    {
        while (true)
        {
            var update = await Updates.CheckAsync(_api.Http);
            if (update is not null && update.Version != _settings.DismissedVersion)
            {
                _updateUrl = update.Url;
                UpdateText.Text = $"MapMatcher {update.Version} is out.";
                UpdateBanner.IsVisible = true;
            }
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    private void Update_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateUrl is not null) Open(_updateUrl);
    }

    private void UpdateDismiss_Click(object? sender, RoutedEventArgs e)
    {
        UpdateBanner.IsVisible = false;
        _settings.DismissedVersion = (UpdateText.Text ?? "").Replace("MapMatcher ", "").Replace(" is out.", "");
        _saveTimer.Stop(); _saveTimer.Start();
    }

    /// <summary>For screenshots and tests: the update strip as a release would show it.</summary>
    internal void ShowUpdate(string version)
    {
        UpdateText.Text = $"MapMatcher {version} is out.";
        UpdateBanner.IsVisible = true;
    }

    // ================================================================ settings panel

    private CancellationTokenSource? _slide;

    private async void Settings_Toggled(object? sender, RoutedEventArgs e)
    {
        var open = SettingsButton.IsChecked == true;
        SettingsPanel.Width = Math.Min(400, Math.Max(320, Bounds.Width - 24));
        _slide?.Cancel();
        var cts = _slide = new CancellationTokenSource();
        var transform = (TranslateTransform)SettingsPanel.RenderTransform!;
        var from = transform.X;
        if (open)
        {
            if (!SettingsLayer.IsVisible) from = SettingsPanel.Width;
            SettingsLayer.IsVisible = true;
            await Task.WhenAll(
                Slide(SettingsPanel, from, 0, 250, cts.Token),
                Fade(SettingsScrim, SettingsScrim.Opacity < 1 && from > 0 ? 0 : SettingsScrim.Opacity, 1, 180, cts.Token));
        }
        else
        {
            await Task.WhenAll(
                Slide(SettingsPanel, from, SettingsPanel.Width, 180, cts.Token),
                Fade(SettingsScrim, SettingsScrim.Opacity, 0, 180, cts.Token));
            if (!cts.IsCancellationRequested && SettingsButton.IsChecked != true) SettingsLayer.IsVisible = false;
        }
    }

    private static Task Slide(Control target, double from, double to, int ms, CancellationToken ct) =>
        Run(target, TranslateTransform.XProperty, from, to, ms, new ExponentialEaseOut(), ct);

    private static Task Fade(Control target, double from, double to, int ms, CancellationToken ct) =>
        Run(target, OpacityProperty, from, to, ms, new LinearEasing(), ct);

    private static async Task Run(Control target, AvaloniaProperty property, double from, double to, int ms, Easing easing,
                                  CancellationToken ct)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(ms),
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(property, from) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(property, to) } },
            },
        };
        await animation.RunAsync(target, ct);
        // keep the end value once the animation lets go of it
        if (ct.IsCancellationRequested) return;
        if (property == OpacityProperty) target.Opacity = to;
        else if (target.RenderTransform is TranslateTransform t) t.X = to;
    }

    private void Scrim_PointerPressed(object? sender, PointerPressedEventArgs e) => SettingsButton.IsChecked = false;

    // ================================================================ window chrome

    /// <summary>
    /// Windows: no system title bar, ours instead (the top bar moves the window,
    /// the buttons on the right replace the system ones). Linux and macOS keep
    /// their native decorations, which their window managers handle best.
    /// </summary>
    private void SetUpChrome()
    {
        var custom = OperatingSystem.IsWindows();
        CaptionButtons.IsVisible = custom;
        if (custom)
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            ExtendClientAreaTitleBarHeightHint = -1;
        }
        else
        {
            // the paste shortcut the footer names is Cmd+V on macOS
            FooterText.Text = FooterText.Text?.Replace("Ctrl+V", PasteKey);
        }
        if (OperatingSystem.IsMacOS())
            ConnectionHelp.Text = "Nothing else has to run for lazer and Torii: they are followed through their log and a copy of their beatmap database, never written to. osu!stable cannot be read on macOS: open tosu and it is used instead.";
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // a double click maximises, as on a system title bar; the move loop would swallow a DoubleTapped
        if (e.ClickCount == 2 && CaptionButtons.IsVisible)
        {
            Maximize_Click(sender, e);
            e.Handled = true;
            return;
        }
        BeginMoveDrag(e);
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty || change.Property == OffScreenMarginProperty) OnStateChanged();
    }

    private void OnStateChanged()
    {
        if (Root is null || MaxIcon is null) return;
        // a maximised window without a system frame overhangs the screen by its border
        Root.Margin = WindowState == WindowState.Maximized ? OffScreenMargin : default;
        MaxIcon.Data = this.TryFindResource(WindowState == WindowState.Maximized ? "IconRestore" : "IconSquare", out var icon)
            ? icon as Geometry : null;
    }
}
