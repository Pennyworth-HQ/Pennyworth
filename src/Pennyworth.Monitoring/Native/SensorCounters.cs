using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pennyworth.Monitoring.Native;

/// <summary>
/// Power draw and temperature where Windows reports them without a driver or admin rights:
/// the "Energy Meter" counters (processor package power, on processors that expose it), the
/// "Power Meter" counters (some laptops and servers) and the ACPI thermal zones. Many desktops
/// report none of these; the readings are then null.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class SensorCounters : IDisposable
{
    /// <summary>Readings outside these ranges are a broken sensor, not a measurement.</summary>
    private const double MaxWatts = 2000;
    private const double MinCelsius = 1;
    private const double MaxCelsius = 150;

    private nint _query;
    private nint _energy;
    private nint _powerMeter;
    private nint _thermalPrecise;
    private nint _thermal;

    public SensorCounters()
    {
        if (Pdh.OpenQuery(null, 0, out _query) != 0)
        {
            _query = 0;
            return;
        }

        // Each counter is optional: a missing one is simply never read.
        _ = Pdh.AddEnglishCounter(_query, @"\Energy Meter(*)\Power", 0, out _energy);
        _ = Pdh.AddEnglishCounter(_query, @"\Power Meter(*)\Power", 0, out _powerMeter);
        _ = Pdh.AddEnglishCounter(_query, @"\Thermal Zone Information(*)\High Precision Temperature", 0, out _thermalPrecise);
        _ = Pdh.AddEnglishCounter(_query, @"\Thermal Zone Information(*)\Temperature", 0, out _thermal);
        Pdh.CollectQueryData(_query);
    }

    /// <summary>Watts and degrees Celsius; each null when this PC doesn't report it.</summary>
    public (double? Watts, double? Celsius) Sample()
    {
        if (_query == 0 || Pdh.CollectQueryData(_query) != 0)
        {
            return (null, null);
        }

        double? watts = PickPower(Read(_energy), Read(_powerMeter));
        double? celsius = PickTemperature(Read(_thermalPrecise), tenthsOfKelvin: true)
            ?? PickTemperature(Read(_thermal), tenthsOfKelvin: false);
        return (watts, celsius);
    }

    /// <summary>
    /// Energy Meter instances look like "RAPL_Package0_PKG", "RAPL_Package0_PP0" (cores) and
    /// "..._DRAM"; the package already includes the cores, so only packages are summed. Power
    /// Meter reports the whole system ("_Total"). Both are in milliwatts.
    /// </summary>
    internal static double? PickPower(IReadOnlyList<(string Instance, double Value)> energy, IReadOnlyList<(string Instance, double Value)> meter)
    {
        var packages = energy.Where(e => e.Instance.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase)).ToList();
        double milliwatts;
        if (packages.Count > 0)
        {
            milliwatts = packages.Sum(p => p.Value);
        }
        else if (meter.FirstOrDefault(m => m.Instance == "_Total") is { Instance: not null } total)
        {
            milliwatts = total.Value;
        }
        else if (meter.Count > 0)
        {
            milliwatts = meter.Sum(m => m.Value);
        }
        else
        {
            return null;
        }

        double watts = milliwatts / 1000;
        return watts is > 0 and < MaxWatts ? watts : null;
    }

    /// <summary>The hottest zone, in °C. Desktops often report a fixed placeholder; it is shown as read.</summary>
    internal static double? PickTemperature(IReadOnlyList<(string Instance, double Value)> zones, bool tenthsOfKelvin)
    {
        double? hottest = null;
        foreach (var (_, value) in zones)
        {
            double celsius = (tenthsOfKelvin ? value / 10 : value) - 273.15;
            if (celsius is >= MinCelsius and <= MaxCelsius && (hottest is null || celsius > hottest))
            {
                hottest = celsius;
            }
        }

        return hottest;
    }

    private static List<(string Instance, double Value)> Read(nint counter)
    {
        var values = new List<(string, double)>();
        if (counter == 0)
        {
            return values;
        }

        uint size = 0;
        if (Pdh.GetFormattedCounterArray(counter, Pdh.FmtDouble, ref size, out _, null) != Pdh.MoreData || size == 0)
        {
            return values;
        }

        byte* buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            if (Pdh.GetFormattedCounterArray(counter, Pdh.FmtDouble, ref size, out uint count, buffer) == 0)
            {
                var items = (Pdh.CounterItem*)buffer;
                for (int i = 0; i < count; i++)
                {
                    if (items[i].Status == 0)
                    {
                        values.Add((new string(items[i].Name), items[i].Value));
                    }
                }
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }

        return values;
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
