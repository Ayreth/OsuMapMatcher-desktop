namespace MapMatcher.Core;

/// <summary>
/// Where the selected map comes from, with nothing else to install:
/// osu!stable is read from memory, lazer and Torii from their log plus a copy
/// of their database. tosu, when it happens to be open, takes over. Either way a map is reported only once it
/// has stayed selected for a second, so scrolling song select does not fire
/// one search per map hovered.
/// </summary>
public sealed class MapFeed : IDisposable
{
    public const int DebounceMs = 1000;
    private const int PollMs = 250;

    private readonly StableReader _stable = new();
    private readonly LazerReader _lazer = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private string _confirmed = "";
    private string _pending = "";
    private CancellationTokenSource? _debounce;
    private string _lastStatus = "";

    public MapFeed(string tosuHost)
    {
        Tosu = new TosuClient(tosuHost);
        Tosu.RawMap += map => Offer(map);
        Tosu.StatusChanged += (_, _) => Report();
    }

    public TosuClient Tosu { get; }
    public StableReader Stable => _stable;
    public LazerReader Lazer => _lazer;

    /// <summary>Settled map, raised on a thread-pool thread.</summary>
    public event Action<LiveMap>? MapChanged;
    /// <summary>(connected, text), raised on a thread-pool thread.</summary>
    public event Action<bool, string>? StatusChanged;

    public void Start()
    {
        Tosu.Start();
        _ = Task.Run(PollStableAsync);
    }

    private async Task PollStableAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            if (!Tosu.Connected)
            {
                var map = _stable.Read();
                if (map is null && !_stable.Attached) map = _lazer.Read();
                if (map is not null) Offer(map);
            }
            Report();
            try { await Task.Delay(PollMs, _stop.Token); } catch { return; }
        }
    }

    private void Report()
    {
        bool on;
        string text;
        if (Tosu.Connected) { on = true; text = "via tosu"; }
        else if (_stable.Attached) { on = true; text = "osu!stable connected"; }
        else if (_lazer.Attached) { on = true; text = _lazer.State; }
        // the game is there but the OS keeps it closed: waiting will not help, say so
        else if (_stable.Blocked) { on = false; text = _stable.State; }
        else if (OperatingSystem.IsMacOS()) { on = false; text = "lazer or Torii not running · " + StableReader.MacOsState; }
        else { on = false; text = "osu!, lazer or Torii not running"; }
        var key = $"{on}|{text}";
        if (key == _lastStatus) return;
        _lastStatus = key;
        StatusChanged?.Invoke(on, text);
    }

    private void Offer(LiveMap map)
    {
        lock (_gate)
        {
            var key = map.Key;
            // comparing with the confirmed map only is not enough: every
            // identical message would re-arm the timer and it would never fire
            if (key == _confirmed || key == _pending) return;
            _pending = key;
            _debounce?.Cancel();
            var cts = _debounce = new CancellationTokenSource();
            _ = Task.Delay(DebounceMs, cts.Token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                lock (_gate)
                {
                    if (_pending != key) return;
                    _confirmed = key;
                    _pending = "";
                }
                MapChanged?.Invoke(map);
            }, TaskScheduler.Default);
        }
    }

    public void SetTosuHost(string host) => Tosu.SetHost(host);

    public void Dispose()
    {
        _stop.Cancel();
        Tosu.Dispose();
        _stable.Dispose();
    }
}
