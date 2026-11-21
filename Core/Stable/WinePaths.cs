using System.Globalization;
using System.IO;
using System.Text;

namespace MapMatcher.Core.Stable;

/// <summary>One line of /proc/&lt;pid&gt;/maps.</summary>
public readonly record struct MapsEntry(long Start, long End, bool Readable, string Path)
{
    public long Size => End - Start;
}

/// <summary>
/// osu!stable on Linux runs under Wine: the process is found through /proc,
/// and every path it knows (its own folder, the cfg's BeatmapDirectory) is a
/// Windows path that has to be turned into a Unix one through the prefix.
/// Pure helpers, so they can be tested on any OS.
/// </summary>
public static class WinePaths
{
    /// <summary>"00400000-00401000 r-xp 00000000 08:01 1234   /path/to/file" or null if it is not one.</summary>
    public static MapsEntry? ParseMapsLine(string line)
    {
        var parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return null;
        var dash = parts[0].IndexOf('-');
        if (dash <= 0) return null;
        if (!long.TryParse(parts[0].AsSpan(0, dash), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var start)) return null;
        if (!long.TryParse(parts[0].AsSpan(dash + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var end)) return null;
        if (end <= start || parts[1].Length < 4) return null;
        return new MapsEntry(start, end, parts[1][0] == 'r', parts.Length > 5 ? parts[5].Trim() : "");
    }

    /// <summary>/proc/&lt;pid&gt;/cmdline and /proc/&lt;pid&gt;/environ: NUL-separated strings.</summary>
    public static List<string> SplitNul(ReadOnlySpan<byte> data)
    {
        var list = new List<string>();
        foreach (var part in Encoding.UTF8.GetString(data).Split('\0'))
            if (part.Length > 0) list.Add(part);
        return list;
    }

    /// <summary>A variable out of an environ block, or null.</summary>
    public static string? EnvironmentValue(IEnumerable<string> environ, string name)
    {
        foreach (var entry in environ)
        {
            if (entry.Length > name.Length && entry[name.Length] == '=' && entry.StartsWith(name, StringComparison.Ordinal))
                return entry[(name.Length + 1)..];
        }
        return null;
    }

    /// <summary>Is this the Wine process running osu!stable? By its name, or by the program it was started with.</summary>
    public static bool IsStableProcess(string comm, IReadOnlyList<string> cmdline)
    {
        if (comm.Trim().Equals("osu!.exe", StringComparison.OrdinalIgnoreCase)) return true;
        return cmdline.Count > 0 && FileName(cmdline[0]).Equals("osu!.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The last component of a path written either way.</summary>
    public static string FileName(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? path[(cut + 1)..] : path;
    }

    /// <summary>"C:\..." or "Z:/...": a path with a drive letter.</summary>
    public static bool IsDrivePath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    /// <summary>
    /// A Windows path as Wine sees it, turned into the Unix path it points at.
    /// <paramref name="driveRoot"/> gives the Unix folder of a drive letter.
    /// Unix paths come back as they are; relative ones give null.
    /// </summary>
    public static string? ToUnix(string path, Func<char, string?> driveRoot)
    {
        path = path.Trim().Trim('"');
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
        if (path.StartsWith('/')) return path;
        if (!IsDrivePath(path)) return null;
        var root = driveRoot(char.ToLowerInvariant(path[0]));
        if (root is null) return null;
        var rest = path[3..].Replace('\\', '/').Trim('/');
        if (rest.Length == 0) return root;
        return root.EndsWith('/') ? root + rest : root + "/" + rest;
    }

    /// <summary>
    /// The Unix folder behind a drive letter in a prefix: the dosdevices
    /// symlink when there is one, else Wine's defaults (C: is drive_c, Z: is /).
    /// </summary>
    public static string? DriveRoot(string prefix, char drive)
    {
        drive = char.ToLowerInvariant(drive);
        try
        {
            var link = new DirectoryInfo(Path.Combine(prefix, "dosdevices", drive + ":"));
            if (link.LinkTarget is { } target)
            {
                var full = Path.IsPathRooted(target) ? target : Path.GetFullPath(Path.Combine(link.Parent!.FullName, target));
                return full.Length > 1 ? full.TrimEnd('/') : full;
            }
        }
        catch { /* no such link, or a filesystem that cannot hold one */ }
        if (drive == 'c')
        {
            var driveC = Path.Combine(prefix, "drive_c");
            return Directory.Exists(driveC) ? driveC : null;
        }
        return drive == 'z' ? "/" : null;
    }

    /// <summary>
    /// The path as it exists on disk, matching each component without regard
    /// to case: Windows programs do not care, a Linux filesystem does. Null if
    /// nothing matches.
    /// </summary>
    public static string? ResolveCase(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) return path;
        if (!path.StartsWith('/')) return null;
        var current = "/";
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var exact = Path.Combine(current, part);
            if (Directory.Exists(exact) || File.Exists(exact)) { current = exact; continue; }
            string? hit = null;
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    if (!Path.GetFileName(entry).Equals(part, StringComparison.OrdinalIgnoreCase)) continue;
                    hit = entry;
                    break;
                }
            }
            catch { return null; }
            if (hit is null) return null;
            current = hit;
        }
        return current;
    }
}
