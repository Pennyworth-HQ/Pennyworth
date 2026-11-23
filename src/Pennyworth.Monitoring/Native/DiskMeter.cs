using System.Runtime.Versioning;

namespace Pennyworth.Monitoring.Native;

/// <summary>
/// Disk activity the way Task Manager shows it ("active time"): the share of the last second
/// the disks were not idle (averaged over all physical disks), 0 to 100.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DiskMeter : IDisposable
{
    private nint _query;
    private nint _idle;

    public DiskMeter()
    {
        if (Pdh.OpenQuery(null, 0, out _query) != 0)
        {
            _query = 0;
            return;
        }

        if (Pdh.AddEnglishCounter(_query, @"\PhysicalDisk(_Total)\% Idle Time", 0, out _idle) != 0)
        {
            Dispose();
            return;
        }

        Pdh.CollectQueryData(_query); // Rates need two samples; the first one primes the counter.
    }

    /// <summary>0 to 100, or null when the counter is unavailable.</summary>
    public double? Sample()
    {
        if (_query == 0 || Pdh.CollectQueryData(_query) != 0)
        {
            return null;
        }

        if (Pdh.GetFormattedCounterValue(_idle, Pdh.FmtDouble, out _, out var value) != 0 || value.Status != 0)
        {
            return null;
        }

        // The union in PDH_FMT_COUNTERVALUE holds a double when asked for PDH_FMT_DOUBLE.
        double idle = BitConverter.Int64BitsToDouble(value.Value);
        return Math.Clamp(100 - idle, 0, 100);
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
