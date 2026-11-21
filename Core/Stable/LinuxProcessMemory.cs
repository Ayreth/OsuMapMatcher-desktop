using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MapMatcher.Core.Stable;

/// <summary>
/// process_vm_readv and /proc/&lt;pid&gt;/maps: osu!stable under Wine on Linux.
/// Linux lets a process read another one of the same user only if ptrace
/// would be allowed too, which Yama (ptrace_scope 1, the default on most
/// distributions) refuses to non-parents: the read then fails with EPERM.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxProcessMemory : IProcessMemory
{
    public const int EPERM = 1;
    public const int ESRCH = 3;

    private readonly int _pid;

    public LinuxProcessMemory(int pid) => _pid = pid;

    /// <summary>errno of the last failed read.</summary>
    public int LastError { get; private set; }

    /// <summary>
    /// Try a few reads before scanning, to tell "not allowed" apart from
    /// "not there". 0 when reading works, else the errno that stopped it.
    /// </summary>
    public int Probe()
    {
        List<MemoryRegion> regions;
        try { regions = Regions(long.MaxValue).Take(16).ToList(); }
        catch (UnauthorizedAccessException) { return EPERM; }
        catch (IOException) { return ESRCH; }
        Span<byte> one = stackalloc byte[4];
        var error = ESRCH;
        foreach (var region in regions)
        {
            if (Read(region.Start, one)) return 0;
            if (LastError == EPERM) return EPERM;
            error = LastError;
        }
        return error;
    }

    public unsafe bool Read(long address, Span<byte> buffer)
    {
        if (buffer.Length == 0) return true;
        fixed (byte* p = buffer)
        {
            var local = new IoVec { Base = (IntPtr)p, Length = (nuint)buffer.Length };
            var remote = new IoVec { Base = (IntPtr)address, Length = (nuint)buffer.Length };
            var read = process_vm_readv(_pid, &local, 1, &remote, 1, 0);
            if (read < 0)
            {
                LastError = Marshal.GetLastPInvokeError();
                return false;
            }
            // a short read means part of the range is not mapped: as good as a failure
            return read == buffer.Length;
        }
    }

    public IEnumerable<MemoryRegion> Regions(long below)
    {
        // read in one go: the map changes while the game runs
        var lines = File.ReadAllLines($"/proc/{_pid}/maps");
        foreach (var line in lines)
        {
            if (WinePaths.ParseMapsLine(line) is not { } entry) continue;
            if (entry.Start >= below) break;
            // [vvar] and friends belong to the kernel, /dev mappings to drivers (the GPU): not the game's code
            if (!entry.Readable || entry.Path.StartsWith("[v", StringComparison.Ordinal)
                || entry.Path.StartsWith("/dev/", StringComparison.Ordinal)) continue;
            yield return new MemoryRegion(entry.Start, entry.Size);
        }
    }

    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVec
    {
        public IntPtr Base;
        public nuint Length;
    }

    // glibc: the one libc the linux-x64 build targets
    [DllImport("libc.so.6", SetLastError = true)]
    private static extern unsafe nint process_vm_readv(int pid, IoVec* local, nuint localCount, IoVec* remote, nuint remoteCount, nuint flags);
}
