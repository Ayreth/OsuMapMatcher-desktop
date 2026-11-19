using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MapMatcher.Ui;

/// <summary>
/// Covers from assets.ppy.sh, fetched a few at a time and retried: the CDN
/// often drops the first burst of requests, and WPF's own loader never tries
/// again. A real 404 is remembered and left empty.
/// </summary>
public static class ImageLoader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static readonly SemaphoreSlim Gate = new(4);
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> Cache = new();

    public static Task<ImageSource?> LoadAsync(string url, int decodeWidth) =>
        Cache.GetOrAdd($"{decodeWidth}|{url}", _ => FetchAsync(url, decodeWidth));

    private static async Task<ImageSource?> FetchAsync(string url, int decodeWidth)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Gate.WaitAsync();
            try
            {
                using var response = await Http.GetAsync(url);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
                if (response.IsSuccessStatusCode)
                {
                    var bytes = await response.Content.ReadAsByteArrayAsync();
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
            await Task.Delay(600 * (attempt + 1));
        }
        return null;
    }

    public static ImageSource? Decode(byte[] bytes, int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes);
            image.DecodePixelWidth = decodeWidth;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
