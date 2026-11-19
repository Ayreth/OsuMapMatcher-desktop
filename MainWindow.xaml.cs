using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

    private readonly Settings _settings = Settings.Load();
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
    private Storyboard? _loading;
    private bool _ready;

    public MainWindow()
    {
        InitializeComponent();

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

        Loaded += OnLoaded;
        Closing += (_, _) => { SaveSettings(); _feed.Dispose(); };
        StateChanged += (_, _) => OnStateChanged();
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ================================================================ boot

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ready = true;
        SyncSummaries();
        RenderHeader();
        ShowEmpty("No map yet", "Open osu!, lazer or Torii and pick a map in song select. You can also drop a .osu file on this window, or paste a beatmap link with Ctrl+V.");

        _feed.StatusChanged += (on, text) => Dispatcher.BeginInvoke(() => SetStatus(on, text));
        _feed.MapChanged += map => Dispatcher.BeginInvoke(() => OnLiveMap(map));
        _feed.Start();

        await LoadMetaAsync();

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
        StatusDot.Fill = (Brush)FindResource(on ? "Ok" : "Alert");
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
        SteerSection.Visibility = _steer.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

    private void Filter_Changed(object sender, RoutedEventArgs e) => SettingChanged();
    private void Diverge_Changed(object sender, SelectionChangedEventArgs e) => SettingChanged();
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
        block.Foreground = (Brush)FindResource(parts.Count > 0 ? "Ink" : "Ink3");
    }

    private void ResetWeights_Click(object sender, RoutedEventArgs e)
    {
        _ready = false;
        foreach (var r in _weights) r.Value = r.Default;
        DivergeBox.SelectedIndex = 0;
        _ready = true;
        SettingChanged();
    }

    private void ResetSteer_Click(object sender, RoutedEventArgs e)
    {
        _ready = false;
        foreach (var r in _steer) r.Value = 0;
        _ready = true;
        SettingChanged();
    }

    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        _ready = false;
        foreach (var r in _filters) r.Value = r.Default;
        SameSetBox.IsChecked = OnePerSongBox.IsChecked = UnrankedBox.IsChecked = true;
        _ready = true;
        SettingChanged();
    }

    private void Section_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton t || t.Tag is not string key) return;
        _settings.OpenSections.Remove(key);
        if (t.IsChecked == true) _settings.OpenSections.Add(key);
        if (_ready) { _saveTimer.Stop(); _saveTimer.Start(); }
    }

    private void OnTop_Changed(object sender, RoutedEventArgs e)
    {
        Topmost = OnTopBox.IsChecked == true;
        _settings.AlwaysOnTop = Topmost;
        if (_ready) { _saveTimer.Stop(); _saveTimer.Start(); }
    }

    private async void ApplyConnection_Click(object sender, RoutedEventArgs e)
    {
        _settings.TosuHost = TosuClient.Normalise(TosuBox.Text);
        _settings.ApiOverride = ApiBox.Text.Trim();
        _feed.SetTosuHost(_settings.TosuHost);
        SaveSettings();
        await LoadMetaAsync();
    }

    private void SaveSettings()
    {
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
            _settings.Width = Width; _settings.Height = Height;
            _settings.Left = Left; _settings.Top = Top;
        }
        _settings.Save();
    }

    private void RestorePlacement()
    {
        Width = Math.Max(MinWidth, _settings.Width);
        Height = Math.Max(MinHeight, _settings.Height);
        if (_settings.Left is { } left && _settings.Top is { } top
            && left > SystemParameters.VirtualScreenLeft - 100
            && top > SystemParameters.VirtualScreenTop - 20
            && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
            && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left; Top = top;
        }
        if (_settings.Maximized) WindowState = WindowState.Maximized;
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

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _pinned = null;
        _lastQuery = null;
        RenderHeader();
        if (_live is null)
        {
            _matches.Clear();
            ShowEmpty("No map yet", "Open osu!, lazer or Torii and pick a map in song select. You can also drop a .osu file on this window, or paste a beatmap link with Ctrl+V.");
            CountText.Text = "";
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
            BackButton.Visibility = _live is not null ? Visibility.Visible : Visibility.Collapsed;
            artist = p.Artist; title = p.Title; version = p.Version; mapper = p.Creator;
            stars = p.Stars; bpm = p.Bpm; length = p.LengthS;
            image = p.BackgroundPath ?? Cover(p.CoverUrl);
            imageKey = image;
        }
        else if (_live is { } l)
        {
            BackButton.Visibility = Visibility.Collapsed;
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
            BackButton.Visibility = Visibility.Collapsed;
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
        HeroVersionRun.Text = version.Length > 0 ? $" [{version}]" : "";

        HeroStats.Children.Clear();
        if (_pinned is not null || _live is not null)
        {
            if (mapper.Length > 0) AddStat("Mapper", mapper, sans: true);
            if (mods is not null) AddStat("Mods", mods);
            AddStat("Stars", stars > 0 ? Fmt.Stars(stars) : "–", stars > 0 ? "★" : null);
            AddStat("BPM", bpm > 0 ? Fmt.Bpm(bpm) : "–");
            AddStat("Length", length > 0 ? Fmt.Time(length) : "–");
        }

        HeroNotice.Visibility = notice.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeroNoticeText.Text = notice;
        SetHeroImage(image, imageKey);
    }

    private static string? Cover(string? listCover) =>
        listCover?.Replace("list@2x", "cover@2x").Replace("/list.", "/cover.");

    private void AddStat(string label, string value, string? unit = null, bool sans = false)
    {
        var block = new StackPanel { Margin = new Thickness(0, 0, 30, 8) };
        block.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("Label"), Margin = new Thickness(0, 0, 0, 6) });
        var v = new TextBlock { Style = (Style)FindResource("MonoValue"), FontSize = 15 };
        if (sans) { v.FontFamily = (FontFamily)FindResource("Sans"); v.FontWeight = FontWeights.Medium; }
        v.Inlines.Add(new System.Windows.Documents.Run(value));
        if (unit is not null)
            v.Inlines.Add(new System.Windows.Documents.Run(unit) { Foreground = (Brush)FindResource("Ink4"), FontSize = 13 });
        block.Children.Add(v);
        HeroStats.Children.Add(block);
    }

    private async void SetHeroImage(string? source, string? key)
    {
        if (key == _heroImageKey && source is not null) return;
        _heroImageKey = key ?? "";
        HeroImage.BeginAnimation(OpacityProperty, null);
        HeroImage.Opacity = 0;
        HeroBrush.ImageSource = null;
        if (source is null) return;

        ImageSource? image;
        if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            image = await ImageLoader.LoadAsync(source, 1600);
        else
        {
            try { image = File.Exists(source) ? ImageLoader.Decode(await File.ReadAllBytesAsync(source), 1600) : null; }
            catch { image = null; }
        }
        // a newer map may have been picked while this one loaded
        if (image is null || _heroImageKey != (key ?? "")) return;
        HeroBrush.ImageSource = image;
        HeroImage.BeginAnimation(OpacityProperty, new DoubleAnimation(0.62, TimeSpan.FromMilliseconds(350)));
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
        if (!_ready) return;
        var query = BuildQuery();
        if (query is null)
        {
            if (_live is { IsStandard: false } && _pinned is null)
            {
                _matches.Clear();
                CountText.Text = "";
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
            CountText.Text = "";
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
        SummaryText.Visibility = bits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = data.Count.ToString(Fmt.Inv);

        _matches.Clear();
        foreach (var m in data.Matches) _matches.Add(new MatchVm(m));
        ResultsScroll.ScrollToTop();
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
            EmptyPanel.Visibility = Visibility.Collapsed;
            ResultsScroll.Visibility = Visibility.Visible;
        }
    }

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
        EmptyPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        ResultsScroll.Opacity = 0.35;
        if (_loading is null)
        {
            _loading = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            var shimmer = new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1400));
            Storyboard.SetTarget(shimmer, ShimmerText);
            Storyboard.SetTargetProperty(shimmer, new PropertyPath("Foreground.RelativeTransform.X"));
            var track = new DoubleAnimation(-72, 240, TimeSpan.FromMilliseconds(1100))
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTargetName(track, "TrackMove");
            Storyboard.SetTargetProperty(track, new PropertyPath(TranslateTransform.XProperty));
            _loading.Children.Add(shimmer);
            _loading.Children.Add(track);
        }
        _loading.Begin(this, true);
    }

    private void HideLoading()
    {
        _loading?.Stop(this);
        LoadingPanel.Visibility = Visibility.Collapsed;
        ResultsScroll.Opacity = 1;
    }

    private void ShowEmpty(string title, string text)
    {
        HideLoading();
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        EmptyPanel.Visibility = Visibility.Visible;
        ResultsScroll.Visibility = _matches.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================ rows

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MatchVm vm } row)
        {
            vm.IsOpen = !vm.IsOpen;
            row.Focus();
        }
    }

    private void Row_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space) || e.OriginalSource != sender) return;
        if (sender is FrameworkElement { DataContext: MatchVm vm }) vm.IsOpen = !vm.IsOpen;
        e.Handled = true;
    }

    private static Match? MatchOf(object sender) => (sender as FrameworkElement)?.DataContext is MatchVm vm ? vm.Model : null;

    private void Direct_Click(object sender, RoutedEventArgs e)
    {
        if (MatchOf(sender) is { } m) Open(m.UrlDirect);
    }

    private void Web_Click(object sender, RoutedEventArgs e)
    {
        if (MatchOf(sender) is { } m) Open(m.UrlWeb);
    }

    private void Pivot_Click(object sender, RoutedEventArgs e)
    {
        if (MatchOf(sender) is not { } m) return;
        _pinned = new PinnedMap(m.Checksum, m.BeatmapId, m.Artist, m.Title, m.Version, m.Creator,
                                m.Stars, m.Bpm, m.LengthS, m.UrlCover) { Origin = "suggestion" };
        _lastQuery = null;
        RenderHeader();
        _ = SearchAsync();
    }

    private static void Open(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("osu://") && !url.StartsWith("https://")) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no handler for osu:// when osu! is not installed */ }
    }

    // ================================================================ link / id / file

    private static readonly Regex BeatmapLink = new(
        @"(?:/b/|/beatmaps/|#(?:osu|taiko|fruits|mania)/|/p/beatmap\?b=)(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Md5 = new(@"^[0-9a-f]{32}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Ctrl+V anywhere: a beatmap link, id, checksum or .osu path.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsButton.IsChecked == true)
        {
            SettingsButton.IsChecked = false;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox)
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var file = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(f => f.EndsWith(".osu", StringComparison.OrdinalIgnoreCase));
                    if (file is not null) Lookup(file);
                }
                else if (Clipboard.ContainsText()) Lookup(Clipboard.GetText());
            }
            catch { /* clipboard busy */ }
            e.Handled = true;
        }
    }

    private void Lookup(string raw)
    {
        var text = raw.Trim().Trim('"');
        if (text.Length == 0) return;

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
                "Copy a difficulty link such as osu.ppy.sh/beatmaps/129891, a beatmap id or a .osu file, then press Ctrl+V here.");
            return;
        }

        SettingsButton.IsChecked = false;
        _pinned = new PinnedMap(checksum, id, "", "", "", "", 0, 0, 0, null) { Origin = "link" };
        _lastQuery = null;
        RenderHeader();
        if (id > 0) { HeroTitleRun.Text = $"Beatmap {id}"; HeroArtist.Text = "Looking it up"; }
        Keyboard.ClearFocus();
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
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => f.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
            : null;

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        var ok = DroppedOsu(e) is not null;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedOsu(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (DroppedOsu(e) is { } path) PinFile(path);
    }

    // ================================================================ settings panel

    private void Settings_Toggled(object sender, RoutedEventArgs e)
    {
        var open = SettingsButton.IsChecked == true;
        SettingsPanel.Width = Math.Min(400, Math.Max(320, ActualWidth - 24));
        var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        if (open)
        {
            SettingsLayer.Visibility = Visibility.Visible;
            SettingsSlide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(SettingsPanel.Width, 0, TimeSpan.FromMilliseconds(250)) { EasingFunction = ease });
            SettingsScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        else
        {
            var slide = new DoubleAnimation(0, SettingsPanel.Width, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
            slide.Completed += (_, _) => { if (SettingsButton.IsChecked != true) SettingsLayer.Visibility = Visibility.Collapsed; };
            SettingsSlide.BeginAnimation(TranslateTransform.XProperty, slide);
            SettingsScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)));
        }
    }

    private void Scrim_Click(object sender, MouseButtonEventArgs e) => SettingsButton.IsChecked = false;

    // ================================================================ window chrome

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged()
    {
        // a maximised chrome-less window overhangs the screen by the resize border
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxIcon.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "IconRestore" : "IconSquare");
    }
}
