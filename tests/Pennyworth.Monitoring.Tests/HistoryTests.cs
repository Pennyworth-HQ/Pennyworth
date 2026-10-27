using Pennyworth.Monitoring.History;
using Pennyworth.Monitoring.Logs;

namespace Pennyworth.Monitoring.Tests;

public class HistoryTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 14, 0, 0, TimeSpan.Zero);

    private static MonitorSample Sample(double cpu, double memoryPercent, double? disk = null, string topApp = "chrome") =>
        new(new MemoryStatus(16 * Gb, (long)(16 * Gb * (1 - (memoryPercent / 100))), 0, 0), cpu, null,
            [new AppGroup(topApp, null, null, null, null, 2 * Gb, cpu, [])], disk);

    private static TelemetryPoint Point(int minute, double cpuPeak, double cpuAverage = 10, string? app = "chrome") =>
        new(T0.AddMinutes(minute), new Reading(cpuAverage, cpuPeak), new Reading(50, 55), new Reading(5, 8), null, app, app);

    [Fact]
    public void Recorder_folds_seconds_into_a_minute_and_keeps_the_peak()
    {
        var recorder = new TelemetryRecorder();
        Assert.Null(recorder.Add(Sample(10, 50, disk: 0), T0.AddSeconds(5)));
        Assert.Null(recorder.Add(Sample(90, 50, disk: 100, topApp: "MsMpEng"), T0.AddSeconds(6)));
        Assert.Null(recorder.Add(Sample(20, 50, disk: 20), T0.AddSeconds(7)));

        TelemetryPoint minute = recorder.Add(Sample(5, 50), T0.AddMinutes(1).AddSeconds(1))!;

        Assert.Equal(T0, minute.At);
        Assert.Equal(40, minute.Cpu.Average, 3);
        Assert.Equal(90, minute.Cpu.Peak);
        Assert.Equal(100, minute.Disk!.Value.Peak);
        Assert.Null(minute.Gpu);
        Assert.Equal("MsMpEng", minute.TopCpuApp);
        Assert.Equal(T0.AddMinutes(1), recorder.Current!.At);
    }

    [Fact]
    public void Store_round_trips_points_and_skips_damaged_lines()
    {
        string folder = Path.Combine(Path.GetTempPath(), "pennyworth-telemetry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TelemetryStore(folder);
            var point = new TelemetryPoint(T0, new Reading(12.5, 98), new Reading(61, 63), null, new Reading(4, 100), "Google\tChrome", null);
            store.Append(point);
            File.AppendAllText(Path.Combine(folder, "2026-10-27.tsv"), "garbage\n2026-10-27T14:05:00Z\t1");
            store.Append(point with { At = T0.AddDays(1) });

            List<TelemetryPoint> loaded = store.Load(T0.AddMinutes(-1));

            Assert.Equal(2, loaded.Count);
            Assert.Equal(point with { TopCpuApp = "Google Chrome" }, loaded[0]);
            Assert.Single(store.Load(T0.AddHours(1)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Store_prunes_days_older_than_a_week()
    {
        string folder = Path.Combine(Path.GetTempPath(), "pennyworth-telemetry-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TelemetryStore(folder);
            store.Append(Point(0, 10) with { At = T0.AddDays(-9) });
            store.Append(Point(0, 10));

            store.Prune(T0);

            Assert.Single(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Spikes_join_nearby_minutes_and_ignore_a_busy_baseline()
    {
        var points = Enumerable.Range(0, 60).Select(m => Point(m, 15)).ToList();
        points[10] = Point(10, 100, app: "MsMpEng");
        points[11] = Point(11, 95, app: "MsMpEng");
        points[13] = Point(13, 92, app: "chrome");
        points[40] = Point(40, 99, app: "game");

        List<Spike> spikes = SpikeFinder.Find(points, Metric.Cpu);

        Assert.Equal(2, spikes.Count);
        Assert.Equal(T0.AddMinutes(10), spikes[0].Start);
        Assert.Equal(T0.AddMinutes(14), spikes[0].End);
        Assert.Equal(100, spikes[0].Peak);
        Assert.Equal("MsMpEng", spikes[0].TopApp);
        Assert.Equal(10, spikes[0].Baseline);

        // A PC that is always at 90% has no "spikes": that is its normal.
        var busy = Enumerable.Range(0, 30).Select(m => Point(m, 95, cpuAverage: 85)).ToList();
        Assert.Empty(SpikeFinder.Find(busy, Metric.Cpu));
    }

    [Fact]
    public void Summary_counts_minutes_at_the_limit()
    {
        var points = new List<TelemetryPoint> { Point(0, 20), Point(1, 95), Point(2, 91) };

        MetricSummary summary = SpikeFinder.Summarize(points, Metric.Cpu)!;

        Assert.Equal(2, summary.MinutesAbove);
        Assert.Equal(95, summary.Peak);
        Assert.Null(SpikeFinder.Summarize(points, Metric.Gpu));
    }

    [Fact]
    public void Windows_events_are_parsed_from_rendered_xml()
    {
        const string xml = """
            ﻿<Events>
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-Disk' EventSourceName='disk'/><EventID Qualifiers='32772'>153</EventID><Level>3</Level><TimeCreated SystemTime='2026-10-27T14:32:10.123Z'/><Channel>System</Channel></System><EventData><Data>\Device\Harddisk0\DR0</Data></EventData><RenderingInfo Culture='en-US'><Message>The IO operation at logical block address 0x1 for Disk 0 was retried.</Message></RenderingInfo></Event>
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Application Error'/><EventID>1000</EventID><Level>2</Level><TimeCreated SystemTime='2026-10-27T14:33:00Z'/><Channel>Application</Channel></System><EventData><Data>game.exe</Data><Data>1.0</Data></EventData></Event>
            <Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Level>2</Level></System></Event>
            </Events>
            """;

        List<SystemLogEntry> entries = WindowsEventLogReader.Parse(xml);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new SystemLogEntry(new DateTimeOffset(2026, 10, 27, 14, 32, 10, 123, TimeSpan.Zero), LogSeverity.Warning, "disk", "153",
            "The IO operation at logical block address 0x1 for Disk 0 was retried.", "System"), entries[0]);
        Assert.Equal("game.exe · 1.0", entries[1].Message);
        Assert.Equal(LogSeverity.Error, entries[1].Severity);
        Assert.Empty(WindowsEventLogReader.Parse("not xml"));
    }

    [Fact]
    public void Journal_lines_are_parsed_including_byte_array_messages()
    {
        const string json = """
            {"__REALTIME_TIMESTAMP":"1793111530000000","PRIORITY":"3","SYSLOG_IDENTIFIER":"kernel","MESSAGE":"nvme0: I/O 12 QID 3 timeout, aborting"}
            {"__REALTIME_TIMESTAMP":"1793111531000000","PRIORITY":"4","_COMM":"pulseaudio","MESSAGE":[104,105]}
            {"__REALTIME_TIMESTAMP":"17931115
            """;

        List<SystemLogEntry> entries = JournalLogReader.Parse(json);

        Assert.Equal(2, entries.Count);
        Assert.Equal(LogSeverity.Error, entries[0].Severity);
        Assert.Equal("kernel", entries[0].Source);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793111530), entries[0].At);
        Assert.Equal("hi", entries[1].Message);
        Assert.Equal("pulseaudio", entries[1].Source);
    }

    [Fact]
    public void Digest_groups_repeats_and_hides_account_sids()
    {
        SystemLogEntry Entry(int minute, string source, string? id, string message, LogSeverity severity = LogSeverity.Warning) =>
            new(T0.AddMinutes(minute), severity, source, id, message, "System");

        List<LogGroup> groups = LogDigest.Group(
        [
            Entry(0, "disk", "153", "retried 1"),
            Entry(5, "disk", "153", "retried 2"),
            Entry(1, "DCOM", "10016", "to the user PC\\ana SID (S-1-5-21-2843311234-802977034-4049983454-1000)"),
            Entry(2, "kernel", null, "nvme0: I/O 12 timeout", LogSeverity.Error),
            Entry(3, "kernel", null, "nvme0: I/O 99 timeout", LogSeverity.Error),
        ]);

        Assert.Equal(3, groups.Count);
        Assert.Equal(("kernel", 2), (groups[0].Source, groups[0].Count));
        Assert.Equal(("disk", 2, "retried 2"), (groups[1].Source, groups[1].Count, groups[1].Message));
        Assert.Equal(T0, groups[1].First);
        Assert.Contains("<sid>", groups[2].Message);
        Assert.DoesNotContain("2843311234", groups[2].Message);
        Assert.Equal(LogDigest.MaxMessage, LogDigest.Shorten(new string('x', 1000)).Length);
    }
}
