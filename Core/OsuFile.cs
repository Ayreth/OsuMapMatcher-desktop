using System.Globalization;
using System.IO;
using System.Security.Cryptography;

namespace MapMatcher.Core;

/// <summary>
/// Just enough of a .osu file to name the map and show its background:
/// metadata, mode, background, main BPM and drain length.
/// </summary>
public sealed class OsuFile
{
    public string Artist { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Version { get; private set; } = "";
    public string Creator { get; private set; } = "";
    public int BeatmapId { get; private set; }
    public int BeatmapsetId { get; private set; }
    public int Mode { get; private set; }
    public string? BackgroundPath { get; private set; }
    public double Bpm { get; private set; }
    public double LengthS { get; private set; }
    public string Checksum { get; private set; } = "";
    public string Path { get; private set; } = "";

    public static OsuFile? TryLoad(string path)
    {
        try { return Load(path); }
        catch { return null; }
    }

    public static OsuFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var file = new OsuFile
        {
            Path = path,
            // osu! identifies a beatmap by the MD5 of its .osu
            Checksum = Convert.ToHexStringLower(MD5.HashData(bytes)),
        };

        var section = "";
        var beatLengths = new List<(double Time, double BeatLength)>();
        double firstObject = -1, lastObject = 0;
        using var reader = new StringReader(System.Text.Encoding.UTF8.GetString(bytes));
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }

            switch (section)
            {
                case "General" or "Metadata":
                    var colon = line.IndexOf(':');
                    if (colon < 0) break;
                    var key = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    switch (key)
                    {
                        case "Mode": file.Mode = Int(value); break;
                        case "Artist": file.Artist = value; break;
                        case "ArtistUnicode" when file.Artist.Length == 0: file.Artist = value; break;
                        case "Title": file.Title = value; break;
                        case "TitleUnicode" when file.Title.Length == 0: file.Title = value; break;
                        case "Version": file.Version = value; break;
                        case "Creator": file.Creator = value; break;
                        case "BeatmapID": file.BeatmapId = Int(value); break;
                        case "BeatmapSetID": file.BeatmapsetId = Int(value); break;
                    }
                    break;

                case "Events":
                    // 0,0,"bg.jpg",0,0 is the background
                    if (file.BackgroundPath is null && line.StartsWith("0,0,"))
                    {
                        var parts = line.Split(',');
                        if (parts.Length >= 3)
                        {
                            var name = parts[2].Trim('"');
                            file.BackgroundPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name);
                        }
                    }
                    break;

                case "TimingPoints":
                    var tp = line.Split(',');
                    if (tp.Length < 2) break;
                    var uninherited = tp.Length < 7 || tp[6].Trim() == "1";
                    var beat = Dbl(tp[1]);
                    if (uninherited && beat > 0) beatLengths.Add((Dbl(tp[0]), beat));
                    break;

                case "HitObjects":
                    var ho = line.Split(',');
                    if (ho.Length < 3) break;
                    var t = Dbl(ho[2]);
                    if (firstObject < 0) firstObject = t;
                    lastObject = Math.Max(lastObject, t);
                    break;
            }
        }

        if (firstObject >= 0) file.LengthS = Math.Max(0, (lastObject - firstObject) / 1000);
        file.Bpm = MainBpm(beatLengths, lastObject);
        return file;
    }

    /// <summary>The BPM that lasts longest, as osu! shows it.</summary>
    private static double MainBpm(List<(double Time, double BeatLength)> points, double end)
    {
        if (points.Count == 0) return 0;
        var durations = new Dictionary<double, double>();
        for (var i = 0; i < points.Count; i++)
        {
            var until = i + 1 < points.Count ? points[i + 1].Time : Math.Max(end, points[i].Time);
            var bl = Math.Round(points[i].BeatLength, 3);
            durations[bl] = durations.GetValueOrDefault(bl) + Math.Max(0, until - points[i].Time);
        }
        var main = durations.MaxBy(kv => kv.Value).Key;
        return main > 0 ? 60000 / main : 0;
    }

    private static int Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    private static double Dbl(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
