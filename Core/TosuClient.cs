using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MapMatcher.Core;

/// <summary>
/// Optional source: tosu's v2 websocket, used when tosu happens to run
/// (it is the only way to follow lazer). Reconnects quietly forever.
/// </summary>
public sealed class TosuClient : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private string _host;
    private ClientWebSocket? _socket;

    public TosuClient(string host) => _host = Normalise(host);

    public string Host => _host;
    public string BaseUrl => "http://" + _host;

    /// <summary>Raised on a thread-pool thread.</summary>
    public event Action<bool, string>? StatusChanged;
    /// <summary>Every map message, undebounced: <see cref="MapFeed"/> settles them.</summary>
    public event Action<LiveMap>? RawMap;

    public bool Connected { get; private set; }

    public static string Normalise(string host)
    {
        host = (host ?? "").Trim();
        host = host.Replace("http://", "").Replace("https://", "").Replace("ws://", "").TrimEnd('/');
        return string.IsNullOrEmpty(host) ? "127.0.0.1:24050" : host;
    }

    public void Start() => _ = Task.Run(RunAsync);

    /// <summary>Point at another tosu; the current socket drops and the loop reconnects there.</summary>
    public void SetHost(string host)
    {
        var next = Normalise(host);
        if (next == _host) return;
        _host = next;
        try { _socket?.Abort(); } catch { /* reconnect handles it */ }
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            _socket = socket;
            try
            {
                Connected = false;
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                connectTimeout.CancelAfter(3000);
                await socket.ConnectAsync(new Uri($"ws://{_host}/websocket/v2"), connectTimeout.Token);
                Connected = true;
                StatusChanged?.Invoke(true, "tosu connected");
                await ReadLoopAsync(socket);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // tosu not running yet, or closed: say so and try again
            }
            var was = Connected;
            Connected = false;
            if (was) StatusChanged?.Invoke(false, "tosu lost");
            try { await Task.Delay(2000, _stop.Token); } catch { return; }
        }
    }

    private async Task ReadLoopAsync(ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !_stop.IsCancellationRequested)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            LiveMap? map;
            try { map = Parse(message.GetBuffer().AsSpan(0, (int)message.Length)); }
            catch { continue; }
            if (map is null || string.IsNullOrEmpty(map.Checksum)) continue;
            RawMap?.Invoke(map);
        }
    }

    private static LiveMap? Parse(ReadOnlySpan<byte> utf8)
    {
        using var doc = JsonDocument.Parse(utf8.ToArray());
        var root = doc.RootElement;
        if (!root.TryGetProperty("beatmap", out var b)) return null;

        string Str(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? "" : "";
        double Num(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetDouble() : 0;
        JsonElement Obj(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

        var stats = Obj(b, "stats");
        var stars = Obj(stats, "stars");
        var time = Obj(b, "time");
        var mode = Obj(b, "mode");
        var mods = Obj(Obj(root, "play"), "mods");
        var modeName = Str(mode, "name");
        var rate = Num(mods, "rate");

        return new LiveMap(
            Checksum: Str(b, "checksum"),
            BeatmapId: (int)Num(b, "id"),
            BeatmapsetId: (int)Num(b, "set"),
            Artist: Str(b, "artist"),
            Title: Str(b, "title"),
            Version: Str(b, "version"),
            Mapper: Str(b, "mapper"),
            Mods: Str(mods, "name"),
            Rate: rate == 0 ? 1 : rate,
            Stars: Num(stars, "total") is var total and > 0 ? total : Num(stars, "live"),
            Bpm: Num(Obj(stats, "bpm"), "common"),
            // approximate; the real drain time comes back with the answer
            LengthS: Math.Max(0, (Num(time, "lastObject") - Num(time, "firstObject")) / 1000),
            IsStandard: string.IsNullOrEmpty(modeName) || modeName == "osu");
    }

    /// <summary>The selected map's .osu, served by tosu, for maps the server has never seen.</summary>
    public async Task<byte[]?> GetBeatmapFileAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync($"{BaseUrl}/files/beatmap/file", ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(ct) : null;
        }
        catch (HttpRequestException) { return null; }
    }

    public string BackgroundUrl(string checksum) =>
        $"{BaseUrl}/files/beatmap/background?v={Uri.EscapeDataString(checksum)}";

    public void Dispose()
    {
        _stop.Cancel();
        try { _socket?.Abort(); } catch { }
    }
}
