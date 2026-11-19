using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace MapMatcher.Core;

public sealed record UpdateInfo(string Version, string Url);

/// <summary>Is there a newer release on GitHub than the one running?</summary>
public static class Updates
{
    private const string Latest = "https://api.github.com/repos/Ayreth/OsuMapMatcher-desktop/releases/latest";

    public static Version Current =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    public static async Task<UpdateInfo?> CheckAsync(HttpClient http)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, Latest);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return null;
            var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken: cts.Token);
            if (release is null || release.Draft || release.Prerelease) return null;

            var tag = (release.TagName ?? "").TrimStart('v', 'V');
            if (!Version.TryParse(tag, out var latest)) return null;
            var current = Current;
            // compare major.minor.build only: the revision is not used by releases
            var mine = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            var theirs = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
            return theirs > mine ? new UpdateInfo(tag, release.HtmlUrl ?? "https://github.com/Ayreth/OsuMapMatcher-desktop/releases/latest") : null;
        }
        catch
        {
            return null;   // offline, rate limited: say nothing
        }
    }

    private sealed class Release
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    }
}
