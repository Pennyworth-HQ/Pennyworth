using Pennyworth.App.ViewModels;
using Pennyworth.Assistant;
using Pennyworth.Monitoring.History;
using Pennyworth.Monitoring.Logs;

namespace Pennyworth.App.Tests;

public class HistoryViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);

    private sealed class FakeLogs(params SystemLogEntry[] entries) : ISystemLogReader
    {
        public DateTimeOffset? Since { get; private set; }

        public bool Fail { get; init; }

        public string SourceName => "Test log";

        public Task<IReadOnlyList<SystemLogEntry>> ReadAsync(DateTimeOffset since, CancellationToken cancellationToken)
        {
            Since = since;
            return Fail ? Task.FromException<IReadOnlyList<SystemLogEntry>>(new InvalidOperationException()) : Task.FromResult<IReadOnlyList<SystemLogEntry>>(entries);
        }
    }

    private static TelemetryPoint Point(DateTimeOffset at, double diskPeak) =>
        new(at, new Reading(10, 20), new Reading(50, 55), new Reading(4, diskPeak), null, "MsMpEng", "chrome");

    private static (HistoryViewModel History, string Folder) Create(ISystemLogReader? logs = null)
    {
        string folder = Path.Combine(Path.GetTempPath(), "pennyworth-history-" + Guid.NewGuid().ToString("N"));
        var store = new TelemetryStore(folder);
        for (int m = 0; m < 120; m++)
        {
            DateTimeOffset at = Now.AddHours(-3).AddMinutes(m);
            store.Append(Point(at, m is 30 or 31 ? 100 : 8));
        }

        store.Append(Point(Now.AddDays(-3), 100)); // only in the 7-day range
        return (new HistoryViewModel(store, logs, () => Now), folder);
    }

    [Fact]
    public async Task Loads_the_history_into_charts_and_spikes()
    {
        var (history, folder) = Create();
        try
        {
            await history.EnsureLoaded();

            Assert.True(history.HasData);
            Assert.Equal([Metric.Cpu, Metric.Memory, Metric.Disk], history.Charts.Select(c => c.Metric));
            Assert.Equal(288, history.Charts[0].Peaks.Count);
            Assert.True(double.IsNaN(history.Charts[0].Peaks[0])); // nothing recorded 24 h ago
            SpikeRowViewModel spike = Assert.Single(history.Spikes);
            Assert.Equal(Metric.Disk, spike.Spike.Metric);
            Assert.Equal(TimeSpan.FromMinutes(120), history.Recorded);

            history.ShowWeekCommand.Execute(null);

            Assert.Equal(2, history.Spikes.Count);
            Assert.Equal(336, history.Charts[0].Peaks.Count);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task New_minutes_show_up_without_reloading()
    {
        var (history, folder) = Create();
        try
        {
            await history.EnsureLoaded();

            history.Add(Point(Now.AddMinutes(-1), 8));

            Assert.Equal(TimeSpan.FromMinutes(121), history.Recorded);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Reads_the_system_log_for_the_range_and_hands_it_to_the_assistant()
    {
        var logs = new FakeLogs(
            new SystemLogEntry(Now.AddHours(-2), LogSeverity.Warning, "disk", "153", "retried", "System"),
            new SystemLogEntry(Now.AddHours(-1), LogSeverity.Warning, "disk", "153", "retried", "System"));
        var (history, folder) = Create(logs);
        var asked = new List<(AssistantTopic Topic, string Question)>();
        history.AskAbout = (topic, question) => asked.Add((topic, question));
        try
        {
            await history.EnsureLoaded();
            Assert.False(history.CanAskAboutLogs);

            history.ReadLogsCommand.Execute(null);
            await history.LogReading;

            Assert.Equal(Now.AddHours(-24), logs.Since);
            LogRowViewModel row = Assert.Single(history.LogRows);
            Assert.Equal("disk 153 · System", row.SourceText);
            Assert.True(history.CanAskAboutLogs);

            history.AskAboutLogsCommand.Execute(null);
            history.AskAboutSpikesCommand.Execute(null);

            Assert.Equal(new AssistantTopic(TopicKind.History, AssistantTopic.LogsKey), asked[0].Topic);
            Assert.Equal(new AssistantTopic(TopicKind.History), asked[1].Topic);
            HistoryReport report = history.Report();
            Assert.Equal(2, Assert.Single(report.Logs!).Count);
            Assert.Equal("Test log", report.LogSource);

            history.ShowWeekCommand.Execute(null);

            Assert.Empty(history.LogRows); // read again for the new range
            Assert.Null(history.Report().Logs);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_log_that_cannot_be_read_says_so()
    {
        var (history, folder) = Create(new FakeLogs { Fail = true });
        try
        {
            history.ReadLogsCommand.Execute(null);
            await history.LogReading;

            Assert.Equal(LogState.Failed, history.LogState);
            Assert.True(history.HasLogStatus);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
