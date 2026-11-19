using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MapMatcher.Core;

public sealed class SearchResult
{
    public SimilarResponse? Data { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Talks to the MapMatcher API. The player's machine comes first: if the
/// local server from start.bat answers with an index, it is used and nothing
/// leaves the network. Otherwise the public API.
/// </summary>
public sealed class ApiClient
{
    public const string DefaultApi = "https://api.ayreth.site";
    public const string LocalApi = "http://127.0.0.1:8000";

    private static readonly TimeSpan LocalProbe = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RemoteProbe = TimeSpan.FromSeconds(8);

    public HttpClient Http { get; }

    public string BaseUrl { get; private set; } = DefaultApi;
    public bool IsLocal { get; private set; }
    public Meta? Meta { get; private set; }

    public ApiClient()
    {
        Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("osu-MapMatcher-desktop", "1.0"));
        Http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>Choose who answers searches. A forced URL wins over everything.</summary>
    public async Task<Meta?> PickAsync(string? forced)
    {
        if (!string.IsNullOrWhiteSpace(forced))
        {
            BaseUrl = forced.Trim().TrimEnd('/');
            IsLocal = BaseUrl.Contains("127.0.0.1") || BaseUrl.Contains("localhost");
            return Meta = await AskMetaAsync(BaseUrl, RemoteProbe);
        }

        var local = await AskMetaAsync(LocalApi, LocalProbe);
        // a local server without an index answers /meta but cannot search
        if (local is { Variants.Count: > 0 })
        {
            BaseUrl = LocalApi;
            IsLocal = true;
            return Meta = local;
        }
        BaseUrl = DefaultApi;
        IsLocal = false;
        return Meta = await AskMetaAsync(BaseUrl, RemoteProbe);
    }

    private async Task<Meta?> AskMetaAsync(string url, TimeSpan budget)
    {
        using var cts = new CancellationTokenSource(budget);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url + "/api/v1/meta");
            request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            using var response = await Http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<Meta>(Json.Options, cts.Token);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Search by checksum (or beatmap id for a pasted link). A map the server
    /// has never seen answers 404: its .osu, read from tosu, is then posted.
    /// </summary>
    public async Task<SearchResult> SimilarAsync(
        IReadOnlyList<KeyValuePair<string, string>> query,
        Func<CancellationToken, Task<byte[]?>>? fileFallback,
        CancellationToken ct)
    {
        try
        {
            var url = BaseUrl + "/api/v1/similar?" + Encode(query);
            using var response = await Http.GetAsync(url, ct);
            if (response.StatusCode == HttpStatusCode.NotFound && fileFallback is not null)
            {
                var bytes = await fileFallback(ct);
                if (bytes is { Length: > 0 })
                {
                    var upload = BaseUrl + "/api/v1/similar/file?" +
                                 Encode(query.Where(p => p.Key is not "checksum" and not "beatmap_id"));
                    using var content = new ByteArrayContent(bytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    using var posted = await Http.PostAsync(upload, content, ct);
                    return await ReadAsync(posted, ct);
                }
            }
            return await ReadAsync(response, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new SearchResult { Error = $"Cannot reach {Pretty(BaseUrl)}." };
        }
    }

    private static async Task<SearchResult> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            var data = await response.Content.ReadFromJsonAsync<SimilarResponse>(Json.Options, ct);
            return new SearchResult { Data = data };
        }
        string message = response.ReasonPhrase ?? "Search failed.";
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ApiError>(Json.Options, ct);
            if (error?.Detail.ValueKind == JsonValueKind.String) message = error.Detail.GetString() ?? message;
            else if (error?.Detail.ValueKind == JsonValueKind.Array) message = "The server refused these settings.";
        }
        catch { /* keep the reason phrase */ }
        return new SearchResult { Error = message };
    }

    private static string Encode(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    public static string Pretty(string url) => url.Replace("https://", "").Replace("http://", "");
}
