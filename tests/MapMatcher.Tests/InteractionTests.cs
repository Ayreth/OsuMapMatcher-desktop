using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MapMatcher.Core;
using MapMatcher.Ui;
using Xunit;

namespace MapMatcher.Tests;

/// <summary>Clicks, keys and drops, sent through the real input pipeline.</summary>
public class InteractionTests
{
    private static void Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static MainWindow Window()
    {
        var window = new MainWindow(new Settings(), online: false);
        window.Show();
        Pump(100);
        var map = new LiveMap("0123456789abcdef0123456789abcdef", 1, 2, "xi", "Last Resort", "Loli's INFINITE",
                              "Kroytz", "", 1, 7.13, 252, 111, true);
        var data = new SimilarResponse
        {
            Query = new QueryBlock { Stars = 7.13, InCorpus = true },
            Matches = Enumerable.Range(1, 3).Select(i => new Match
            {
                Rank = i, Artist = "Artist", Title = $"Song {i}", Version = "Extra", Creator = "Mapper",
                Similarity = 0.8, UrlDirect = $"osu://b/{i}", UrlWeb = $"https://osu.ppy.sh/b/{i}",
            }).ToList(),
        };
        window.ShowPreview(map, data, null, "osu!stable connected");
        Pump(200);
        return window;
    }

    private static Point Centre(Visual visual, Visual window) =>
        visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), window)!.Value;

    private static void Click(MainWindow window, Point at)
    {
        window.MouseMove(at);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Pump(50);
    }

    [AvaloniaFact]
    public void Clicking_a_row_folds_its_details_out_and_the_osu_button_does_not()
    {
        var window = Window();
        var row = window.ResultsList.ContainerFromIndex(0)!;
        var vm = window.Matches[0];

        Click(window, row.TranslatePoint(new Point(150, 30), window)!.Value);
        Assert.True(vm.IsOpen);

        string? opened = null;
        window.OpenedOffline += url => opened = url;
        var osu = row.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Count == 0 && b.Width == 34);
        Click(window, Centre(osu, window));
        Assert.Equal("osu://b/1", opened);
        Assert.True(vm.IsOpen);

        Click(window, row.TranslatePoint(new Point(150, 30), window)!.Value);
        Assert.False(vm.IsOpen);
        window.Close();
    }

    [AvaloniaFact]
    public void Escape_closes_the_settings_panel()
    {
        var window = Window();
        Click(window, Centre(window.SettingsButton, window));
        Pump(400);
        Assert.True(window.SettingsLayer.IsVisible);
        window.KeyPress(Key.Escape, RawInputModifiers.None);
        Pump(400);
        Assert.False(window.SettingsLayer.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pasting_a_beatmap_link_searches_from_it()
    {
        var window = Window();
        await window.Clipboard!.SetTextAsync("https://osu.ppy.sh/beatmapsets/41823#osu/129891");
        Pump(50);
        window.KeyPress(Key.V, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control);
        Pump(200);
        Assert.Equal("Beatmap 129891", window.HeroTitleRun.Text);
        Assert.True(window.BackButton.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dropping_a_osu_file_searches_from_it()
    {
        var window = Window();
        var path = Path.Combine(Directory.CreateTempSubdirectory("drop").FullName, "map.osu");
        File.WriteAllText(path, "osu file format v14\n\n[General]\nMode: 0\n\n[Metadata]\nTitle:Dropped Song\nArtist:Someone\nCreator:Me\nVersion:Hard\n\n[TimingPoints]\n0,500,4,2,0,100,1,0\n\n[HitObjects]\n256,192,1000,1,0\n256,192,61000,1,0\n");
        var data = new DataTransfer();
        var file = await window.StorageProvider.TryGetFileFromPathAsync(path);
        Assert.NotNull(file);
        data.Add(DataTransferItem.CreateFile(file));
        var at = new Point(200, 400);

        window.DragDrop(at, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        Pump(50);
        Assert.True(window.DropOverlay.IsVisible);
        window.DragDrop(at, RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        Pump(100);
        Assert.False(window.DropOverlay.IsVisible);
        Assert.Equal("Dropped Song", window.HeroTitleRun.Text);
        Assert.Equal("  [Hard]", window.HeroVersionRun.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Settings_fields_take_typing_and_the_combo_opens()
    {
        var window = Window();
        window.SettingsButton.IsChecked = true;
        window.ConnectionToggle.IsChecked = true;
        Pump(400);

        // the connection section sits below the fold of the settings panel
        window.TosuBox.BringIntoView();
        Pump(100);
        Click(window, Centre(window.TosuBox, window));
        var focused = window.FocusManager!.GetFocusedElement();
        Assert.True(focused == window.TosuBox, $"focus is on {focused?.GetType().Name ?? "nothing"}, panel at {window.SettingsPanel.Bounds}, box at {window.TosuBox.TranslatePoint(default, window)}");
        window.KeyTextInput("127.0.0.1:9999");
        Pump(50);
        Assert.Equal("127.0.0.1:9999", window.TosuBox.Text);

        window.OsuTargetBox.BringIntoView();
        Pump(100);
        Click(window, Centre(window.OsuTargetBox, window));
        Pump(100);
        Assert.True(window.OsuTargetBox.IsDropDownOpen);
        window.OsuTargetBox.SelectedIndex = 1;
        Assert.Equal("web", MatchVm.OsuTarget);
        window.OsuTargetBox.SelectedIndex = 0;
        window.Close();
    }
}
