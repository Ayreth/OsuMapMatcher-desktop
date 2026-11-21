namespace MapMatcher.Core.Stable;

/// <summary>A readable stretch of the game's address space.</summary>
public readonly record struct MemoryRegion(long Start, long Size);

/// <summary>
/// Read-only access to another process's memory: all the OS has to provide.
/// The pattern scan and the pointer walk on top of it are shared.
/// </summary>
public interface IProcessMemory : IDisposable
{
    /// <summary>Fill the whole buffer from that address, or say it could not.</summary>
    bool Read(long address, Span<byte> buffer);

    /// <summary>Committed, readable regions starting below <paramref name="below"/>, in address order.</summary>
    IEnumerable<MemoryRegion> Regions(long below);
}

/// <summary>Byte patterns with wildcards, as tosu writes them ("F8 01 74 ?? 83").</summary>
public static class PatternScanner
{
    /// <summary>Regions this large are not code: skipped, as they always were.</summary>
    public const long MaxRegionSize = 256L * 1024 * 1024;

    public static byte?[] Parse(string pattern) =>
        pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries)
               .Select(t => t == "??" ? (byte?)null : Convert.ToByte(t, 16))
               .ToArray();

    public static int IndexOf(ReadOnlySpan<byte> data, byte?[] pattern)
    {
        var first = pattern[0]!.Value;
        var last = data.Length - pattern.Length;
        for (var i = 0; i <= last; i++)
        {
            if (data[i] != first) continue;
            var j = 1;
            for (; j < pattern.Length; j++)
            {
                var b = pattern[j];
                if (b.HasValue && data[i + j] != b.Value) break;
            }
            if (j == pattern.Length) return i;
        }
        return -1;
    }

    /// <summary>Walk every readable region once and look for all patterns. 0 where one was not found.</summary>
    public static long[] Scan(IProcessMemory memory, byte?[][] patterns, long below)
    {
        var found = new long[patterns.Length];
        byte[] chunk = [];
        foreach (var region in memory.Regions(below))
        {
            if (region.Size <= 0 || region.Size >= MaxRegionSize) continue;
            if (chunk.Length < region.Size) chunk = new byte[region.Size];
            var span = chunk.AsSpan(0, (int)region.Size);
            if (!memory.Read(region.Start, span)) continue;
            for (var p = 0; p < patterns.Length; p++)
            {
                if (found[p] != 0) continue;
                var at = IndexOf(span, patterns[p]);
                if (at >= 0) found[p] = region.Start + at;
            }
            if (found.All(f => f != 0)) break;
        }
        return found;
    }
}
