using System.IO;
using System.Text.Json;

namespace MapMatcher.Core;

/// <summary>What the player set last time, kept in %AppData%\osu!MapMatcher.</summary>
public sealed class Settings
{
    public Dictionary<string, double> Weights { get; set; } = [];
    public Dictionary<string, double> Steer { get; set; } = [];
    public string Diverge { get; set; } = "";
    public double StarRange { get; set; } = 0.5;
    public double LengthMin { get; set; } = 0.5;
    public double LengthMax { get; set; } = 2;
    public int Results { get; set; } = 20;
    public bool ExcludeSameSet { get; set; } = true;
    public bool OnePerSong { get; set; } = true;
    public bool IncludeUnranked { get; set; } = true;
    public string TosuHost { get; set; } = "127.0.0.1:24050";
    public string ApiOverride { get; set; } = "";
    public bool AlwaysOnTop { get; set; }
    public double Width { get; set; } = 560;
    public double Height { get; set; } = 960;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }
    public List<string> OpenSections { get; set; } = ["weights", "filters"];

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "osu!MapMatcher");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* a broken file is not worth a crash: start fresh */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
