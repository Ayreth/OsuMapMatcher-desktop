using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Realms;

namespace MapMatcher.Core;

/// <summary>
/// Follows osu!lazer and its forks (Torii) without touching the game's memory.
///
/// Every lazer build writes "Game-wide working beatmap updated to Artist -
/// Title (Mapper) [Diff]" to its runtime log when the selected map changes.
/// That name is looked up in a COPY of the game's client.realm (never the
/// original: nothing here may ever write to the player's database), which
/// gives the checksum, the online id, the .osu file and the background.
///
/// Mods are not logged by lazer, so they are not followed here.
/// </summary>
public sealed class LazerReader : IDisposable
{
    private const string Marker = "Game-wide working beatmap updated to ";

    private string? _logPath;
    private long _logPosition;
    private string? _lastTitle;
    private string? _dataRoot;
    private Dictionary<string, List<LazerBeatmap>> _index = new(StringComparer.Ordinal);
    private DateTime _indexedSourceTime;
    private DateTime _nextRefresh = DateTime.MinValue;
    private DateTime _nextProcessCheck = DateTime.MinValue;
    private string? _client;

    public string State { get; private set; } = "lazer not running";
    public bool Attached => _client is not null && _logPath is not null;
    public string? Client => _client;

    /// <summary>The map currently selected, or null if nothing new / no lazer.</summary>
    public LiveMap? Read()
    {
        try
        {
            if (DateTime.UtcNow >= _nextProcessCheck)
            {
                _nextProcessCheck = DateTime.UtcNow.AddSeconds(2);
                _client = FindClient();
                if (_client is null) { Reset("lazer not running"); return null; }
                var newest = NewestLog();
                if (newest is null) { Reset($"{_client} running, no log found"); return null; }
                if (newest != _logPath)
                {
                    _logPath = newest;
                    _dataRoot = Path.GetDirectoryName(Path.GetDirectoryName(newest));
                    // a fresh log: start near its end and pick the last selection made
                    var length = new FileInfo(newest).Length;
                    _logPosition = Math.Max(0, length - 4 * 1024 * 1024);
                    _lastTitle = null;
                }
            }
            if (_client is null || _logPath is null) return null;

            var title = ReadNewSelection();
            if (title is not null) _lastTitle = title;
            if (_lastTitle is null) { State = $"{_client} connected, no map yet"; return null; }
            State = $"{_client} connected";
            return Resolve(_lastTitle);
        }
        catch
        {
            return null;
        }
    }

    private void Reset(string state)
    {
        State = state;
        _logPath = null;
        _lastTitle = null;
    }

    /// <summary>torii.exe, or a 64-bit osu!.exe (the 32-bit one is osu!stable).</summary>
    private static string? FindClient()
    {
        if (Process.GetProcessesByName("torii").Length > 0) return "Torii";
        foreach (var p in Process.GetProcessesByName("osu!"))
        {
            try
            {
                if (IsWow64Process(p.Handle, out var wow64) && !wow64) return "osu!lazer";
            }
            catch { /* access denied: not ours to read anyway */ }
        }
        return null;
    }

    /// <summary>
    /// Lazer keeps logs in its data folder, which storage.ini can move
    /// elsewhere (Torii is often pointed at lazer's own folder).
    /// </summary>
    private static string? NewestLog()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "osu-torii", "osu" })
        {
            var root = Path.Combine(appData, name);
            if (!Directory.Exists(root)) continue;
            roots.Add(root);
            var ini = Path.Combine(root, "storage.ini");
            if (!File.Exists(ini)) continue;
            foreach (var line in File.ReadLines(ini))
            {
                var eq = line.IndexOf('=');
                if (eq < 0 || !line[..eq].Trim().Equals("FullPath", StringComparison.OrdinalIgnoreCase)) continue;
                var full = line[(eq + 1)..].Trim();
                if (Directory.Exists(full)) roots.Add(full);
            }
        }

        return roots
            .Select(r => Path.Combine(r, "logs"))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.runtime.log"))
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    /// <summary>The last selection logged since we last looked.</summary>
    private string? ReadNewSelection()
    {
        using var stream = new FileStream(_logPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < _logPosition) _logPosition = 0;   // rotated
        if (stream.Length == _logPosition) return null;
        stream.Position = _logPosition;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        // only consume complete lines: a half-written one is read next time
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0) return null;
        _logPosition += Encoding.UTF8.GetByteCount(text.AsSpan(0, lastNewline + 1));

        string? found = null;
        foreach (var line in text.AsSpan(0, lastNewline).EnumerateLines())
        {
            var at = line.IndexOf(Marker);
            if (at < 0) continue;
            found = line[(at + Marker.Length)..].Trim().ToString();
        }
        // the placeholder lazer uses when nothing is selected
        if (found is not null && found.StartsWith("please select or load a beatmap!", StringComparison.Ordinal)) return null;
        return found;
    }

    // ------------------------------------------------------------ realm index

    private LiveMap? Resolve(string title)
    {
        var hit = Lookup(title);
        if (hit is null && DateTime.UtcNow >= _nextRefresh)
        {
            // imported since the last copy? copy again, at most every few seconds
            _nextRefresh = DateTime.UtcNow.AddSeconds(5);
            RefreshIndex();
            hit = Lookup(title);
        }
        if (hit is null) { State = $"{_client} connected, map not found locally"; return null; }

        string? osuPath = null, bgPath = null;
        if (_dataRoot is not null)
        {
            if (hit.FileHash.Length > 2) osuPath = StorePath(hit.FileHash);
            if (hit.BackgroundHash is { Length: > 2 } bg) bgPath = StorePath(bg);
        }

        return new LiveMap(
            Checksum: hit.Md5,
            BeatmapId: Math.Max(0, hit.OnlineId),
            BeatmapsetId: Math.Max(0, hit.SetOnlineId),
            Artist: hit.Artist,
            Title: hit.Title,
            Version: hit.Version,
            Mapper: hit.Mapper,
            Mods: "",
            Rate: 1,
            Stars: hit.Stars > 0 ? hit.Stars : 0,
            Bpm: hit.Bpm,
            LengthS: hit.LengthMs / 1000,
            IsStandard: hit.Ruleset == "osu")
        {
            OsuPath = osuPath is not null && File.Exists(osuPath) ? osuPath : null,
            BackgroundPath = bgPath is not null && File.Exists(bgPath) ? bgPath : null,
            ModsKnown = false,
        };
    }

    private string StorePath(string hash) => Path.Combine(_dataRoot!, "files", hash[..1], hash[..2], hash);

    private LazerBeatmap? Lookup(string title)
    {
        if (_index.Count == 0) RefreshIndex();
        if (!_index.TryGetValue(title, out var list) || list.Count == 0) return null;
        // the same name can exist twice (re-imports, other rulesets):
        // prefer standard, then the most recently added set
        return list.OrderByDescending(b => b.Ruleset == "osu").ThenByDescending(b => b.Added).First();
    }

    private void RefreshIndex()
    {
        if (_dataRoot is null) return;
        var source = Path.Combine(_dataRoot, "client.realm");
        if (!File.Exists(source)) return;
        var sourceTime = File.GetLastWriteTimeUtc(source);
        if (_index.Count > 0 && sourceTime == _indexedSourceTime) return;

        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!MapMatcher", "lazer-copy");
        Directory.CreateDirectory(folder);
        foreach (var stale in Directory.EnumerateFileSystemEntries(folder))
        {
            try { if (Directory.Exists(stale)) Directory.Delete(stale, true); else File.Delete(stale); } catch { }
        }
        var copy = Path.Combine(folder, "client.realm");
        // the game holds the file open for writing: a plain copy would be refused
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var output = File.Create(copy))
            input.CopyTo(output);

        var index = new Dictionary<string, List<LazerBeatmap>>(StringComparer.Ordinal);
        using (var realm = OpenCopy(copy))
        {
            foreach (var b in realm.DynamicApi.All("Beatmap"))
            {
                try
                {
                    var entry = ReadBeatmap(b);
                    if (entry is null) continue;
                    if (!index.TryGetValue(entry.DisplayTitle, out var list)) index[entry.DisplayTitle] = list = [];
                    list.Add(entry);
                }
                catch { /* one odd row must not lose the rest */ }
            }
        }
        _index = index;
        _indexedSourceTime = sourceTime;
    }

    private static Realm OpenCopy(string path)
    {
        // dynamic: no schema of our own, read the one in the file
        var config = new RealmConfiguration(path) { IsDynamic = true, IsReadOnly = true };
        try
        {
            return Realm.GetInstance(config);
        }
        catch
        {
            // read-only refuses a file needing an upgrade; this is a copy, so allow it
            return Realm.GetInstance(new RealmConfiguration(path) { IsDynamic = true, SchemaVersion = ulong.MaxValue / 2 });
        }
    }

    private static LazerBeatmap? ReadBeatmap(IRealmObject b)
    {
        var api = b.DynamicApi;
        var md5 = api.Get<string>("MD5Hash");
        if (string.IsNullOrEmpty(md5)) return null;
        if (api.Get<bool>("Hidden")) { /* still selectable in some flows: keep */ }

        var metadata = api.Get<IRealmObjectBase?>("Metadata");
        var set = api.Get<IRealmObjectBase?>("BeatmapSet");
        var ruleset = api.Get<IRealmObjectBase?>("Ruleset");
        var author = metadata?.DynamicApi.Get<IRealmObjectBase?>("Author");

        var artist = metadata?.DynamicApi.Get<string>("Artist") ?? "";
        var title = metadata?.DynamicApi.Get<string>("Title") ?? "";
        var mapper = author?.DynamicApi.Get<string>("Username") ?? "";
        var version = api.Get<string>("DifficultyName") ?? "";
        var background = metadata?.DynamicApi.Get<string>("BackgroundFile") ?? "";

        string? bgHash = null;
        if (set is not null && background.Length > 0)
        {
            foreach (var usage in set.DynamicApi.GetList<IRealmObjectBase>("Files"))
            {
                if (!string.Equals(usage.DynamicApi.Get<string>("Filename"), background, StringComparison.OrdinalIgnoreCase)) continue;
                bgHash = usage.DynamicApi.Get<IRealmObjectBase?>("File")?.DynamicApi.Get<string>("Hash");
                break;
            }
        }

        return new LazerBeatmap(
            DisplayTitle: DisplayTitle(artist, title, mapper, version),
            Md5: md5,
            FileHash: api.Get<string>("Hash") ?? "",
            OnlineId: api.Get<int>("OnlineID"),
            SetOnlineId: set?.DynamicApi.Get<int>("OnlineID") ?? -1,
            Artist: artist, Title: title, Mapper: mapper, Version: version,
            Ruleset: ruleset?.DynamicApi.Get<string>("ShortName") ?? "",
            Stars: api.Get<double>("StarRating"),
            Bpm: api.Get<double>("BPM"),
            LengthMs: api.Get<double>("Length"),
            BackgroundHash: bgHash,
            Added: set?.DynamicApi.Get<DateTimeOffset>("DateAdded") ?? default);
    }

    /// <summary>Exactly what lazer prints: BeatmapInfo.GetDisplayTitle().</summary>
    public static string DisplayTitle(string artist, string title, string mapper, string version)
    {
        var a = string.IsNullOrEmpty(artist) ? "unknown artist" : artist;
        var t = string.IsNullOrEmpty(title) ? "unknown title" : title;
        var by = string.IsNullOrEmpty(mapper) ? "" : $" ({mapper})";
        var meta = $"{a} - {t}{by}".Trim();
        var diff = string.IsNullOrEmpty(version) ? "" : $"[{version}]";
        return $"{meta} {diff}".Trim();
    }

    public void Dispose() { }

    private sealed record LazerBeatmap(
        string DisplayTitle, string Md5, string FileHash, int OnlineId, int SetOnlineId,
        string Artist, string Title, string Mapper, string Version, string Ruleset,
        double Stars, double Bpm, double LengthMs, string? BackgroundHash, DateTimeOffset Added);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);
}
