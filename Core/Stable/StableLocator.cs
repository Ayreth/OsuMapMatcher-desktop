using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;

namespace MapMatcher.Core.Stable;

/// <summary>A running osu!stable, and how to reach its memory and its files from here.</summary>
public abstract class StableTarget : IDisposable
{
    protected StableTarget(Process process) => Process = process;

    public Process Process { get; }
    public bool HasExited
    {
        get
        {
            try { return Process.HasExited; }
            catch { return true; }
        }
    }

    /// <summary>The folder holding osu!.exe and the cfg files, as a local path.</summary>
    public abstract string? GameFolder();

    /// <summary>A path read from the cfg (absolute or relative to the game) as a local one.</summary>
    public abstract string? LocalPath(string gameFolder, string written);

    /// <summary>A beatmap file under the songs folder, from the relative names osu! keeps in memory.</summary>
    public abstract string SongPath(string songsFolder, string folder, string file);

    /// <summary>The memory, or null with the reason for the status line.</summary>
    public abstract IProcessMemory? Open(out string problem, out bool blocked);

    public void Dispose() => Process.Dispose();
}

/// <summary>Finds osu!stable on the current OS. Null on macOS: reading another process needs root there.</summary>
public static class StableLocator
{
    public static StableTarget? Find()
    {
        if (OperatingSystem.IsWindows()) return WindowsStable.Find();
        if (OperatingSystem.IsLinux()) return LinuxStable.Find();
        return null;
    }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsStable(Process process) : StableTarget(process)
{
    public static StableTarget? Find()
    {
        // lazer's executable has the same name; osu!stable is the 32-bit one
        var process = Process.GetProcessesByName("osu!").FirstOrDefault(IsStable);
        return process is null ? null : new WindowsStable(process);
    }

    private static bool IsStable(Process p)
    {
        try { return GameProcesses.IsWow64(p) == true; }
        catch { return false; }
    }

    public override string? GameFolder()
    {
        var exe = Process.MainModule?.FileName;
        return exe is null ? null : Path.GetDirectoryName(exe);
    }

    public override string? LocalPath(string gameFolder, string written) =>
        Path.IsPathRooted(written) ? written : Path.Combine(gameFolder, written);

    public override string SongPath(string songsFolder, string folder, string file) =>
        Path.Combine(songsFolder, folder, file);

    public override IProcessMemory? Open(out string problem, out bool blocked)
    {
        var memory = WindowsProcessMemory.Open(Process.Id);
        problem = memory is null ? "osu! found, access denied" : "";
        blocked = false;   // the status line keeps saying the game is not running, as it always did
        return memory;
    }
}

[SupportedOSPlatform("linux")]
internal sealed class LinuxStable : StableTarget
{
    private readonly IReadOnlyList<string> _cmdline;
    private readonly string _prefix;

    private LinuxStable(Process process, IReadOnlyList<string> cmdline, IReadOnlyList<string> environ) : base(process)
    {
        _cmdline = cmdline;
        var home = WinePaths.EnvironmentValue(environ, "HOME") is { Length: > 0 } h
            ? h : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _prefix = WinePaths.EnvironmentValue(environ, "WINEPREFIX") is { Length: > 0 } p
            ? p.TrimEnd('/') : Path.Combine(home, ".wine");
    }

    /// <summary>The Wine process whose name, or first argument, is osu!.exe.</summary>
    public static StableTarget? Find()
    {
        var self = Environment.ProcessId;
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == self) continue;
            try
            {
                var comm = File.ReadAllText(Path.Combine(dir, "comm"));
                var cmdline = WinePaths.SplitNul(File.ReadAllBytes(Path.Combine(dir, "cmdline")));
                if (!WinePaths.IsStableProcess(comm, cmdline)) continue;
                List<string> environ;
                try { environ = WinePaths.SplitNul(File.ReadAllBytes(Path.Combine(dir, "environ"))); }
                catch { environ = []; }
                return new LinuxStable(Process.GetProcessById(pid), cmdline, environ);
            }
            catch
            {
                // gone meanwhile, or someone else's process
            }
        }
        return null;
    }

    private string? DriveRoot(char drive) => WinePaths.DriveRoot(_prefix, drive);

    public override string? GameFolder()
    {
        // what Wine was asked to run: C:\...\osu!.exe, or a Unix path
        if (_cmdline.Count > 0 && WinePaths.ToUnix(_cmdline[0], DriveRoot) is { } exe
            && WinePaths.ResolveCase(exe) is { } found && File.Exists(found))
            return Path.GetDirectoryName(found);

        // started as "wine osu!.exe" from its folder: the working directory is the game
        try
        {
            var cwd = new DirectoryInfo($"/proc/{Process.Id}/cwd").LinkTarget;
            if (cwd is not null && WinePaths.ResolveCase(Path.Combine(cwd, "osu!.exe")) is not null) return cwd;
        }
        catch { /* not ours to look at */ }
        return null;
    }

    public override string? LocalPath(string gameFolder, string written)
    {
        var path = WinePaths.ToUnix(written, DriveRoot)
                   ?? Path.Combine(gameFolder, written.Replace('\\', '/').TrimStart('/'));
        return WinePaths.ResolveCase(path) ?? path;
    }

    public override string SongPath(string songsFolder, string folder, string file)
    {
        var path = Path.Combine(songsFolder, folder.Replace('\\', '/'), file);
        return WinePaths.ResolveCase(path) ?? path;
    }

    public override IProcessMemory? Open(out string problem, out bool blocked)
    {
        var memory = new LinuxProcessMemory(Process.Id);
        var error = memory.Probe();
        problem = error switch
        {
            0 => "",
            LinuxProcessMemory.EPERM => "osu! found, Linux denies memory access (see README)",
            _ => "osu! found, its memory cannot be read",
        };
        blocked = error == LinuxProcessMemory.EPERM;
        return error == 0 ? memory : null;
    }
}
