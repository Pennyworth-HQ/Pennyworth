namespace Pennyworth.Monitoring.History;

/// <summary>Folds the once-per-second samples into one <see cref="TelemetryPoint"/> per minute.</summary>
public sealed class TelemetryRecorder
{
    private DateTimeOffset? _minute;
    private Accumulator _cpu;
    private Accumulator _memory;
    private Accumulator _disk;
    private Accumulator _gpu;
    private string? _topCpuApp;
    private string? _topMemoryApp;

    /// <summary>
    /// Adds one sample. Returns the previous minute when <paramref name="now"/> starts a new one,
    /// otherwise null. A minute with a gap (the PC slept) is still returned with what it saw.
    /// </summary>
    public TelemetryPoint? Add(MonitorSample sample, DateTimeOffset now)
    {
        DateTimeOffset minute = StartOfMinute(now);
        TelemetryPoint? finished = null;
        if (_minute is { } current && current != minute)
        {
            finished = Build(current);
            Reset();
        }

        _minute = minute;
        double memory = sample.Memory.Total == 0 ? 0 : 100.0 * sample.Memory.Used / sample.Memory.Total;
        if (_cpu.Add(sample.CpuPercent))
        {
            _topCpuApp = NameOf(sample.Apps.MaxBy(a => a.Cpu));
        }

        if (_memory.Add(memory))
        {
            _topMemoryApp = NameOf(sample.Apps.MaxBy(a => a.Memory));
        }

        if (sample.DiskPercent is { } disk)
        {
            _disk.Add(disk);
        }

        if (sample.GpuPercent is { } gpu)
        {
            _gpu.Add(gpu);
        }

        return finished;
    }

    /// <summary>The minute being recorded, so far; null before the first sample.</summary>
    public TelemetryPoint? Current => _minute is { } minute ? Build(minute) : null;

    public static DateTimeOffset StartOfMinute(DateTimeOffset at)
    {
        DateTimeOffset utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }

    /// <summary>The name a person recognises: the product description, else the executable.</summary>
    public static string? NameOf(AppGroup? app) =>
        app is null ? null : app.Explanation?.Title.Get(System.Globalization.CultureInfo.InvariantCulture) ?? app.Description ?? app.Key;

    private TelemetryPoint Build(DateTimeOffset minute) => new(
        minute,
        _cpu.Reading ?? default,
        _memory.Reading ?? default,
        _disk.Reading,
        _gpu.Reading,
        _topCpuApp,
        _topMemoryApp);

    private void Reset()
    {
        _cpu = default;
        _memory = default;
        _disk = default;
        _gpu = default;
        _topCpuApp = null;
        _topMemoryApp = null;
    }

    private struct Accumulator
    {
        private double _sum;
        private int _count;
        private double _peak;

        public readonly Reading? Reading => _count == 0 ? null : new Reading(_sum / _count, _peak);

        /// <summary>Adds a value; true when it is the new peak.</summary>
        public bool Add(double value)
        {
            value = Math.Clamp(value, 0, 100);
            _sum += value;
            _count++;
            if (_count == 1 || value >= _peak)
            {
                _peak = value;
                return true;
            }

            return false;
        }
    }
}
