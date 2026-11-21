using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MapMatcher.Core;

/// <summary>
/// Which osu! is running. On Windows lazer and stable are both osu!.exe and
/// only the bitness tells them apart; on Linux and macOS lazer runs natively
/// as osu!, while stable can only run under Wine, as osu!.exe.
/// </summary>
public static class GameProcesses
{
    /// <summary>"Torii" or "osu!lazer" when one of them runs, else null.</summary>
    public static string? FindLazerClient()
    {
        if (Running("torii")) return "Torii";
        foreach (var p in Process.GetProcessesByName("osu!"))
        {
            using (p)
            {
                // elsewhere a native process called osu! can only be lazer
                if (!OperatingSystem.IsWindows()) return "osu!lazer";
                try
                {
                    if (IsWow64(p) == false) return "osu!lazer";
                }
                catch { /* access denied: not ours to read anyway */ }
            }
        }
        return null;
    }

    private static bool Running(string name)
    {
        var found = false;
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { found |= p.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase); }
                catch { /* gone meanwhile */ }
            }
        }
        return found;
    }

    /// <summary>True for a 32-bit process on 64-bit Windows: osu!stable. Null when it cannot be asked.</summary>
    [SupportedOSPlatform("windows")]
    public static bool? IsWow64(Process p) => IsWow64Process(p.Handle, out var wow64) ? wow64 : null;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);
}
