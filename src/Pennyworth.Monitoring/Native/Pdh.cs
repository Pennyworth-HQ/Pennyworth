using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pennyworth.Monitoring.Native;

/// <summary>Windows performance counters (PDH), readable without admin rights.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe partial class Pdh
{
    public const uint FmtDouble = 0x00000200;
    public const uint FmtLarge = 0x00000400;
    public const uint MoreData = 0x800007D2;

    [StructLayout(LayoutKind.Sequential)]
    public struct CounterItem
    {
        public char* Name;
        public uint Status;
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CounterValue
    {
        public uint Status;
        public long Value;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint OpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint AddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll", EntryPoint = "PdhCollectQueryData")]
    public static partial uint CollectQueryData(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial uint GetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* items);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterValue")]
    public static partial uint GetFormattedCounterValue(nint counter, uint format, out uint type, out CounterValue value);

    [LibraryImport("pdh.dll", EntryPoint = "PdhCloseQuery")]
    public static partial uint CloseQuery(nint query);
}

/// <summary>
/// Free memory as Task Manager counts it (free and zeroed pages). Everything else that is
/// "available" is standby cache, which Windows hands back instantly.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MemoryCounters : IDisposable
{
    private nint _query;
    private nint _free;

    public MemoryCounters()
    {
        if (Pdh.OpenQuery(null, 0, out _query) != 0)
        {
            _query = 0;
            return;
        }

        if (Pdh.AddEnglishCounter(_query, @"\Memory\Free & Zero Page List Bytes", 0, out _free) != 0)
        {
            Dispose();
        }
    }

    /// <summary>Bytes of free and zeroed memory, or null when the counter is unavailable.</summary>
    public long? ReadFree()
    {
        if (_query == 0 || Pdh.CollectQueryData(_query) != 0)
        {
            return null;
        }

        return Pdh.GetFormattedCounterValue(_free, Pdh.FmtLarge, out _, out var value) == 0 && value.Status == 0
            ? value.Value
            : null;
    }

    public void Dispose()
    {
        if (_query != 0)
        {
            Pdh.CloseQuery(_query);
            _query = 0;
        }
    }
}
