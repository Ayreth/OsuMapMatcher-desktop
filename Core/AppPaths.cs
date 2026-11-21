using System.IO;

namespace MapMatcher.Core;

/// <summary>
/// The folders that differ per OS: where our own settings and throwaway
/// copies go, and where osu!lazer and Torii keep their data by default.
/// Windows keeps the exact paths of the Windows-only versions.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "osu!MapMatcher";

    /// <summary>settings.json lives here.</summary>
    public static string ConfigFolder => Current.ConfigFolder;

    /// <summary>Throwaway files (the copy of lazer's database): safe to delete at any time.</summary>
    public static string CacheFolder => Current.CacheFolder;

    /// <summary>Where lazer, then Torii, keep their data unless storage.ini moves it.</summary>
    public static IReadOnlyList<string> LazerDataRoots => Current.LazerDataRoots;

    private static Layout? _current;
    private static Layout Current => _current ??= Layout.For(CurrentOs, Env,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    // on Windows the known folders, exactly what the Windows-only versions asked for
    private static string? Env(string name) => name switch
    {
        "APPDATA" when OperatingSystem.IsWindows() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LOCALAPPDATA" when OperatingSystem.IsWindows() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        _ => Environment.GetEnvironmentVariable(name),
    };

    public enum Os { Windows, Linux, MacOS }

    public static Os CurrentOs =>
        OperatingSystem.IsWindows() ? Os.Windows : OperatingSystem.IsMacOS() ? Os.MacOS : Os.Linux;

    public sealed record Layout(string ConfigFolder, string CacheFolder, IReadOnlyList<string> LazerDataRoots)
    {
        /// <summary>Pure, so every OS can be checked from any OS.</summary>
        public static Layout For(Os os, Func<string, string?> env, string home)
        {
            switch (os)
            {
                case Os.Windows:
                {
                    // %AppData% and %LocalAppData%, as the Windows-only versions used
                    var roaming = env("APPDATA") is { Length: > 0 } a ? a : Path.Combine(home, "AppData", "Roaming");
                    var local = env("LOCALAPPDATA") is { Length: > 0 } l ? l : Path.Combine(home, "AppData", "Local");
                    return new Layout(
                        Path.Combine(roaming, AppFolderName),
                        Path.Combine(local, AppFolderName),
                        [Path.Combine(roaming, "osu-torii"), Path.Combine(roaming, "osu")]);
                }
                case Os.MacOS:
                {
                    var support = Path.Combine(home, "Library", "Application Support");
                    return new Layout(
                        Path.Combine(support, AppFolderName),
                        Path.Combine(home, "Library", "Caches", AppFolderName),
                        [Path.Combine(support, "osu-torii"), Path.Combine(support, "osu")]);
                }
                default:
                {
                    // XDG base directories, with their documented fallbacks
                    string Xdg(string name, string fallback) =>
                        env(name) is { Length: > 0 } v && Path.IsPathRooted(v) ? v : Path.Combine(home, fallback);
                    var data = Xdg("XDG_DATA_HOME", Path.Combine(".local", "share"));
                    return new Layout(
                        Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), AppFolderName),
                        Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), AppFolderName),
                        [Path.Combine(data, "osu-torii"), Path.Combine(data, "osu")]);
                }
            }
        }
    }
}
