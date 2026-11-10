namespace Pennyworth.Monitoring.History;

/// <summary>
/// One episode of heavy use: consecutive minutes where a metric peaked well above its usual level.
/// </summary>
/// <param name="Start">The first minute, UTC.</param>
/// <param name="End">The end of the last minute, UTC.</param>
/// <param name="Peak">The highest second, 0 to 100.</param>
/// <param name="Baseline">The usual level over the whole range (median of the minute averages).</param>
/// <param name="TopApp">The app most often on top during the spike, if known.</param>
public sealed record Spike(Metric Metric, DateTimeOffset Start, DateTimeOffset End, double Peak, double Baseline, string? TopApp)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Per-metric numbers over a range, for the cards and the assistant.</summary>
/// <param name="MinutesAbove">Minutes whose peak reached <see cref="SpikeFinder.ThresholdOf"/>.</param>
public sealed record MetricSummary(Metric Metric, double Average, double Peak, double Baseline, int MinutesAbove, int Minutes);

/// <summary>Finds the intermittent bottlenecks the history exists for.</summary>
public static class SpikeFinder
{
    /// <summary>Minutes apart that still count as the same episode.</summary>
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(2);

    /// <summary>How far above the usual level a peak must be, in percentage points.</summary>
    private const double MinRise = 25;

    /// <summary>The level that counts as "maxed out" for a metric.</summary>
    public static double ThresholdOf(Metric metric) => metric switch
    {
        Metric.Memory => 90,
        Metric.Gpu => 95,
        _ => 90,
    };

    public static MetricSummary? Summarize(IReadOnlyList<TelemetryPoint> points, Metric metric)
    {
        var readings = points.Select(p => p.Get(metric)).OfType<Reading>().ToList();
        if (readings.Count == 0)
        {
            return null;
        }

        double threshold = ThresholdOf(metric);
        return new MetricSummary(
            metric,
            readings.Average(r => r.Average),
            readings.Max(r => r.Peak),
            Median(readings.Select(r => r.Average)),
            readings.Count(r => r.Peak >= threshold),
            readings.Count);
    }

    /// <summary>Every spike of every metric, most recent first.</summary>
    public static List<Spike> Find(IReadOnlyList<TelemetryPoint> points) =>
        [.. Enum.GetValues<Metric>().SelectMany(metric => Find(points, metric)).OrderByDescending(s => s.Start)];

    public static List<Spike> Find(IReadOnlyList<TelemetryPoint> points, Metric metric)
    {
        var spikes = new List<Spike>();
        var withReading = points.Where(p => p.Get(metric) is not null).ToList();
        if (withReading.Count == 0)
        {
            return spikes;
        }

        double baseline = Median(withReading.Select(p => p.Get(metric)!.Value.Average));
        double threshold = ThresholdOf(metric);
        var episode = new List<TelemetryPoint>();

        void Close()
        {
            if (episode.Count == 0)
            {
                return;
            }

            string? app = episode.Select(p => p.TopApp(metric)).OfType<string>()
                .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();
            spikes.Add(new Spike(
                metric,
                episode[0].At,
                episode[^1].At.AddMinutes(1),
                episode.Max(p => p.Get(metric)!.Value.Peak),
                baseline,
                app));
            episode.Clear();
        }

        foreach (TelemetryPoint point in withReading)
        {
            Reading reading = point.Get(metric)!.Value;
            bool hot = reading.Peak >= threshold && reading.Peak - baseline >= MinRise;
            if (!hot)
            {
                continue;
            }

            if (episode.Count > 0 && point.At - episode[^1].At > MaxGap)
            {
                Close();
            }

            episode.Add(point);
        }

        Close();
        return spikes;
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] sorted = [.. values.Order()];
        if (sorted.Length == 0)
        {
            return 0;
        }

        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
