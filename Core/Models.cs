using System.Text.Json;
using System.Text.Json.Serialization;

namespace MapMatcher.Core;

/// <summary>The map selected in osu!, as tosu reports it.</summary>
public sealed record LiveMap(
    string Checksum,
    int BeatmapId,
    int BeatmapsetId,
    string Artist,
    string Title,
    string Version,
    string Mapper,
    string Mods,
    double Rate,
    double Stars,
    double Bpm,
    double LengthS,
    bool IsStandard)
{
    public string Key => $"{Checksum}|{Mods}|{Rate}";

    /// <summary>The .osu on disk, when the built-in reader found it.</summary>
    public string? OsuPath { get; init; }
    /// <summary>The background image on disk.</summary>
    public string? BackgroundPath { get; init; }
    /// <summary>False when the source cannot see mods (lazer and Torii logs).</summary>
    public bool ModsKnown { get; init; } = true;
}

/// <summary>A map picked as the search source instead of the one in game.</summary>
public sealed record PinnedMap(
    string? Checksum,
    int BeatmapId,
    string Artist,
    string Title,
    string Version,
    string Creator,
    double Stars,
    double Bpm,
    double LengthS,
    string? CoverUrl)
{
    /// <summary>A dropped or opened .osu: sent as-is when the server does not know it.</summary>
    public string? OsuPath { get; init; }
    public string? BackgroundPath { get; init; }
    public string Origin { get; init; } = "suggestion";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

public sealed class SteerAxis
{
    public string Axis { get; set; } = "";
    public string More { get; set; } = "";
    public string Less { get; set; } = "";
}

public sealed class Meta
{
    public List<string> Variants { get; set; } = [];
    public Dictionary<string, int>? NMaps { get; set; }
    public List<string> Supports { get; set; } = [];
    public List<SteerAxis> SteerAxes { get; set; } = [];

    public int Total => NMaps is null || NMaps.Count == 0
        ? 0
        : NMaps.TryGetValue("nomod", out var n) ? n : NMaps.Values.First();
}

public sealed class QueryBlock
{
    public string? Checksum { get; set; }
    public int BeatmapId { get; set; }
    public string Label { get; set; } = "";
    public double Stars { get; set; }
    public double Bpm { get; set; }
    public double LengthS { get; set; }
    public bool InCorpus { get; set; }
}

public sealed class Match
{
    public int Rank { get; set; }
    public string Checksum { get; set; } = "";
    public int BeatmapId { get; set; }
    public int BeatmapsetId { get; set; }
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public string Creator { get; set; } = "";
    public string Version { get; set; } = "";
    public double Stars { get; set; }
    public double Bpm { get; set; }
    public double LengthS { get; set; }
    public double Distance { get; set; }
    public double Similarity { get; set; }
    public Dictionary<string, double> GroupShare { get; set; } = [];
    public List<string> Differences { get; set; } = [];
    public List<string> Similarities { get; set; } = [];
    public string? Status { get; set; }
    public bool IsUnranked { get; set; }
    public long? Playcount { get; set; }
    public string UrlWeb { get; set; } = "";
    public string UrlDirect { get; set; } = "";
    public string? UrlCover { get; set; }
}

public sealed class SimilarResponse
{
    public QueryBlock Query { get; set; } = new();
    public string? Variant { get; set; }
    public string? Diverge { get; set; }
    public Dictionary<string, double>? Steer { get; set; }
    public int Count { get; set; }
    public List<Match> Matches { get; set; } = [];
}

public sealed class ApiError
{
    public JsonElement Detail { get; set; }
}
