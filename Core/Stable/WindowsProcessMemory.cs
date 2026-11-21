using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MapMatcher.Core.Stable;

/// <summary>ReadProcessMemory and VirtualQueryEx: osu!stable on Windows.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessMemory : IProcessMemory
{
    private IntPtr _handle;

    private WindowsProcessMemory(IntPtr handle) => _handle = handle;

    /// <summary>Null when Windows refuses the handle (a game run as administrator, say).</summary>
    public static WindowsProcessMemory? Open(int processId)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, processId);
        return handle == IntPtr.Zero ? null : new WindowsProcessMemory(handle);
    }

    public unsafe bool Read(long address, Span<byte> buffer)
    {
        fixed (byte* p = buffer)
        {
            return ReadProcessMemory(_handle, (IntPtr)address, (IntPtr)p, buffer.Length, out var read)
                   && read == buffer.Length;
        }
    }

    public IEnumerable<MemoryRegion> Regions(long below)
    {
        long address = 0;
        var size = (uint)Marshal.SizeOf<MemoryBasicInformation>();
        while (address < below && VirtualQueryEx(_handle, (IntPtr)address, out var info, size) != 0)
        {
            var regionSize = (long)info.RegionSize;
            if (regionSize <= 0) yield break;
            var readable = info.State == MemCommit
                           && (info.Protect & PageGuard) == 0
                           && (info.Protect & PageNoAccess) == 0
                           && (info.Protect & ReadableMask) != 0;
            if (readable) yield return new MemoryRegion((long)info.BaseAddress, regionSize);
            address = (long)info.BaseAddress + regionSize;
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    // READONLY | READWRITE | WRITECOPY | EXECUTE_READ | EXECUTE_READWRITE | EXECUTE_WRITECOPY
    private const uint ReadableMask = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, IntPtr buffer, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation info, uint length);
}
