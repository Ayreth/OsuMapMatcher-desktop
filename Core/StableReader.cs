using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MapMatcher.Core;

/// <summary>
/// Reads the map selected in osu!stable straight from the game's memory, the
/// way tosu and gosumemory do: read-only, nothing is written, nothing is
/// injected. Patterns and offsets are the ones tosu uses (packages/tosu/src/
/// memory/stable.ts). Lazer is not covered here; tosu still handles it.
/// </summary>
public sealed class StableReader : IDisposable
{
    // beatmap: [[Base - 0xC]] -> Beatmap object
    private static readonly byte?[] BasePattern = Parse("F8 01 74 04 83 65");
    // mods selected in song select: [[MenuMods + 0x9]]
    private static readonly byte?[] MenuModsPattern = Parse("C8 FF ?? ?? ?? ?? ?? 81 0D ?? ?? ?? ?? ?? 08 00 00");

    private Process? _process;
    private IntPtr _handle;
    private long _base;
    private long _menuMods;
    private string? _songsFolder;
    private DateTime _nextScan = DateTime.MinValue;

    public bool Attached => _handle != IntPtr.Zero && _base != 0;
    public string? SongsFolder => _songsFolder;

    /// <summary>What happened on the last read, for the status line.</summary>
    public string State { get; private set; } = "osu! not running";

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
            if (_songsFolder is not null && folder.Length > 0)
            {
                osuPath = Path.Combine(_songsFolder, folder, fileName);
                if (background.Length > 0) bgPath = Path.Combine(_songsFolder, folder, background);
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
        if (_process is not null && !_process.HasExited && Attached) return true;
        Detach();
        if (DateTime.UtcNow < _nextScan) return false;
        _nextScan = DateTime.UtcNow.AddSeconds(3);

        // lazer's executable has the same name; osu!stable is the 32-bit one
        var process = Process.GetProcessesByName("osu!").FirstOrDefault(IsStable);
        if (process is null) { State = "osu! not running"; return false; }

        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, process.Id);
        if (handle == IntPtr.Zero) { State = "osu! found, access denied"; return false; }

        _process = process;
        _handle = handle;
        State = "osu! found, looking for the map";

        // the patterns sit in JIT-compiled code, which only exists once the
        // game has finished loading: a fresh osu! may need a few tries
        var found = Scan([BasePattern, MenuModsPattern]);
        if (found[0] == 0) { State = "osu! is still loading"; CloseKeepProcess(); return false; }
        _base = found[0];
        _menuMods = found[1] == 0 ? 0 : found[1] + 0x9;
        _songsFolder = FindSongsFolder(process);
        return true;
    }

    private static bool IsStable(Process p)
    {
        try { return IsWow64Process(p.Handle, out var wow64) && wow64; }
        catch { return false; }
    }

    private void CloseKeepProcess()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
        _handle = IntPtr.Zero;
        _process = null;
    }

    private void Detach()
    {
        CloseKeepProcess();
        _base = _menuMods = 0;
    }

    /// <summary>Where osu! keeps its maps: BeatmapDirectory from the user cfg, else Songs.</summary>
    private static string? FindSongsFolder(Process process)
    {
        try
        {
            var exe = process.MainModule?.FileName;
            if (exe is null) return null;
            var root = Path.GetDirectoryName(exe)!;
            foreach (var cfg in Directory.EnumerateFiles(root, "osu!.*.cfg"))
            {
                foreach (var line in File.ReadLines(cfg))
                {
                    if (!line.StartsWith("BeatmapDirectory", StringComparison.OrdinalIgnoreCase)) continue;
                    var value = line[(line.IndexOf('=') + 1)..].Trim();
                    if (value.Length == 0) continue;
                    var path = Path.IsPathRooted(value) ? value : Path.Combine(root, value);
                    if (Directory.Exists(path)) return path;
                }
            }
            var songs = Path.Combine(root, "Songs");
            return Directory.Exists(songs) ? songs : null;
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
        if (address == 0) return 0;
        Span<byte> buffer = stackalloc byte[4];
        return ReadBytes(address, buffer) ? BitConverter.ToInt32(buffer) : 0;
    }

    /// <summary>A .NET string: length at +4, UTF-16 characters from +8.</summary>
    private string ReadString(long address)
    {
        if (address == 0) return "";
        var length = ReadInt(address + 4);
        if (length <= 0 || length > 4096) return "";
        var bytes = new byte[length * 2];
        return ReadBytes(address + 8, bytes) ? Encoding.Unicode.GetString(bytes) : "";
    }

    private unsafe bool ReadBytes(long address, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            return ReadProcessMemory(_handle, (IntPtr)address, (IntPtr)p, buffer.Length, out var read)
                   && read == buffer.Length;
        }
    }

    /// <summary>Walk every committed, readable region once and look for all patterns.</summary>
    private long[] Scan(byte?[][] patterns)
    {
        var found = new long[patterns.Length];
        long address = 0;
        var info = new MemoryBasicInformation();
        var size = Marshal.SizeOf<MemoryBasicInformation>();
        byte[] chunk = [];

        while (address < 0x7FFF_0000 && VirtualQueryEx(_handle, (IntPtr)address, out info, (uint)size) != 0)
        {
            var regionSize = (long)info.RegionSize;
            if (regionSize <= 0) break;
            var readable = info.State == MemCommit
                           && (info.Protect & PageGuard) == 0
                           && (info.Protect & PageNoAccess) == 0
                           && (info.Protect & ReadableMask) != 0;
            if (readable && regionSize < 256L * 1024 * 1024)
            {
                if (chunk.Length < regionSize) chunk = new byte[regionSize];
                var span = chunk.AsSpan(0, (int)regionSize);
                if (ReadBytes((long)info.BaseAddress, span))
                {
                    for (var p = 0; p < patterns.Length; p++)
                    {
                        if (found[p] != 0) continue;
                        var at = IndexOf(span, patterns[p]);
                        if (at >= 0) found[p] = (long)info.BaseAddress + at;
                    }
                    if (found.All(f => f != 0)) break;
                }
            }
            address = (long)info.BaseAddress + regionSize;
        }
        return found;
    }

    private static int IndexOf(ReadOnlySpan<byte> data, byte?[] pattern)
    {
        var first = pattern[0]!.Value;
        var last = data.Length - pattern.Length;
        for (var i = 0; i <= last; i++)
        {
            if (data[i] != first) continue;
            var j = 1;
            for (; j < pattern.Length; j++)
            {
                var b = pattern[j];
                if (b.HasValue && data[i + j] != b.Value) break;
            }
            if (j == pattern.Length) return i;
        }
        return -1;
    }

    private static byte?[] Parse(string pattern) =>
        pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries)
               .Select(t => t == "??" ? (byte?)null : Convert.ToByte(t, 16))
               .ToArray();

    public void Dispose() => Detach();

    // ---------------------------------------------------------------- win32

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    // READONLY | READWRITE | WRITECOPY | EXECUTE_READ | EXECUTE_READWRITE | EXECUTE_WRITECOPY
    private const uint ReadableMask = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation info, uint length);
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
