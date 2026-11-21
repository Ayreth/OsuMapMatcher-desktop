using MapMatcher.Core;
using MapMatcher.Core.Stable;
using Xunit;

namespace MapMatcher.Tests;

public class PatternTests
{
    [Fact]
    public void Parse_reads_bytes_and_wildcards()
    {
        var pattern = PatternScanner.Parse("F8 01 ?? 04  83");
        Assert.Equal(new byte?[] { 0xF8, 0x01, null, 0x04, 0x83 }, pattern);
    }

    [Fact]
    public void IndexOf_honours_wildcards_and_bounds()
    {
        var pattern = PatternScanner.Parse("AA ?? CC");
        Assert.Equal(2, PatternScanner.IndexOf(new byte[] { 0, 1, 0xAA, 0x55, 0xCC }, pattern));
        Assert.Equal(-1, PatternScanner.IndexOf(new byte[] { 0xAA, 0x55, 0xCD }, pattern));
        // a match must not run past the end
        Assert.Equal(-1, PatternScanner.IndexOf(new byte[] { 0, 0, 0xAA, 0x55 }, pattern));
    }

    [Fact]
    public void Scan_finds_each_pattern_in_its_region_and_skips_unreadable_ones()
    {
        var memory = new FakeMemory();
        memory.Add(0x1000, [0, 0, 0, 0]);
        memory.Add(0x2000, [9, 9, 0xF8, 0x01, 0x74, 0x04, 0x83, 0x65], readable: false);
        memory.Add(0x3000, [0, 0xF8, 0x01, 0x74, 0x04, 0x83, 0x65, 0]);
        memory.Add(0x4000, [0xC8, 0xFF, 1, 2, 3, 4, 5, 0x81, 0x0D, 1, 2, 3, 4, 5, 0x08, 0x00, 0x00]);

        var found = PatternScanner.Scan(memory,
            [PatternScanner.Parse("F8 01 74 04 83 65"), PatternScanner.Parse("C8 FF ?? ?? ?? ?? ?? 81 0D ?? ?? ?? ?? ?? 08 00 00")],
            0x7FFF_0000);

        Assert.Equal(0x3001, found[0]);
        Assert.Equal(0x4000, found[1]);
    }

    [Fact]
    public void Scan_stops_at_the_limit()
    {
        var memory = new FakeMemory();
        memory.Add(0x8000_0000, [0xF8, 0x01]);
        var found = PatternScanner.Scan(memory, [PatternScanner.Parse("F8 01")], 0x7FFF_0000);
        Assert.Equal(0, found[0]);
    }

    [Theory]
    [InlineData(0, "", 1.0)]
    [InlineData((1 << 3) | (1 << 6), "HDDT", 1.5)]
    [InlineData((1 << 6) | (1 << 9), "NC", 1.5)]
    [InlineData((1 << 5) | (1 << 14), "PF", 1.0)]
    [InlineData(1 << 8, "HT", 0.75)]
    public void Mods_are_named_like_the_api(int bits, string name, double rate)
    {
        Assert.Equal((name, rate), Mods.Describe(bits));
    }

    /// <summary>Regions in memory, some of which refuse to be read.</summary>
    private sealed class FakeMemory : IProcessMemory
    {
        private readonly List<(long Start, byte[] Data, bool Readable)> _regions = [];

        public void Add(long start, byte[] data, bool readable = true) => _regions.Add((start, data, readable));

        public bool Read(long address, Span<byte> buffer)
        {
            foreach (var (start, data, readable) in _regions)
            {
                if (address < start || address + buffer.Length > start + data.Length) continue;
                if (!readable) return false;
                data.AsSpan((int)(address - start), buffer.Length).CopyTo(buffer);
                return true;
            }
            return false;
        }

        public IEnumerable<MemoryRegion> Regions(long below) =>
            _regions.Where(r => r.Start < below).Select(r => new MemoryRegion(r.Start, r.Data.Length));

        public void Dispose() { }
    }
}

public class WinePathsTests
{
    [Fact]
    public void Maps_lines_are_parsed()
    {
        var entry = WinePaths.ParseMapsLine("00400000-00452000 r-xp 00000000 08:02 173521      /home/u/.wine/drive_c/osu!/osu!.exe");
        Assert.NotNull(entry);
        Assert.Equal(0x400000, entry.Value.Start);
        Assert.Equal(0x52000, entry.Value.Size);
        Assert.True(entry.Value.Readable);
        Assert.Equal("/home/u/.wine/drive_c/osu!/osu!.exe", entry.Value.Path);

        var anonymous = WinePaths.ParseMapsLine("7f0000000000-7f0000021000 ---p 00000000 00:00 0");
        Assert.NotNull(anonymous);
        Assert.False(anonymous.Value.Readable);
        Assert.Equal("", anonymous.Value.Path);

        var spaced = WinePaths.ParseMapsLine("10000-11000 rw-p 00000000 00:00 0   /tmp/a file with spaces");
        Assert.Equal("/tmp/a file with spaces", spaced!.Value.Path);

        Assert.Null(WinePaths.ParseMapsLine(""));
        Assert.Null(WinePaths.ParseMapsLine("garbage line"));
        Assert.Null(WinePaths.ParseMapsLine("zz-11 r--p 0 0 0"));
    }

    [Fact]
    public void Nul_separated_blocks_and_environment()
    {
        var environ = WinePaths.SplitNul("HOME=/home/u\0WINEPREFIX=/games/osu/prefix\0WINEPREFIXX=no\0\0"u8);
        Assert.Equal(3, environ.Count);
        Assert.Equal("/games/osu/prefix", WinePaths.EnvironmentValue(environ, "WINEPREFIX"));
        Assert.Equal("/home/u", WinePaths.EnvironmentValue(environ, "HOME"));
        Assert.Null(WinePaths.EnvironmentValue(environ, "WINEDEBUG"));
    }

    [Theory]
    [InlineData("osu!.exe\n", "", true)]
    [InlineData("wine-preloader", @"C:\osu!\osu!.exe", true)]
    [InlineData("wine64-preload", "/home/u/games/osu!/OSU!.EXE", true)]
    [InlineData("osu!", "/opt/osu/osu!", false)]
    [InlineData("explorer.exe", @"C:\windows\explorer.exe", false)]
    public void Stable_is_recognised_by_name_or_command_line(string comm, string arg0, bool expected)
    {
        var cmdline = arg0.Length > 0 ? new List<string> { arg0 } : [];
        Assert.Equal(expected, WinePaths.IsStableProcess(comm, cmdline));
    }

    [Theory]
    [InlineData(@"C:\osu!\Songs", "/home/u/.wine/drive_c/osu!/Songs")]
    [InlineData(@"c:/users/u/AppData/Local/osu!", "/home/u/.wine/drive_c/users/u/AppData/Local/osu!")]
    [InlineData(@"Z:\home\u\Songs\", "/home/u/Songs")]
    [InlineData(@"Z:\", "/")]
    [InlineData(@"\\?\C:\osu!", "/home/u/.wine/drive_c/osu!")]
    [InlineData("\"C:\\osu!\\osu!.exe\"", "/home/u/.wine/drive_c/osu!/osu!.exe")]
    [InlineData("/mnt/games/osu!/Songs", "/mnt/games/osu!/Songs")]
    [InlineData("Songs", null)]
    [InlineData(@"D:\Songs", null)]
    public void Windows_paths_map_through_the_prefix(string written, string? expected)
    {
        string? Drive(char d) => d switch { 'c' => "/home/u/.wine/drive_c", 'z' => "/", _ => null };
        Assert.Equal(expected, WinePaths.ToUnix(written, Drive));
    }

    [Fact]
    public void Drive_roots_fall_back_to_wine_defaults()
    {
        var prefix = Directory.CreateTempSubdirectory("prefix").FullName;
        try
        {
            // no dosdevices links here: C: is drive_c when it exists, Z: is the root
            Assert.Null(WinePaths.DriveRoot(prefix, 'C'));
            Directory.CreateDirectory(Path.Combine(prefix, "drive_c"));
            Assert.Equal(Path.Combine(prefix, "drive_c"), WinePaths.DriveRoot(prefix, 'C'));
            Assert.Equal("/", WinePaths.DriveRoot(prefix, 'z'));
            Assert.Null(WinePaths.DriveRoot(prefix, 'd'));
        }
        finally
        {
            Directory.Delete(prefix, true);
        }
    }

    [Fact]
    public void Drive_letters_are_recognised()
    {
        Assert.True(WinePaths.IsDrivePath(@"C:\x"));
        Assert.True(WinePaths.IsDrivePath("z:/"));
        Assert.False(WinePaths.IsDrivePath("C:"));
        Assert.False(WinePaths.IsDrivePath("/c/x"));
        Assert.Equal("osu!.exe", WinePaths.FileName(@"C:\osu!\osu!.exe"));
        Assert.Equal("osu!.exe", WinePaths.FileName("/a/osu!.exe"));
    }
}

public class AppPathsTests
{
    private static string Unix(string path) => path.Replace('\\', '/');

    [Fact]
    public void Linux_uses_xdg_folders_with_their_fallbacks()
    {
        var bare = AppPaths.Layout.For(AppPaths.Os.Linux, _ => null, "/home/u");
        Assert.Equal("/home/u/.config/osu!MapMatcher", Unix(bare.ConfigFolder));
        Assert.Equal("/home/u/.cache/osu!MapMatcher", Unix(bare.CacheFolder));
        Assert.Equal(["/home/u/.local/share/osu-torii", "/home/u/.local/share/osu"], bare.LazerDataRoots.Select(Unix));

        var xdg = AppPaths.Layout.For(AppPaths.Os.Linux, name => name switch
        {
            "XDG_DATA_HOME" => "/data",
            "XDG_CONFIG_HOME" => "/cfg",
            "XDG_CACHE_HOME" => "relative/is/ignored",
            _ => null,
        }, "/home/u");
        Assert.Equal("/cfg/osu!MapMatcher", Unix(xdg.ConfigFolder));
        Assert.Equal("/home/u/.cache/osu!MapMatcher", Unix(xdg.CacheFolder));
        Assert.Equal("/data/osu", Unix(xdg.LazerDataRoots[1]));
    }

    [Fact]
    public void MacOS_uses_library_folders()
    {
        var mac = AppPaths.Layout.For(AppPaths.Os.MacOS, _ => null, "/Users/u");
        Assert.Equal("/Users/u/Library/Application Support/osu!MapMatcher", Unix(mac.ConfigFolder));
        Assert.Equal("/Users/u/Library/Caches/osu!MapMatcher", Unix(mac.CacheFolder));
        Assert.Equal("/Users/u/Library/Application Support/osu", Unix(mac.LazerDataRoots[1]));
    }

    [Fact]
    public void Windows_keeps_the_appdata_folders()
    {
        var win = AppPaths.Layout.For(AppPaths.Os.Windows,
            name => name == "APPDATA" ? @"C:\Users\u\AppData\Roaming" : name == "LOCALAPPDATA" ? @"C:\Users\u\AppData\Local" : null,
            @"C:\Users\u");
        Assert.Equal(@"C:/Users/u/AppData/Roaming/osu!MapMatcher", Unix(win.ConfigFolder));
        Assert.Equal(@"C:/Users/u/AppData/Local/osu!MapMatcher", Unix(win.CacheFolder));
        Assert.Equal([@"C:/Users/u/AppData/Roaming/osu-torii", @"C:/Users/u/AppData/Roaming/osu"], win.LazerDataRoots.Select(Unix));
    }

    [Fact]
    public void Lazer_display_title_matches_what_lazer_logs()
    {
        Assert.Equal("xi - Last Resort (Kroytz) [Loli's INFINITE]", LazerReader.DisplayTitle("xi", "Last Resort", "Kroytz", "Loli's INFINITE"));
        Assert.Equal("unknown artist - unknown title", LazerReader.DisplayTitle("", "", "", ""));
    }
}
