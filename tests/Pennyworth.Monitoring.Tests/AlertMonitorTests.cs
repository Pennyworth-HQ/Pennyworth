using Pennyworth.Monitoring.Alerts;

namespace Pennyworth.Monitoring.Tests;

public class AlertMonitorTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    private static readonly AlertRule Cpu = new("cpu", AlertComponent.Cpu, 90);

    private static IReadOnlyList<UsageAlert> At(AlertMonitor monitor, int second, double cpu, params AlertRule[] rules) =>
        monitor.Evaluate(rules.Length == 0 ? [Cpu] : rules, [new UsageReading(AlertComponent.Cpu, cpu)], Start.AddSeconds(second));

    [Fact]
    public void Fires_only_after_the_value_stays_above_the_limit()
    {
        var monitor = new AlertMonitor(sustain: TimeSpan.FromSeconds(10));

        for (int s = 0; s < 10; s++)
        {
            Assert.Empty(At(monitor, s, 95));
        }

        UsageAlert alert = Assert.Single(At(monitor, 10, 97));
        Assert.Equal(Cpu, alert.Rule);
        Assert.Equal(97, alert.Value);
    }

    [Fact]
    public void A_short_spike_does_not_fire()
    {
        var monitor = new AlertMonitor(sustain: TimeSpan.FromSeconds(10));

        At(monitor, 0, 100);
        At(monitor, 5, 100);
        At(monitor, 6, 40);

        Assert.Empty(At(monitor, 12, 100));
    }

    [Fact]
    public void Exactly_at_the_limit_is_not_above_it()
    {
        var monitor = new AlertMonitor(sustain: TimeSpan.Zero);

        Assert.Empty(At(monitor, 0, 90));
        Assert.Single(At(monitor, 1, 90.5));
    }

    [Fact]
    public void Fires_once_while_the_value_stays_high()
    {
        var monitor = new AlertMonitor(sustain: TimeSpan.Zero, cooldown: TimeSpan.FromMinutes(1));

        Assert.Single(At(monitor, 0, 95));
        for (int s = 1; s < 3600; s += 30)
        {
            Assert.Empty(At(monitor, s, 95));
        }
    }

    [Fact]
    public void Fires_again_after_calming_down_and_resting()
    {
        var monitor = new AlertMonitor(sustain: TimeSpan.Zero, cooldown: TimeSpan.FromMinutes(1), hysteresis: 5);

        Assert.Single(At(monitor, 0, 95));
        At(monitor, 10, 87); // Below the limit but not by 5 points: still the same episode.
        Assert.Empty(At(monitor, 70, 95));

        At(monitor, 80, 50); // Calm.
        Assert.Single(At(monitor, 140, 95));
    }

    [Fact]
    public void Waits_for_the_cooldown_even_after_calming_down()
    {
        var monitor = new AlertMonitor(sustain: TimeSpan.Zero, cooldown: TimeSpan.FromMinutes(10));

        Assert.Single(At(monitor, 0, 95));
        At(monitor, 1, 10);
        Assert.Empty(At(monitor, 2, 95));
        Assert.Single(At(monitor, 601, 95));
    }

    [Fact]
    public void Turned_off_rules_never_fire()
    {
        var monitor = new AlertMonitor(sustain: TimeSpan.Zero);

        Assert.Empty(At(monitor, 0, 100, Cpu with { Enabled = false }));
    }

    [Fact]
    public void Every_rule_and_drive_is_watched_on_its_own()
    {
        var monitor = new AlertMonitor(sustain: TimeSpan.Zero);
        AlertRule disk = new("disk", AlertComponent.Disk, 90);
        AlertRule custom = new("mine", AlertComponent.Disk, 80, Message: "Clean up!");
        UsageReading[] readings = [new(AlertComponent.Disk, 85, @"C:\"), new(AlertComponent.Disk, 95, @"D:\")];

        var fired = monitor.Evaluate([disk, custom], readings, Start);

        Assert.Equal(3, fired.Count);
        Assert.Contains(fired, a => a.Rule == disk && a.Target == @"D:\");
        Assert.Contains(fired, a => a.Rule == custom && a.Target == @"C:\");
        Assert.Contains(fired, a => a.Rule == custom && a.Target == @"D:\");
    }

    [Fact]
    public void Readings_cover_every_part_the_sample_reports()
    {
        var sample = new MonitorSample(new MemoryStatus(Total: 16, Available: 4, Cached: 0, Compressed: 0), 42, 77, []);

        var readings = AlertMonitor.Readings(sample, [(@"C:\", 91.5)]);

        Assert.Contains(new UsageReading(AlertComponent.Cpu, 42), readings);
        Assert.Contains(new UsageReading(AlertComponent.Memory, 75), readings);
        Assert.Contains(new UsageReading(AlertComponent.Gpu, 77), readings);
        Assert.Contains(new UsageReading(AlertComponent.Disk, 91.5, @"C:\"), readings);
    }

    [Fact]
    public void Parts_the_pc_does_not_report_are_left_out()
    {
        var sample = new MonitorSample(new MemoryStatus(0, 0, 0, 0), 10, null, []);

        var readings = AlertMonitor.Readings(sample, []);

        Assert.Equal([new UsageReading(AlertComponent.Cpu, 10)], readings);
    }
}
