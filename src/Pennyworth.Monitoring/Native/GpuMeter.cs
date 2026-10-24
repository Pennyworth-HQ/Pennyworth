using System.Runtime.InteropServices;

namespace Pennyworth.Monitoring.Native;

/// <summary>
/// GPU load the way Task Manager shows it: the busiest engine type (3D, video, copy...),
/// summed over all processes, from the "GPU Engine" performance counters.
/// </summary>
internal sealed unsafe partial class GpuMeter : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;

    private nint _query;
    private nint _counter;

    public GpuMeter()
    {
        if (PdhOpenQuery(null, 0, out _query) != 0)
        {
            _query = 0;
            return;
        }

        if (PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", 0, out _counter) != 0)
        {
            Dispose();
            return;
        }

        PdhCollectQueryData(_query); // Rates need two samples; the first one primes the counter.
    }

    public bool IsAvailable => _query != 0;

    /// <summary>0 to 100, or null when the counters are unavailable (old drivers, remote sessions).</summary>
    public double? Sample()
    {
        if (_query == 0 || PdhCollectQueryData(_query) != 0)
        {
            return null;
        }

        uint size = 0;
        uint status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out _, null);
        if (status != PdhMoreData || size == 0)
        {
            return status == 0 ? 0 : null;
        }

        byte* buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            if (PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out uint count, buffer) != 0)
            {
                return null;
            }

            var items = (CounterItem*)buffer;
            var values = new List<(string Instance, double Value)>((int)count);
            for (int i = 0; i < count; i++)
            {
                if (items[i].Status == 0)
                {
                    values.Add((new string(items[i].Name), items[i].Value));
                }
            }

            return Combine(values);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <summary>
    /// Instance names look like "pid_1234_luid_0x0_0xD1A5_phys_0_eng_0_engtype_3D".
    /// Sums each engine type and returns the busiest one, capped at 100.
    /// </summary>
    internal static double Combine(IEnumerable<(string Instance, double Value)> values)
    {
        var byEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instance, value) in values)
        {
            int at = instance.LastIndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            string engine = at < 0 ? "" : instance[(at + 8)..];
            byEngine[engine] = byEngine.GetValueOrDefault(engine) + value;
        }

        return byEngine.Count == 0 ? 0 : Math.Clamp(byEngine.Values.Max(), 0, 100);
    }

    public void Dispose()
    {
        if (_query != 0)
        {
            PdhCloseQuery(_query);
            _query = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public char* Name;
        public uint Status;
        public double Value;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQuery(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounter(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static partial uint PdhGetFormattedCounterArray(nint counter, uint format, ref uint bufferSize, out uint itemCount, byte* items);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}
