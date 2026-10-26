using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring.Hardware;
using static Pennyworth.Assistant.Tests.Samples;

namespace Pennyworth.Assistant.Tests;

public class DiagnosisTests
{
    private static readonly DiagnosisEngine Engine = DiagnosisEngine.FromKnowledgeBase();

    private static IReadOnlyList<string> Fired(PcSnapshot snapshot) =>
        [.. Engine.Evaluate(FactCollector.Collect(snapshot, En), En).Select(f => f.RuleId)];

    [Theory]
    [InlineData(90, "disk-system-critical")]
    [InlineData(89, "disk-system-high")]
    [InlineData(80, "disk-system-high")]
    public void System_drive_fills_up_in_two_steps(double used, string rule)
    {
        var fired = Fired(Snapshot(diskUsedPercent: used));

        Assert.Contains(rule, fired);
        Assert.Single(fired, id => id.StartsWith("disk-system-", StringComparison.Ordinal));
    }

    [Fact]
    public void A_drive_at_79_percent_is_fine()
    {
        Assert.DoesNotContain(Fired(Snapshot(diskUsedPercent: 79)), id => id.StartsWith("disk-system-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(85, true)]
    [InlineData(84, false)]
    public void Memory_pressure_starts_at_85_percent(double ram, bool fires)
    {
        Assert.Equal(fires, Fired(Snapshot(ramPercent: ram)).Contains("ram-pressure"));
        Assert.Equal(fires, Fired(Snapshot(ramPercent: ram)).Contains("ram-upgrade-possible")); // one slot is free in the sample
    }

    [Theory]
    [InlineData(90, true)]
    [InlineData(89, false)]
    public void A_busy_processor_starts_at_90_percent(double cpu, bool fires)
    {
        Assert.Equal(fires, Fired(Snapshot(cpu: cpu)).Contains("cpu-busy"));
    }

    [Fact]
    public void One_app_with_a_quarter_of_the_memory_is_named()
    {
        var findings = Engine.Evaluate(FactCollector.Collect(Snapshot(apps: [App("chrome", 4 * Gb)]), En), En);

        Finding hungry = Assert.Single(findings, f => f.RuleId == "ram-hungry-app");
        Assert.Equal("One app is using 25% of the memory (4.00 GB). Apps using the most: Google Chrome 4.00 GB.", hungry.Text);
        Assert.Equal(new AssistantAction(ActionKind.Memory), hungry.Action);
        Assert.DoesNotContain("ram-hungry-app", Fired(Snapshot(apps: [App("chrome", 3 * Gb)])));
    }

    [Fact]
    public void Hardware_findings_come_from_the_specs()
    {
        var fired = Fired(Snapshot());

        Assert.Contains("windows-on-hdd", fired);
        Assert.Contains("unused-ssd", fired);
        Assert.Contains("long-uptime", fired); // 200 h in the sample
        Assert.Contains("low-video-memory", fired);
        Assert.Contains("cleanup-worthwhile", fired); // 18 GB of temporary files
        Assert.DoesNotContain("windows-on-hdd", Fired(Snapshot(specs: Specs(systemSsd: true))));
    }

    [Fact]
    public void Low_battery_only_counts_when_unplugged()
    {
        Assert.Contains("battery-low", Fired(Snapshot(specs: Specs(new BatterySpecs(20, false, OnAcPower: false)))));
        Assert.DoesNotContain("battery-low", Fired(Snapshot(specs: Specs(new BatterySpecs(20, true, OnAcPower: true)))));
        Assert.DoesNotContain("battery-low", Fired(Snapshot(specs: Specs(new BatterySpecs(21, false, OnAcPower: false)))));
    }

    [Fact]
    public void Problems_come_first_and_text_is_in_the_users_language()
    {
        var findings = Engine.Evaluate(FactCollector.Collect(Snapshot(diskUsedPercent: 92, ramPercent: 90), Pt), Pt);

        Assert.Equal(DiagnosisSeverity.Problem, findings[0].Severity);
        Assert.Equal(DiagnosisSeverity.Problem, findings[1].Severity);
        Assert.StartsWith("O disco do Windows está 92% ocupado (", findings[0].Text);
        Assert.False(DiagnosisEngine.IsAllClear(findings));
    }

    [Fact]
    public void Missing_facts_never_fire_a_rule()
    {
        Assert.Empty(Engine.Evaluate(FactCollector.Collect(PcSnapshot.Empty, En), En));
        Assert.True(DiagnosisEngine.IsAllClear([]));
    }

    [Fact]
    public void Embedded_rules_are_consistent()
    {
        // Every fact a rule names exists for a full snapshot (laptop, busy, hungry app).
        var snapshot = Snapshot(diskUsedPercent: 95, ramPercent: 95, cpu: 95, specs: Specs(new BatterySpecs(10, false, false)), apps: [App("chrome", 8 * Gb, 30)]);
        var ids = FactCollector.Collect(snapshot, En).Select(f => f.Id).ToHashSet();

        Assert.Equal(Engine.Rules.Count, Engine.Rules.Select(r => r.Id).Distinct().Count());
        foreach (DiagnosisRule rule in Engine.Rules)
        {
            Assert.All(rule.When, c => Assert.Contains(c.Fact, ids));
            Assert.All(DiagnosisEngine.Placeholders(rule.Finding.En).Concat(DiagnosisEngine.Placeholders(rule.Finding.PtBr)), p => Assert.Contains(p, ids));
            Assert.True(Enum.TryParse<TopicKind>(rule.Group, out _), rule.Id);
            Assert.True(rule.Action is null || AssistantAction.Parse(rule.Action) is not null, rule.Id);
        }
    }
}
