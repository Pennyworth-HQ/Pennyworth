using Pennyworth.Monitoring.History;
using Pennyworth.Monitoring.Logs;
using static Pennyworth.Assistant.Tests.Samples;

namespace Pennyworth.Assistant.Tests;

public class HistoryContextTests
{
    private static readonly DiagnosisEngine Engine = DiagnosisEngine.FromKnowledgeBase();
    private static readonly DateTimeOffset At = new(2026, 10, 27, 14, 32, 0, TimeSpan.Zero);

    private static HistoryReport Report(IReadOnlyList<LogGroup>? logs = null) => new(
        TimeSpan.FromHours(24),
        TimeSpan.FromMinutes(372),
        [new MetricSummary(Metric.Disk, 12, 100, 5, 4, 372)],
        [new Spike(Metric.Disk, At, At.AddMinutes(3), 100, 5, "MsMpEng")],
        logs,
        "Windows Event Log");

    private static ContextBundle Build(AssistantTopic topic, HistoryReport? report, System.Globalization.CultureInfo culture)
    {
        PcSnapshot snapshot = Snapshot() with { History = report };
        var facts = FactCollector.Collect(snapshot, culture);
        return ContextBuilder.Build(topic, facts, Engine.Evaluate(facts, culture), snapshot, culture);
    }

    [Fact]
    public void History_questions_lead_with_the_range_metrics_and_spikes()
    {
        var bundle = Build(new AssistantTopic(TopicKind.History), Report([new LogGroup("disk", "153", LogSeverity.Warning, 3, At, At.AddMinutes(1), "retried", "System")]), En);

        Assert.Equal("Range analysed", bundle.Facts[0].Label);
        Assert.StartsWith("last 24 h; Pennyworth recorded 6 h 12 min", bundle.Facts[0].Value);
        Assert.Equal("disk (active time) over the range", bundle.Facts[1].Label);
        Assert.Equal("disk (active time) spike", bundle.Facts[2].Label);
        Assert.Contains("(3 min): 100%, usual 5%; app using most CPU: MsMpEng", bundle.Facts[2].Value);
        Assert.DoesNotContain(bundle.Facts, f => f.Value.Contains("retried", StringComparison.Ordinal)); // logs only when asked
    }

    [Fact]
    public void Logs_come_after_the_spikes_when_the_person_read_them()
    {
        var logs = new[] { new LogGroup("disk", "153", LogSeverity.Warning, 3, At, At.AddMinutes(1), "The IO operation was retried.", "System") };

        var bundle = Build(new AssistantTopic(TopicKind.History, AssistantTopic.LogsKey), Report(logs), Pt);

        ContextLine log = bundle.Facts.Single(f => f.Label.StartsWith("Windows Event Log", StringComparison.Ordinal));
        Assert.Equal("Windows Event Log: Aviso (disk 153, System)", log.Label);
        Assert.StartsWith("The IO operation was retried. [3×, de ", log.Value);
        Assert.Contains("Relacione os picos com os registros do log", PersonaPrompt.Compose(bundle));
    }

    [Fact]
    public void Without_a_history_the_assistant_is_told_nothing_was_recorded()
    {
        var bundle = Build(new AssistantTopic(TopicKind.History, AssistantTopic.LogsKey), null, En);

        Assert.Equal(new ContextLine("Usage history", "nothing recorded yet"), bundle.Facts[0]);
    }
}
