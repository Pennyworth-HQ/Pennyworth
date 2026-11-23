using Pennyworth.App.Localization;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.Core.Storage;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.Alerts;
using Pennyworth.Platform;

namespace Pennyworth.App.Tests;

[Collection(nameof(LanguageSwitching))]
public sealed class AlertsViewModelTests : IDisposable
{
    private static readonly MonitorSample Busy = new(new MemoryStatus(100, 50, 0, 0), CpuPercent: 99, GpuPercent: 10, Apps: []);
    private static readonly MonitorSample Calm = new(new MemoryStatus(100, 50, 0, 0), CpuPercent: 5, GpuPercent: 10, Apps: []);

    private readonly FakeHost _host = new();
    private readonly FakeNotifier _notifier = new();
    private DateTimeOffset _now = new(2026, 10, 28, 9, 30, 0, TimeSpan.Zero);

    public AlertsViewModelTests() => Localizer.Instance.SetLanguage(LanguagePreference.English);

    public void Dispose()
    {
        Localizer.Instance.SetLanguage(LanguagePreference.English);
        Directory.Delete(_dir, recursive: true);
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("pennyworth-alerts").FullName;

    private AlertsViewModel Create(IDriveService? drives = null, PetViewModel? pet = null) =>
        new(_host, _notifier, drives ?? new FakeDriveService(TestApp.DriveC), pet, () => _now, new AlertMonitor(sustain: TimeSpan.Zero));

    /// <summary>A Codex pet already added in Settings.</summary>
    private PetViewModel Boba()
    {
        var library = new PetLibrary(Path.Combine(_dir, "pets"));
        library.Import(SamplePets.Zip(_dir, "boba.zip", ("pet.json", SamplePets.Utf8(SamplePets.PetJson)), ("spritesheet.webp", SamplePets.WebpHeader)));
        return new PetViewModel(library, null, null, () => _now, "boba");
    }

    [Fact]
    public void Starts_with_one_rule_per_part_at_ninety_percent()
    {
        var alerts = Create();

        Assert.Equal([AlertComponent.Cpu, AlertComponent.Memory, AlertComponent.Gpu, AlertComponent.Disk], alerts.Rules.Select(r => r.Component));
        Assert.All(alerts.Rules, r => Assert.Equal("90%", r.ThresholdText));
    }

    [Fact]
    public void A_busy_processor_sends_a_notification_and_logs_it()
    {
        var alerts = Create();

        alerts.Check(Busy);

        var (title, message, onClick) = Assert.Single(_notifier.Sent);
        Assert.Equal("Processor at 99%", title);
        Assert.Contains("processor", message, StringComparison.OrdinalIgnoreCase);
        AlertLogEntry entry = Assert.Single(alerts.Recent);
        Assert.Equal("09:30", entry.Time);
        Assert.Equal(title, entry.Title);

        onClick!();
        Assert.Equal([AlertComponent.Cpu], _host.Opened);
    }

    [Fact]
    public void A_custom_rule_uses_its_own_words()
    {
        var alerts = Create();
        alerts.AddRuleCommand.Execute(null);
        AlertRuleViewModel mine = alerts.Rules[^1];
        mine.SetComponentCommand.Execute(AlertComponent.Memory);
        mine.LowerCommand.Execute(null); // 85%
        mine.Message = "Close some tabs, Bruce.";

        alerts.Check(new MonitorSample(new MemoryStatus(100, 12, 0, 0), 5, null, []));

        Assert.Contains(_notifier.Sent, n => n.Title == "Memory at 88%" && n.Message == "Close some tabs, Bruce.");
        Assert.DoesNotContain(_notifier.Sent, n => n.Message != "Close some tabs, Bruce."); // 88% is under the default 90%.
    }

    [Fact]
    public void A_full_drive_names_the_drive()
    {
        var full = new DriveSummary(@"D:\", "Games", 100, 3);
        var alerts = Create(new FakeDriveService(full));

        alerts.Check(Calm);

        var (title, message, _) = Assert.Single(_notifier.Sent);
        Assert.Equal("Drive D: at 97%", title);
        Assert.Contains("D:", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Paused_alerts_send_nothing_and_do_not_fire_late()
    {
        var alerts = Create();
        alerts.TogglePaused();

        alerts.Check(Busy);
        Assert.Empty(_notifier.Sent);

        alerts.TogglePaused();
        _now = _now.AddSeconds(1);
        alerts.Check(Busy);
        Assert.Empty(_notifier.Sent); // The same episode, already counted while paused.
    }

    [Fact]
    public void Changes_are_saved()
    {
        var alerts = Create();

        alerts.Rules[0].RaiseCommand.Execute(null);
        alerts.RemoveRuleCommand.Execute(alerts.Rules[1]);
        alerts.RunInBackground = false;

        AlertSettings saved = _host.AlertSettings;
        Assert.False(saved.RunInBackground);
        Assert.Equal(3, saved.Rules.Count);
        Assert.Equal(95, saved.Rules[0].Threshold);
        Assert.DoesNotContain(saved.Rules, r => r.Component == AlertComponent.Memory);
    }

    [Fact]
    public void Limits_step_by_five_and_stop_at_the_ends()
    {
        var rule = new AlertRuleViewModel(new AlertRule("x", AlertComponent.Cpu, 90), () => { });

        rule.RaiseCommand.Execute(null);
        Assert.Equal(95, rule.Rule.Threshold);
        rule.RaiseCommand.Execute(null);
        rule.RaiseCommand.Execute(null);
        Assert.Equal(99, rule.Rule.Threshold);
        rule.LowerCommand.Execute(null);
        Assert.Equal(95, rule.Rule.Threshold);
    }

    [Fact]
    public void Without_a_tray_alerts_show_as_toasts()
    {
        var alerts = new AlertsViewModel(_host, null, new FakeDriveService(TestApp.DriveC), clock: () => _now, monitor: new AlertMonitor(sustain: TimeSpan.Zero));

        alerts.Check(Busy);

        Assert.Contains(_host.Toasts, t => t.StartsWith("Processor at 99%", StringComparison.Ordinal));
    }

    [Fact]
    public void With_a_pet_the_alerts_come_as_its_speech()
    {
        var alerts = Create(pet: Boba());

        alerts.Check(Busy);

        Assert.Empty(_notifier.Sent);
        Assert.Equal("Processor at 99%", alerts.Pet.Speech!.Title);
        alerts.Pet.OpenSpeechCommand.Execute(null);
        Assert.Equal([AlertComponent.Cpu], _host.Opened);
    }

    [Fact]
    public void Without_a_pet_the_alerts_go_back_to_windows()
    {
        PetViewModel pet = Boba();
        var alerts = Create(pet: pet);

        pet.RemoveCommand.Execute(null);
        alerts.Check(Busy);

        Assert.False(alerts.Pet.IsVisible);
        Assert.Single(_notifier.Sent);
    }

    [Fact]
    public void Settings_from_before_alerts_get_the_default_rules()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pennyworth-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "language": "English", "welcomeSeen": true }""");
            var store = new FileSettingsStore(path);

            AppSettings loaded = store.Load();
            Assert.Equal(AlertRule.Defaults, loaded.Alerts.Rules);
            Assert.True(loaded.Alerts.RunInBackground);

            var changed = loaded with { Alerts = loaded.Alerts with { Rules = [new AlertRule("t", AlertComponent.Gpu, 80, Message: "Busy!")] } };
            store.Save(changed);
            Assert.Equal(changed, store.Load());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FakeHost : IAlertsHost
    {
        public AlertSettings AlertSettings { get; private set; } = new();

        public List<string> Toasts { get; } = [];

        public List<AlertComponent> Opened { get; } = [];

        public void SaveAlertSettings(AlertSettings settings) => AlertSettings = settings;

        public void ShowToast(string message) => Toasts.Add(message);

        public void OpenFromAlert(AlertComponent component) => Opened.Add(component);
    }

    private sealed class FakeNotifier : INotifier
    {
        public List<(string Title, string Message, Action? OnClick)> Sent { get; } = [];

        public void Notify(string title, string message, Action? onClick = null) => Sent.Add((title, message, onClick));
    }
}
