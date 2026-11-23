namespace Pennyworth.Monitoring.History;

/// <summary>What the history charts and spike finder look at.</summary>
public enum Metric
{
    Cpu,
    Memory,
    Disk,
    Gpu,
}

/// <summary>One metric over one minute: the average and the highest second, both 0 to 100.</summary>
public readonly record struct Reading(double Average, double Peak);

/// <summary>
/// One minute of the PC's life, as the history keeps it. Seconds are folded into the minute so a
/// week fits in a few hundred kilobytes, but the peak second is kept: short spikes are the point.
/// </summary>
/// <param name="At">The start of the minute, in UTC.</param>
/// <param name="Disk">Null when the disk counter is unavailable.</param>
/// <param name="Gpu">Null when the GPU counters are unavailable.</param>
/// <param name="TopCpuApp">The app using the most CPU in the busiest second.</param>
/// <param name="TopMemoryApp">The app using the most memory when memory was fullest.</param>
public sealed record TelemetryPoint(
    DateTimeOffset At,
    Reading Cpu,
    Reading Memory,
    Reading? Disk,
    Reading? Gpu,
    string? TopCpuApp,
    string? TopMemoryApp)
{
    public Reading? Get(Metric metric) => metric switch
    {
        Metric.Cpu => Cpu,
        Metric.Memory => Memory,
        Metric.Disk => Disk,
        _ => Gpu,
    };

    /// <summary>The app most likely behind a spike of <paramref name="metric"/>.</summary>
    public string? TopApp(Metric metric) => metric == Metric.Memory ? TopMemoryApp : TopCpuApp;
}
