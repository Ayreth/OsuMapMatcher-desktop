using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using Avalonia.Media.Imaging;

namespace MapMatcher.Ui;

/// <summary>
/// Covers from assets.ppy.sh, fetched a few at a time and retried: the CDN
/// often drops the first burst of requests, and a plain image loader never
/// tries again. A real 404 is remembered and left empty.
/// </summary>
public static class ImageLoader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim Gate = new(4);
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> Cache = new();

    public static Task<Bitmap?> LoadAsync(string url, int decodeWidth) =>
        Cache.GetOrAdd($"{decodeWidth}|{url}", _ => FetchAsync(url, decodeWidth));

    private static async Task<Bitmap?> FetchAsync(string url, int decodeWidth)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var response = await Http.GetAsync(url).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    return Decode(bytes, decodeWidth);
                }
            }
            catch
            {
                // timeout or reset: try again after a pause
            }
            finally
            {
                Gate.Release();
            }
            await Task.Delay(600 * (attempt + 1)).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>Decoded at most this wide: a cover never needs its full size.</summary>
    public static Bitmap? Decode(byte[] bytes, int decodeWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            return null;
        }
    }
}
