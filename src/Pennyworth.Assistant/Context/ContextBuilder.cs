using System.Globalization;
using System.Text;
using Pennyworth.Cleanup;
using Pennyworth.Core.Formatting;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.History;
using Pennyworth.Monitoring.Logs;

namespace Pennyworth.Assistant;

public sealed record ContextLine(string Label, string Value);

/// <summary>
/// What the model is told about this PC for one question. <see cref="ToText"/> is exactly what
/// goes into the prompt, and what the send preview shows before a cloud request.
/// </summary>
public sealed record ContextBundle(
    AssistantTopic Topic,
    CultureInfo Culture,
    IReadOnlyList<ContextLine> Facts,
    IReadOnlyList<string> Findings,
    IReadOnlyList<AssistantAction> AllowedActions)
{
    public bool IsPortuguese => Culture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase);

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(IsPortuguese ? "FATOS medidos pelo Pennyworth neste PC:" : "FACTS measured by Pennyworth on this PC:");
        foreach (ContextLine line in Facts)
        {
            sb.Append("- ").Append(line.Label).Append(": ").AppendLine(line.Value);
        }

        // Phase 1 evaluation: without this line small models "answered" these from general knowledge.
        sb.AppendLine();
        sb.AppendLine(IsPortuguese
            ? "NÃO MEDIDO pelo Pennyworth (diga que não consegue ver): temperaturas, ventoinhas, velocidade da internet, requisitos de jogos ou do Windows, preços."
            : "NOT MEASURED by Pennyworth (say you can't see these): temperatures, fans, internet speed, game or Windows requirements, prices.");

        if (Findings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(IsPortuguese ? "CONCLUSÕES das regras do Pennyworth:" : "FINDINGS from Pennyworth's rules:");
            foreach (string finding in Findings)
            {
                sb.Append("- ").AppendLine(finding);
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine(IsPortuguese ? "CONCLUSÕES: nada precisa de atenção agora." : "FINDINGS: nothing needs attention right now.");
        }

        return sb.ToString();
    }
}

/// <summary>Picks, orders and trims the facts and findings for one topic.</summary>
public static class ContextBuilder
{
    /// <summary>About 1,500 tokens; small local models must keep all of it in view.</summary>
    public const int MaxCharacters = 6_000;

    private const int MaxActions = 5;
    private const int MaxRecycleBinItems = 5;
    private const int MaxSpikes = 8;
    private const int MaxLogGroups = 12;

    /// <summary>The facts every answer may need, in this order, after the topic's own facts.</summary>
    private static readonly string[] KeyFacts =
    [
        "pc.kind", "os.name", "disk.system.used_percent", "disk.system.free", "disk.system.kind",
        "ram.total", "ram.used_percent", "ram.sustained_percent", "cpu.name", "cpu.percent",
        "gpu.name", "gpu.memory", "battery.percent", "cleanup.safe_bytes",
    ];

    public static ContextBundle Build(
        AssistantTopic topic,
        IReadOnlyList<Fact> facts,
        IReadOnlyList<Finding> findings,
        PcSnapshot snapshot,
        CultureInfo culture)
    {
        TopicKind group = GroupOf(topic);
        var lines = new List<ContextLine>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        void AddFact(Fact fact)
        {
            if (used.Add(fact.Id))
            {
                lines.Add(new ContextLine(fact.Label, fact.Format(culture)));
            }
        }

        if (topic.Kind == TopicKind.CleanupItem && topic.Key is { } ruleId)
        {
            foreach (Fact fact in facts.Where(f => f.Id.StartsWith($"cleanup.item.{ruleId}.", StringComparison.Ordinal)))
            {
                AddFact(fact);
            }

            lines.AddRange(CleanupItemLines(snapshot, ruleId, culture));
        }
        else if (topic.Kind == TopicKind.History)
        {
            lines.AddRange(HistoryLines(snapshot.History, topic.Key == AssistantTopic.LogsKey, culture));
        }
        else if (group != TopicKind.Overview)
        {
            foreach (Fact fact in facts.Where(f => f.Group == group))
            {
                AddFact(fact);
            }
        }

        var byId = facts.ToDictionary(f => f.Id, StringComparer.Ordinal);
        foreach (string id in KeyFacts)
        {
            if (byId.TryGetValue(id, out Fact? fact))
            {
                AddFact(fact);
            }
        }

        var ordered = findings
            .OrderByDescending(f => group != TopicKind.Overview && f.Group == group)
            .ThenByDescending(f => f.Severity)
            .ToList();

        var bundle = new ContextBundle(topic, culture, lines, [.. ordered.Select(f => f.Text)], AllowedActions(topic, group, ordered, snapshot));
        return Trim(bundle);
    }

    /// <summary>The topic a question is about, as one of the fact groups.</summary>
    public static TopicKind GroupOf(AssistantTopic topic) => topic.Kind switch
    {
        TopicKind.CleanupItem => TopicKind.Storage,
        TopicKind.SpecSection => topic.Key switch
        {
            "02" => TopicKind.Processor,
            "03" => TopicKind.Memory,
            "04" => TopicKind.Graphics,
            "05" => TopicKind.Storage,
            "08" => TopicKind.Power,
            _ => TopicKind.Overview,
        },
        _ => topic.Kind,
    };

    private static List<ContextLine> CleanupItemLines(PcSnapshot snapshot, string ruleId, CultureInfo culture)
    {
        var lines = new List<ContextLine>();
        bool pt = culture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        if (snapshot.Cleanup.FirstOrDefault(e => e.Rule.Id == ruleId) is not { } estimate)
        {
            return lines;
        }

        lines.Add(new ContextLine(pt ? "O que é" : "What it is", estimate.Rule.Description.Get(culture)));
        lines.Add(new ContextLine(pt ? "Se for removido" : "If removed", estimate.Rule.Consequence.Get(culture)));
        foreach (CleanupFile item in (estimate.Files ?? []).Where(f => f.OriginalPath is not null).Take(MaxRecycleBinItems))
        {
            // The full path in one piece, so the anonymiser hides personal file names too (phase 6 privacy review).
            lines.Add(new ContextLine(pt ? "Item na Lixeira" : "Item in the Recycle Bin", $"{item.OriginalPath} ({ByteSize.Format(item.Size, culture)})"));
        }

        return lines;
    }

    /// <summary>
    /// The usage history first, then the system log, so the model can line them up by time. Times
    /// are local, as the person saw them on the History screen.
    /// </summary>
    public static List<ContextLine> HistoryLines(HistoryReport? report, bool includeLogs, CultureInfo culture)
    {
        var lines = new List<ContextLine>();
        bool pt = culture.Name.StartsWith("pt", StringComparison.OrdinalIgnoreCase);
        if (report is null)
        {
            lines.Add(new ContextLine(pt ? "Histórico de uso" : "Usage history", pt ? "ainda não há dados gravados" : "nothing recorded yet"));
            return lines;
        }

        lines.Add(new ContextLine(
            pt ? "Período analisado" : "Range analysed",
            pt
                ? $"últimas {Duration(report.Range, pt)}; o Pennyworth gravou {Duration(report.Recorded, pt)} (só grava com o app aberto), um ponto por minuto com média e pico"
                : $"last {Duration(report.Range, pt)}; Pennyworth recorded {Duration(report.Recorded, pt)} (it only records while open), one point per minute with average and peak"));

        foreach (MetricSummary metric in report.Metrics)
        {
            string threshold = SpikeFinder.ThresholdOf(metric.Metric).ToString("0", culture);
            lines.Add(new ContextLine(
                pt ? $"{MetricName(metric.Metric, pt)} no período" : $"{MetricName(metric.Metric, pt)} over the range",
                pt
                    ? $"média {Percent(metric.Average, culture)}, normal {Percent(metric.Baseline, culture)}, pico {Percent(metric.Peak, culture)}, {metric.MinutesAbove} min com pico ≥ {threshold}%"
                    : $"average {Percent(metric.Average, culture)}, usual {Percent(metric.Baseline, culture)}, peak {Percent(metric.Peak, culture)}, {metric.MinutesAbove} min peaking ≥ {threshold}%"));
        }

        if (report.Spikes.Count == 0)
        {
            lines.Add(new ContextLine(pt ? "Picos" : "Spikes", pt ? "nenhum pico fora do normal no período" : "no unusual spikes in the range"));
        }

        foreach (Spike spike in report.Spikes.OrderByDescending(s => s.Peak).ThenByDescending(s => s.Start).Take(MaxSpikes).OrderBy(s => s.Start))
        {
            string when = $"{Time(spike.Start, culture)}–{spike.End.ToLocalTime().ToString("HH:mm", culture)} ({Duration(spike.Duration, pt)})";
            string app = spike.TopApp is null ? "" : spike.Metric == Metric.Memory
                ? (pt ? $"; app com mais memória: {spike.TopApp}" : $"; app using most memory: {spike.TopApp}")
                : (pt ? $"; app com mais CPU: {spike.TopApp}" : $"; app using most CPU: {spike.TopApp}");
            lines.Add(new ContextLine(
                pt ? $"Pico de {MetricName(spike.Metric, pt)}" : $"{MetricName(spike.Metric, pt)} spike",
                pt
                    ? $"{when}: {Percent(spike.Peak, culture)}, normal {Percent(spike.Baseline, culture)}{app}"
                    : $"{when}: {Percent(spike.Peak, culture)}, usual {Percent(spike.Baseline, culture)}{app}"));
        }

        if (!includeLogs)
        {
            return lines;
        }

        string source = report.LogSource ?? (pt ? "log do sistema" : "system log");
        if (report.Logs is not { Count: > 0 } logs)
        {
            lines.Add(new ContextLine(source, pt ? "nenhum aviso ou erro no período" : "no warnings or errors in the range"));
            return lines;
        }

        foreach (LogGroup group in logs.Take(MaxLogGroups))
        {
            string severity = group.Severity switch
            {
                LogSeverity.Critical => pt ? "Crítico" : "Critical",
                LogSeverity.Error => pt ? "Erro" : "Error",
                _ => pt ? "Aviso" : "Warning",
            };
            string id = group.EventId is null ? group.Source : $"{group.Source} {group.EventId}";
            string times = group.Count == 1
                ? Time(group.Last, culture)
                : pt ? $"{group.Count}×, de {Time(group.First, culture)} a {Time(group.Last, culture)}" : $"{group.Count}×, {Time(group.First, culture)} to {Time(group.Last, culture)}";
            lines.Add(new ContextLine($"{source}: {severity} ({id}, {group.Log})", $"{group.Message} [{times}]"));
        }

        return lines;
    }

    private static string MetricName(Metric metric, bool pt) => metric switch
    {
        Metric.Cpu => "CPU",
        Metric.Memory => pt ? "memória" : "memory",
        Metric.Disk => pt ? "disco (tempo ativo)" : "disk (active time)",
        _ => "GPU",
    };

    private static string Percent(double value, CultureInfo culture) => value.ToString("0", culture) + "%";

    private static string Time(DateTimeOffset at, CultureInfo culture) => at.ToLocalTime().ToString("ddd dd/MM HH:mm", culture);

    private static string Duration(TimeSpan span, bool pt) => span.TotalMinutes < 60
        ? $"{Math.Max((int)Math.Round(span.TotalMinutes), 1)} min"
        : span.TotalHours < 48
            ? (span.Minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {span.Minutes} min")
            : pt ? $"{(int)Math.Round(span.TotalDays)} dias" : $"{(int)Math.Round(span.TotalDays)} days";

    private static List<AssistantAction> AllowedActions(AssistantTopic topic, TopicKind group, List<Finding> findings, PcSnapshot snapshot)
    {
        var actions = new List<AssistantAction>();
        void Add(AssistantAction action)
        {
            if (!actions.Contains(action))
            {
                actions.Add(action);
            }
        }

        if (topic.Kind == TopicKind.CleanupItem && topic.Key is { } ruleId && snapshot.Cleanup.Any(e => e.Rule.Id == ruleId))
        {
            Add(new AssistantAction(ActionKind.CleanupItem, ruleId));
        }

        foreach (Finding finding in findings.Where(f => f.Action is not null && (group == TopicKind.Overview || f.Group == group)))
        {
            Add(finding.Action!);
        }

        if (group is TopicKind.Memory or TopicKind.Processor or TopicKind.Overview && ClosableTopApp(snapshot.Latest) is { } app)
        {
            Add(new AssistantAction(ActionKind.CloseApp, app.Key));
        }

        AssistantAction[] defaults = group switch
        {
            TopicKind.Storage => [new(ActionKind.Cleanup), new(ActionKind.Storage)],
            TopicKind.Memory or TopicKind.Processor => [new(ActionKind.Memory)],
            TopicKind.Graphics or TopicKind.Power => [new(ActionKind.Specs)],
            _ => [new(ActionKind.Cleanup), new(ActionKind.Memory), new(ActionKind.Specs)],
        };
        foreach (AssistantAction action in defaults)
        {
            Add(action);
        }

        if (group == TopicKind.Storage && !snapshot.IsUnlocked)
        {
            Add(new AssistantAction(ActionKind.Unlock));
        }

        return [.. actions.Take(MaxActions)];
    }

    /// <summary>The app using the most memory, when the user may close it.</summary>
    private static AppGroup? ClosableTopApp(MonitorSample? sample) =>
        sample?.Apps.OrderByDescending(a => a.Memory).FirstOrDefault() is { CanClose: true, IsProtected: false } app ? app : null;

    /// <summary>Drops the least important facts, then findings, until the text fits the budget.</summary>
    private static ContextBundle Trim(ContextBundle bundle)
    {
        while (bundle.ToText().Length > MaxCharacters && bundle.Facts.Count > 1)
        {
            bundle = bundle with { Facts = [.. bundle.Facts.Take(bundle.Facts.Count - 1)] };
        }

        while (bundle.ToText().Length > MaxCharacters && bundle.Findings.Count > 1)
        {
            bundle = bundle with { Findings = [.. bundle.Findings.Take(bundle.Findings.Count - 1)] };
        }

        return bundle;
    }
}
