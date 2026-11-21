using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MapMatcher.Core;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(MapMatcher.Tests.TestApp))]

namespace MapMatcher.Tests;

/// <summary>The real App (theme, fonts, styles), rendered by Skia without a screen.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .With(new FontManagerOptions { DefaultFamilyName = "avares://MapMatcher/Fonts#Geist" });
}

/// <summary>
/// The window built from made-up data, the way docs/window.png and
/// docs/settings.png show it. Screenshots land in the test output folder
/// (screenshots/), to be compared by eye.
/// </summary>
public class WindowTests
{
    private static string Screenshots => Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots")).FullName;

    private static string? RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Let async work, layout, animations and rendering catch up.</summary>
    private static void Pump(int ms)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static (MainWindow Window, Settings Settings) Build(string title = "Last Resort", string version = "Loli's INFINITE")
    {
        var settings = new Settings { Steer = new() { ["object_density"] = -0.3 } };
        var window = new MainWindow(settings, online: false);
        window.Show();
        Pump(100);

        var background = RepoFile(Path.Combine("docs", "banner.png"));
        var map = new LiveMap("0123456789abcdef0123456789abcdef", 2097898, 1001507, "xi", title, version,
                              "Kroytz", "", 1, 7.13, 252, 111, true) { BackgroundPath = background };
        var meta = new Meta
        {
            Supports = ["steer"],
            SteerAxes =
            [
                new SteerAxis { Axis = "object_density", More = "denser", Less = "sparser" },
                new SteerAxis { Axis = "sp_p50", More = "wider jumps", Less = "tighter jumps" },
            ],
        };
        var rows = new (string Artist, string Title, string Version, string Creator, double Stars, double Bpm, double Length, double Sim, string Status)[]
        {
            ("t+pazolite", "Tempestissimo", "Extreme", "Shru", 7.22, 231, 135, 0.80, "loved"),
            ("LeaF", "Musical Movement", "Another AR10", "Snow Note", 7.13, 215, 116, 0.79, "graveyard"),
            ("Rish feat. Choko", "Omochachachacha!!!!!", "paws at u :3", "cwutchy", 6.83, 200, 116, 0.79, "ranked"),
            ("Hirano Aya & Katou Emiri & Fukuhara Kaori & Ookubo Rumi", "Super Driver", "GAMING 1.37x (240bpm) AR9.8", "rockstarrzz", 7.59, 240, 160, 0.79, "graveyard"),
            ("MisoilePunch ~Takenoko Soe~", "Staring at stars", "_Epreus' GRAVITY", "Kroytz", 7.35, 250, 103, 0.78, "graveyard"),
            ("LeaF", "Euphoric Orchid Dance", "Extra", "Mapper", 7.0, 220, 140, 0.78, "ranked"),
        };
        var data = new SimilarResponse
        {
            Query = new QueryBlock { Stars = 7.13, Bpm = 252, LengthS = 111, InCorpus = true },
            Matches = rows.Select((r, i) => new Match
            {
                Rank = i + 1, Artist = r.Artist, Title = r.Title, Version = r.Version, Creator = r.Creator,
                Stars = r.Stars, Bpm = r.Bpm, LengthS = r.Length, Similarity = r.Sim, Distance = 1 - r.Sim, Status = r.Status,
                GroupShare = new() { ["aim"] = 0.34, ["speed"] = 0.22, ["rhythm"] = 0.18, ["spacing"] = 0.16, ["parameters"] = 0.10 },
                Differences = ["denser streams", "longer"], Similarities = ["same jump size", "same AR"],
                UrlDirect = $"osu://b/{i}", UrlWeb = $"https://osu.ppy.sh/b/{i}",
            }).ToList(),
        };
        window.ShowPreview(map, data, meta, "osu!stable connected");
        // covers come from the network in the app; here, the banner stands in for them
        if (background is not null)
        {
            using var stream = File.OpenRead(background);
            var cover = Bitmap.DecodeToWidth(stream, 420);
            foreach (var vm in window.Matches.Where((_, i) => i % 3 != 1)) vm.SetThumb(cover);
        }
        Pump(600);
        return (window, settings);
    }

    [AvaloniaFact]
    public void Main_window_renders_the_map_and_its_suggestions()
    {
        var (window, _) = Build();
        Assert.Equal(6, window.Matches.Count);
        Assert.Equal("Last Resort", window.HeroTitleRun.Text);
        Assert.True(window.ResultsScroll.IsVisible);
        Assert.False(window.EmptyPanel.IsVisible);

        window.Matches[1].IsOpen = true;
        Pump(200);
        window.CaptureRenderedFrame()!.Save(Path.Combine(Screenshots, "window.png"));

        window.Matches[1].IsOpen = false;
        window.ShowUpdate("9.9.9");
        Pump(200);
        window.CaptureRenderedFrame()!.Save(Path.Combine(Screenshots, "window-update.png"));
        window.Close();
    }

    [AvaloniaFact]
    public void A_long_title_wraps_without_losing_its_last_line()
    {
        var (window, _) = Build("Everlasting Eternity", "Requiem of Perpetual Eternity (copy)");
        window.CaptureRenderedFrame()!.Save(Path.Combine(Screenshots, "window-long-title.png"));
        Assert.True(window.HeroTitle.Bounds.Height >= 2 * 42);
        window.Close();
    }

    [AvaloniaFact]
    public void Settings_panel_slides_in_and_keeps_negative_steer()
    {
        var (window, settings) = Build();
        window.SettingsButton.IsChecked = true;
        window.SteerToggle.IsChecked = true;
        Pump(700);
        Assert.True(window.SettingsLayer.IsVisible);
        // a slider bound before its range must not clamp the value to its default minimum
        Assert.Equal(-0.3, window.SteerSliders.Single(r => r.Key == "object_density").Value, 3);
        Assert.Equal("sparser", window.SteerSummary.Text);
        window.CaptureRenderedFrame()!.Save(Path.Combine(Screenshots, "settings.png"));

        window.SettingsButton.IsChecked = false;
        Pump(600);
        Assert.False(window.SettingsLayer.IsVisible);
        window.Close();
        Assert.Equal(-0.3, settings.Steer["object_density"], 3);
    }
}
