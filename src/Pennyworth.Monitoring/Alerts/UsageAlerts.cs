namespace Pennyworth.Monitoring.Alerts;

/// <summary>What an alert watches, as a percentage of use.</summary>
public enum AlertComponent
{
    Cpu,
    Memory,
    Gpu,
    Disk,
}

/// <summary>
/// "Tell me when <paramref name="Component"/> goes above <paramref name="Threshold"/>". A
/// <paramref name="Message"/> replaces the text Pennyworth would write.
/// </summary>
public sealed record AlertRule(string Id, AlertComponent Component, double Threshold, bool Enabled = true, string? Message = null)
{
    public const double DefaultThreshold = 90;

    /// <summary>What a new install watches: processor, memory, graphics card and disk space above 90%.</summary>
    public static IReadOnlyList<AlertRule> Defaults { get; } =
    [
        new("cpu", AlertComponent.Cpu, DefaultThreshold),
        new("memory", AlertComponent.Memory, DefaultThreshold),
        new("gpu", AlertComponent.Gpu, DefaultThreshold),
        new("disk", AlertComponent.Disk, DefaultThreshold),
    ];
}

/// <summary>One measured value. <paramref name="Target"/> tells parts with several instances apart (a drive root).</summary>
public readonly record struct UsageReading(AlertComponent Component, double Value, string? Target = null);

/// <summary>A rule that just went off.</summary>
public sealed record UsageAlert(AlertRule Rule, double Value, string? Target, DateTimeOffset At);

/// <summary>
/// Decides when a rule goes off. A value has to stay above the threshold for
/// <see cref="Sustain"/> (a processor spikes to 100% for a moment all the time), then the rule
/// fires once and stays quiet until the value falls <see cref="Hysteresis"/> below the threshold.
/// Even then it waits <see cref="Cooldown"/> before firing again, so a value hovering around the
/// line does not flood the person with notifications.
/// </summary>
public sealed class AlertMonitor
{
    public static readonly TimeSpan DefaultSustain = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(10);
    public const double DefaultHysteresis = 5;

    private readonly Dictionary<(string Rule, string? Target), State> _states = [];

    public AlertMonitor(TimeSpan? sustain = null, TimeSpan? cooldown = null, double hysteresis = DefaultHysteresis)
    {
        Sustain = sustain ?? DefaultSustain;
        Cooldown = cooldown ?? DefaultCooldown;
        Hysteresis = hysteresis;
    }

    public TimeSpan Sustain { get; }

    public TimeSpan Cooldown { get; }

    public double Hysteresis { get; }

    /// <summary>Call with every new set of readings; returns the rules that went off now.</summary>
    public IReadOnlyList<UsageAlert> Evaluate(IReadOnlyList<AlertRule> rules, IReadOnlyList<UsageReading> readings, DateTimeOffset now)
    {
        List<UsageAlert> fired = [];
        HashSet<(string, string?)> seen = [];
        foreach (AlertRule rule in rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            foreach (UsageReading reading in readings)
            {
                if (reading.Component != rule.Component)
                {
                    continue;
                }

                var key = (rule.Id, reading.Target);
                seen.Add(key);
                if (!_states.TryGetValue(key, out State? state))
                {
                    _states[key] = state = new State();
                }

                if (reading.Value > rule.Threshold)
                {
                    state.AboveSince ??= now;
                    bool sustained = now - state.AboveSince.Value >= Sustain;
                    bool rested = state.LastFired is not { } last || now - last >= Cooldown;
                    if (state.Armed && sustained && rested)
                    {
                        state.Armed = false;
                        state.LastFired = now;
                        fired.Add(new UsageAlert(rule, reading.Value, reading.Target, now));
                    }
                }
                else
                {
                    state.AboveSince = null;
                    if (reading.Value <= rule.Threshold - Hysteresis)
                    {
                        state.Armed = true;
                    }
                }
            }
        }

        // Forget rules that were removed or turned off, and drives that went away.
        foreach (var key in _states.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _states.Remove(key);
        }

        return fired;
    }

    /// <summary>The values the rules can watch, from one monitor sample and the drives' usage.</summary>
    public static IReadOnlyList<UsageReading> Readings(MonitorSample sample, IEnumerable<(string Root, double UsedPercent)> drives)
    {
        List<UsageReading> readings = [new(AlertComponent.Cpu, sample.CpuPercent)];
        if (sample.Memory.Total > 0)
        {
            readings.Add(new(AlertComponent.Memory, 100.0 * sample.Memory.Used / sample.Memory.Total));
        }

        if (sample.GpuPercent is { } gpu)
        {
            readings.Add(new(AlertComponent.Gpu, gpu));
        }

        readings.AddRange(drives.Select(d => new UsageReading(AlertComponent.Disk, d.UsedPercent, d.Root)));
        return readings;
    }

    private sealed class State
    {
        public DateTimeOffset? AboveSince;
        public DateTimeOffset? LastFired;
        public bool Armed = true;
    }
}
