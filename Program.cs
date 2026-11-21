using Avalonia;
using Avalonia.Media;

namespace MapMatcher;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Geist everywhere, popups and tooltips included: they do not inherit the window's font
            .With(new FontManagerOptions { DefaultFamilyName = "avares://MapMatcher/Fonts#Geist" })
            .LogToTrace();
}
