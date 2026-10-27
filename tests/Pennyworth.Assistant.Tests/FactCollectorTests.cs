using Pennyworth.Monitoring.Hardware;
using static Pennyworth.Assistant.Tests.Samples;

namespace Pennyworth.Assistant.Tests;

public class FactCollectorTests
{
    [Fact]
    public void Describes_the_system_drive_and_what_fills_it()
    {
        var facts = FactCollector.Collect(Snapshot(diskUsedPercent: 92), En);

        Assert.Equal(92, facts.Get("disk.system.used_percent").Number);
        Assert.Equal("92%", facts.Get("disk.system.used_percent").Format(En));
        Assert.Equal("Drive C: full", facts.Get("disk.system.used_percent").Label);
        Assert.Equal("hard disk (HDD), ST1500DL003, SATA", facts.Get("disk.system.kind").Text);
        Assert.Equal(1, facts.Get("disk.system.is_hdd").Number);
        Assert.Equal(1, facts.Get("disk.unused_ssd_count").Number);
        Assert.Equal(90, facts.Get("disk.d.used_percent").Number);
        Assert.Equal(412 * Gb, facts.Get("storage.games").Number);
        Assert.DoesNotContain(facts, f => f.Id == "storage.other"); // empty categories are left out
    }

    [Fact]
    public void Averages_memory_and_processor_over_the_last_minute_only()
    {
        var snapshot = Snapshot() with
        {
            RecentSamples = [.. Enumerable.Repeat(Sample(10, cpu: 0), 100), .. Enumerable.Repeat(Sample(90, cpu: 50), 60)],
        };

        var facts = FactCollector.Collect(snapshot, En);

        Assert.Equal(90, facts.Get("ram.sustained_percent").Number);
        Assert.Equal(50, facts.Get("cpu.sustained_percent").Number);
    }

    [Fact]
    public void Names_the_apps_using_the_most_memory()
    {
        var facts = FactCollector.Collect(
            Snapshot(apps: [App("discord", Gb), App("chrome", 4 * Gb, cpu: 12), App("svchost", 2 * Gb, canClose: false, title: "Windows services")]),
            En);

        Assert.Equal("Google Chrome 4.00 GB, Windows services 2.00 GB, discord 1.00 GB", facts.Get("ram.top_apps").Text);
        Assert.Equal(25, facts.Get("ram.top_app.share_percent").Number);
        Assert.Equal(1, facts.Get("ram.top_app.can_close").Number);
        Assert.Equal("Google Chrome 12%", facts.Get("cpu.top_apps").Text);
    }

    [Fact]
    public void Knows_laptops_and_their_battery()
    {
        var facts = FactCollector.Collect(Snapshot(specs: Specs(new BatterySpecs(18, IsCharging: false, OnAcPower: false))), Pt);

        Assert.Equal("Notebook", facts.Get("pc.kind").Text);
        Assert.Equal(18, facts.Get("battery.percent").Number);
        Assert.Equal(0, facts.Get("battery.on_ac").Number);
    }

    [Fact]
    public void Reports_cleanup_and_formats_in_portuguese()
    {
        var facts = FactCollector.Collect(Snapshot(), Pt);

        Assert.Equal("18,0 GB", facts.Get("cleanup.safe_bytes").Format(Pt));
        Assert.Equal("67.808", facts.Get("cleanup.item.temp.items").Format(Pt));
        Assert.Equal("Limpeza: temp arquivos", facts.Get("cleanup.item.temp.bytes").Label);
        Assert.Equal(1, facts.Get("ram.slots_free").Number);
        Assert.Equal("16,0 GB DDR4 3200 MT/s", facts.Get("ram.modules").Text);
        Assert.Equal(4 * Gb, facts.Get("gpu.memory").Number); // the card with the most memory
        Assert.Equal(200, facts.Get("os.uptime_hours").Number);
    }

    [Fact]
    public void Missing_data_means_fewer_facts_not_errors()
    {
        var facts = FactCollector.Collect(PcSnapshot.Empty, En);

        Assert.Equal(["unlock.state"], facts.Select(f => f.Id));
    }

    [Fact]
    public void Fact_ids_are_unique()
    {
        var facts = FactCollector.Collect(Snapshot(apps: [App("chrome", Gb)]), En);

        Assert.Equal(facts.Count, facts.Select(f => f.Id).Distinct().Count());
    }

    [Fact]
    public void The_compositor_is_a_graphics_fact_only_when_the_specs_have_one()
    {
        var withCompositor = Specs() with { Compositor = new CompositorSpecs("Wayland", "KWin", "6.8.1") };

        var facts = FactCollector.Collect(Snapshot(specs: withCompositor), En);

        Assert.Equal("KWin 6.8.1 (Wayland)", facts.Get("display.compositor").Text);
        Assert.Equal(TopicKind.Graphics, facts.Get("display.compositor").Group);
        Assert.DoesNotContain(FactCollector.Collect(Snapshot(), En), f => f.Id == "display.compositor");
    }
}
