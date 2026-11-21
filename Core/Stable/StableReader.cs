using System.IO;
using System.Text;
using MapMatcher.Core.Stable;

namespace MapMatcher.Core;

/// <summary>
/// Reads the map selected in osu!stable straight from the game's memory, the
/// way tosu and gosumemory do: read-only, nothing is written, nothing is
/// injected. Patterns and offsets are the ones tosu uses (packages/tosu/src/
/// memory/stable.ts). Lazer is not covered here; see <see cref="LazerReader"/>.
///
/// The memory itself comes from <see cref="IProcessMemory"/>: kernel32 on
/// Windows, process_vm_readv on Linux (stable under Wine). macOS has no way in
/// without root, so stable is left to tosu there.
/// </summary>
public sealed class StableReader : IDisposable
{
    // beatmap: [[Base - 0xC]] -> Beatmap object
    private static readonly byte?[] BasePattern = PatternScanner.Parse("F8 01 74 04 83 65");
    // mods selected in song select: [[MenuMods + 0x9]]
    private static readonly byte?[] MenuModsPattern = PatternScanner.Parse("C8 FF ?? ?? ?? ?? ?? 81 0D ?? ?? ?? ?? ?? 08 00 00");
    // stable is a 32-bit process: everything of interest sits below 2 GB
    private const long ScanLimit = 0x7FFF_0000;

    private StableTarget? _target;
    private IProcessMemory? _memory;
    private long _base;
    private long _menuMods;
    private string? _songsFolder;
    private DateTime _nextScan = DateTime.MinValue;

    public bool Attached => _memory is not null && _base != 0;
    public string? SongsFolder => _songsFolder;

    /// <summary>
    /// The game runs but the OS will not let it be read (Linux ptrace rules):
    /// worth telling the player, since it is not a matter of waiting.
    /// </summary>
    public bool Blocked { get; private set; }

    /// <summary>What happened on the last read, for the status line.</summary>
    public string State { get; private set; } = OperatingSystem.IsMacOS() ? MacOsState : "osu! not running";

    public const string MacOsState = "osu!stable needs tosu on macOS";

    /// <summary>Read the selected map, or null if osu! is not there (yet).</summary>
    public LiveMap? Read()
    {
        try
        {
            if (!EnsureAttached()) return null;

            var beatmap = ReadPtr(ReadPtr(_base - 0xC));
            if (beatmap == 0) { State = "osu! detected, no map selected"; return null; }

            var checksum = ReadString(ReadPtr(beatmap + 0x6C));
            var fileName = ReadString(ReadPtr(beatmap + 0x90));
            if (string.IsNullOrEmpty(checksum) || !fileName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
                return null;

            var folder = ReadString(ReadPtr(beatmap + 0x78));
            var background = ReadString(ReadPtr(beatmap + 0x68));
            var mods = _menuMods != 0 ? ReadInt(ReadPtr(_menuMods)) : 0;

            string? osuPath = null, bgPath = null;
            if (_songsFolder is not null && folder.Length > 0 && _target is { } target)
            {
                osuPath = target.SongPath(_songsFolder, folder, fileName);
                if (background.Length > 0) bgPath = target.SongPath(_songsFolder, folder, background);
            }

            // metadata from the .osu itself: complete, and no more offsets to trust
            var file = osuPath is not null ? OsuFile.TryLoad(osuPath) : null;
            var (modName, rate) = Mods.Describe(mods);
            State = "reading osu!";

            return new LiveMap(
                Checksum: checksum,
                BeatmapId: file?.BeatmapId ?? ReadInt(beatmap + 0xC8),
                BeatmapsetId: file?.BeatmapsetId ?? ReadInt(beatmap + 0xCC),
                Artist: file?.Artist ?? ReadString(ReadPtr(beatmap + 0x18)),
                Title: file?.Title ?? ReadString(ReadPtr(beatmap + 0x24)),
                Version: file?.Version ?? ReadString(ReadPtr(beatmap + 0xAC)),
                Mapper: file?.Creator ?? ReadString(ReadPtr(beatmap + 0x7C)),
                Mods: modName,
                Rate: rate,
                Stars: 0,
                Bpm: (file?.Bpm ?? 0) * rate,
                LengthS: (file?.LengthS ?? 0) / rate,
                IsStandard: file is null || file.Mode == 0)
            {
                OsuPath = osuPath,
                BackgroundPath = bgPath,
            };
        }
        catch
        {
            Detach();
            return null;
        }
    }

    private bool EnsureAttached()
    {
        if (_target is not null && !_target.HasExited && Attached) return true;
        Detach();
        if (DateTime.UtcNow < _nextScan) return false;
        _nextScan = DateTime.UtcNow.AddSeconds(3);

        if (OperatingSystem.IsMacOS()) { State = MacOsState; Blocked = false; return false; }

        var target = StableLocator.Find();
        if (target is null) { State = "osu! not running"; Blocked = false; return false; }

        var memory = target.Open(out var problem, out var blocked);
        if (memory is null)
        {
            State = problem;
            Blocked = blocked;
            target.Dispose();
            return false;
        }

        _target = target;
        _memory = memory;
        Blocked = false;
        State = "osu! found, looking for the map";

        // the patterns sit in JIT-compiled code, which only exists once the
        // game has finished loading: a fresh osu! may need a few tries
        var found = PatternScanner.Scan(memory, [BasePattern, MenuModsPattern], ScanLimit);
        if (found[0] == 0) { State = "osu! is still loading"; CloseKeepProcess(); return false; }
        _base = found[0];
        _menuMods = found[1] == 0 ? 0 : found[1] + 0x9;
        _songsFolder = FindSongsFolder(target);
        return true;
    }

    private void CloseKeepProcess()
    {
        _memory?.Dispose();
        _memory = null;
        _target?.Dispose();
        _target = null;
    }

    private void Detach()
    {
        CloseKeepProcess();
        _base = _menuMods = 0;
    }

    /// <summary>Where osu! keeps its maps: BeatmapDirectory from the user cfg, else Songs.</summary>
    private static string? FindSongsFolder(StableTarget target)
    {
        try
        {
            var root = target.GameFolder();
            if (root is null) return null;
            foreach (var cfg in Directory.EnumerateFiles(root, "osu!.*.cfg"))
            {
                foreach (var line in File.ReadLines(cfg))
                {
                    if (!line.StartsWith("BeatmapDirectory", StringComparison.OrdinalIgnoreCase)) continue;
                    var value = line[(line.IndexOf('=') + 1)..].Trim();
                    if (value.Length == 0) continue;
                    var path = target.LocalPath(root, value);
                    if (path is not null && Directory.Exists(path)) return path;
                }
            }
            var songs = target.LocalPath(root, "Songs");
            return songs is not null && Directory.Exists(songs) ? songs : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- memory

    private long ReadPtr(long address) => (uint)ReadInt(address);

    private int ReadInt(long address)
    {
        if (address == 0 || _memory is null) return 0;
        Span<byte> buffer = stackalloc byte[4];
        return _memory.Read(address, buffer) ? BitConverter.ToInt32(buffer) : 0;
    }

    /// <summary>A .NET string: length at +4, UTF-16 characters from +8.</summary>
    private string ReadString(long address)
    {
        if (address == 0 || _memory is null) return "";
        var length = ReadInt(address + 4);
        if (length <= 0 || length > 4096) return "";
        var bytes = new byte[length * 2];
        return _memory.Read(address + 8, bytes) ? Encoding.Unicode.GetString(bytes) : "";
    }

    public void Dispose() => Detach();
}

/// <summary>osu! mod bits, named the way the API expects them.</summary>
public static class Mods
{
    private static readonly (int Bit, string Name)[] Names =
    [
        (1 << 0, "NF"), (1 << 1, "EZ"), (1 << 2, "TD"), (1 << 3, "HD"), (1 << 4, "HR"),
        (1 << 5, "SD"), (1 << 6, "DT"), (1 << 7, "RX"), (1 << 8, "HT"), (1 << 10, "FL"),
        (1 << 12, "SO"), (1 << 13, "AP"), (1 << 14, "PF"),
    ];

    public static (string Name, double Rate) Describe(int bits)
    {
        var nightcore = (bits & (1 << 9)) != 0;
        var parts = new List<string>();
        foreach (var (bit, name) in Names)
        {
            if ((bits & bit) == 0) continue;
            if (name == "DT" && nightcore) { parts.Add("NC"); continue; }
            if (name == "SD" && (bits & (1 << 14)) != 0) continue; // PF implies SD
            parts.Add(name);
        }
        var rate = (bits & (1 << 6)) != 0 ? 1.5 : (bits & (1 << 8)) != 0 ? 0.75 : 1.0;
        return (string.Concat(parts), rate);
    }
}
