using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.Alerts;
using Pennyworth.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>What the alerts screen needs from the shell.</summary>
public interface IAlertsHost
{
    AlertSettings AlertSettings { get; }

    void SaveAlertSettings(AlertSettings settings);

    void ShowToast(string message);

    /// <summary>A notification was clicked: bring the window back on the screen about that part.</summary>
    void OpenFromAlert(AlertComponent component);
}

/// <summary>
/// Notifications when a part of the PC stays above a limit: one rule per part to start with,
/// each with its own limit, and as many extra rules as the person likes, with their own words.
/// Checked on every live sample, also while the window is hidden in the tray.
/// </summary>
public sealed partial class AlertsViewModel : ObservableObject
{
    public const int RecentLength = 20;

    /// <summary>Drive usage moves slowly; reading it once a minute is plenty.</summary>
    private static readonly TimeSpan DriveRefresh = TimeSpan.FromMinutes(1);

    private readonly IAlertsHost _host;
    private readonly INotifier? _notifier;
    private readonly IDriveService _drives;
    private readonly IStartupRegistration? _startup;
    private readonly Func<DateTimeOffset> _clock;
    private readonly AlertMonitor _monitor;
    private IReadOnlyList<(string Root, double UsedPercent)> _driveUsage = [];
    private DateTimeOffset? _drivesReadAt;
    private bool _loading;

    public AlertsViewModel(IAlertsHost host, INotifier? notifier, IDriveService drives, IStartupRegistration? startup = null,
        Func<DateTimeOffset>? clock = null, AlertMonitor? monitor = null)
    {
        _host = host;
        _notifier = notifier;
        _drives = drives;
        _startup = startup;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _monitor = monitor ?? new AlertMonitor();

        _loading = true;
        AlertSettings settings = host.AlertSettings;
        Enabled = settings.Enabled;
        RunInBackground = settings.RunInBackground;
        StartWithWindows = ReadStartup();
        Pet = new PetViewModel(settings.Pet, _clock);
        PetChoices = [.. PetArt.All.Select(kind => new PetChoiceViewModel(kind) { IsSelected = kind == settings.Pet })];
        foreach (AlertRule rule in settings.Rules)
        {
            Rules.Add(new AlertRuleViewModel(rule, Save));
        }

        _loading = false;
        Recent.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecent));
    }

    public ObservableCollection<AlertRuleViewModel> Rules { get; } = [];

    public ObservableCollection<AlertLogEntry> Recent { get; } = [];

    public bool HasRecent => Recent.Count > 0;

    /// <summary>The pet on the desktop that says the notifications, when one is chosen.</summary>
    public PetViewModel Pet { get; }

    public IReadOnlyList<PetChoiceViewModel> PetChoices { get; }

    /// <summary>False where the OS has no tray: alerts then only show inside the window.</summary>
    public bool HasNotifications => _notifier is not null;

    public bool CanStartWithWindows => _startup is not null;

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    [ObservableProperty]
    public partial bool RunInBackground { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    partial void OnEnabledChanged(bool value) => Save();

    partial void OnRunInBackgroundChanged(bool value) => Save();

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading || _startup is null)
        {
            return;
        }

        try
        {
            _startup.SetEnabled(value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            _host.ShowToast(Strings.AlertsStartupFailed);
            _loading = true;
            StartWithWindows = ReadStartup();
            _loading = false;
        }
    }

    /// <summary>Called with every live sample: fires the rules that went off.</summary>
    public void Check(MonitorSample sample)
    {
        DateTimeOffset now = _clock();
        if (_drivesReadAt is not { } readAt || now - readAt >= DriveRefresh)
        {
            _drivesReadAt = now;
            try
            {
                _driveUsage = [.. _drives.GetDrives()
                    .Where(d => d.TotalBytes > 0)
                    .Select(d => (d.RootPath, 100.0 * d.UsedBytes / d.TotalBytes))];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine(ex); // Keep the last numbers.
            }
        }

        // Evaluated even while paused, so turning alerts back on does not fire for what was already high.
        IReadOnlyList<UsageAlert> alerts = _monitor.Evaluate([.. Rules.Select(r => r.Rule)], AlertMonitor.Readings(sample, _driveUsage), now);
        if (!Enabled)
        {
            return;
        }

        foreach (UsageAlert alert in alerts)
        {
            (string title, string message) = Describe(alert);
            Send(title, message, alert.Rule.Component);
            Recent.Insert(0, new AlertLogEntry(alert.At.ToString("HH:mm", CultureInfo.CurrentCulture), title, message));
            while (Recent.Count > RecentLength)
            {
                Recent.RemoveAt(Recent.Count - 1);
            }
        }
    }

    /// <summary>The words of a notification: the person's own message when the rule has one.</summary>
    public static (string Title, string Message) Describe(UsageAlert alert)
    {
        AlertRule rule = alert.Rule;
        long value = (long)Math.Round(alert.Value);
        string drive = alert.Target?.TrimEnd('\\') ?? "";
        string name = rule.Component switch
        {
            AlertComponent.Cpu => Strings.AlertNameCpu,
            AlertComponent.Memory => Strings.AlertNameMemory,
            AlertComponent.Gpu => Strings.AlertNameGpu,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.AlertDiskName, drive),
        };
        string title = string.Format(CultureInfo.CurrentCulture, Strings.AlertTitle, name, value);
        string message = !string.IsNullOrWhiteSpace(rule.Message) ? rule.Message.Trim() : rule.Component switch
        {
            AlertComponent.Cpu => Strings.AlertBodyCpu,
            AlertComponent.Memory => Strings.AlertBodyMemory,
            AlertComponent.Gpu => Strings.AlertBodyGpu,
            _ => string.Format(CultureInfo.CurrentCulture, Strings.AlertBodyDisk, drive),
        };
        return (title, message);
    }

    /// <summary>From the tray menu.</summary>
    public void TogglePaused() => Enabled = !Enabled;

    [RelayCommand]
    private void AddRule()
    {
        var rule = new AlertRule(Guid.NewGuid().ToString("N"), AlertComponent.Cpu, AlertRule.DefaultThreshold, Message: "");
        Rules.Add(new AlertRuleViewModel(rule, Save));
        Save();
    }

    [RelayCommand]
    private void RemoveRule(AlertRuleViewModel rule)
    {
        Rules.Remove(rule);
        Save();
    }

    /// <summary>Choosing a pet puts it on the desktop, where it says hello; None hides it.</summary>
    [RelayCommand]
    private void ChoosePet(PetKind kind)
    {
        if (kind == Pet.Kind)
        {
            return;
        }

        Pet.Kind = kind;
        foreach (PetChoiceViewModel choice in PetChoices)
        {
            choice.IsSelected = choice.Kind == kind;
        }

        Save();
        Pet.Say(string.Format(CultureInfo.CurrentCulture, Strings.PetHelloTitle, PetArt.Name(kind)), Strings.PetHelloBody);
    }

    [RelayCommand]
    private void SendTest() => Send(Strings.AlertTestTitle, Strings.AlertTestBody, null);

    public void RefreshTexts()
    {
        foreach (AlertRuleViewModel rule in Rules)
        {
            rule.RefreshTexts();
        }

        foreach (PetChoiceViewModel choice in PetChoices)
        {
            choice.RefreshTexts();
        }
    }

    /// <summary>
    /// Every notification goes through here: said by the pet when there is one, else a Windows
    /// notification, else a toast inside the window.
    /// </summary>
    public void Notify(string title, string message, Action? onClick = null)
    {
        if (Pet.IsVisible)
        {
            Pet.Say(title, message, onClick);
        }
        else if (_notifier is { } notifier)
        {
            notifier.Notify(title, message, onClick);
        }
        else
        {
            _host.ShowToast($"{title}: {message}");
        }
    }

    private void Send(string title, string message, AlertComponent? component) =>
        Notify(title, message, component is { } c ? () => _host.OpenFromAlert(c) : null);

    private bool ReadStartup()
    {
        try
        {
            return _startup?.IsEnabled ?? false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }

    private void Save()
    {
        if (_loading)
        {
            return;
        }

        _host.SaveAlertSettings(_host.AlertSettings with
        {
            Enabled = Enabled,
            RunInBackground = RunInBackground,
            Pet = Pet.Kind,
            Rules = [.. Rules.Select(r => r.Rule)],
        });
    }
}

/// <summary>One rule on the alerts screen: which part, above what, and the optional message.</summary>
public sealed partial class AlertRuleViewModel : ObservableObject
{
    private readonly Action _changed;

    public AlertRuleViewModel(AlertRule rule, Action changed)
    {
        _changed = changed;
        Rule = rule;
    }

    public AlertRule Rule { get; private set; }

    public bool Enabled
    {
        get => Rule.Enabled;
        set => Update(Rule with { Enabled = value });
    }

    public string Message
    {
        get => Rule.Message ?? "";
        set => Update(Rule with { Message = value });
    }

    public AlertComponent Component => Rule.Component;

    public bool IsCpu => Component == AlertComponent.Cpu;

    public bool IsMemory => Component == AlertComponent.Memory;

    public bool IsGpu => Component == AlertComponent.Gpu;

    public bool IsDisk => Component == AlertComponent.Disk;

    /// <summary>"90%".</summary>
    public string ThresholdText => Rule.Threshold.ToString("0", CultureInfo.CurrentCulture) + "%";

    public const double Minimum = 50;

    public const double Maximum = 99;

    [RelayCommand]
    private void SetComponent(AlertComponent component) => Update(Rule with { Component = component });

    /// <summary>Steps of 5, ending on the extremes: 90 → 95 → 99.</summary>
    [RelayCommand]
    private void Raise() => Update(Rule with { Threshold = Math.Min(Maximum, (Math.Floor(Rule.Threshold / 5) * 5) + 5) });

    [RelayCommand]
    private void Lower() => Update(Rule with { Threshold = Math.Max(Minimum, (Math.Ceiling(Rule.Threshold / 5) * 5) - 5) });

    public void RefreshTexts() => OnPropertyChanged(nameof(ThresholdText));

    private void Update(AlertRule rule)
    {
        if (rule == Rule)
        {
            return;
        }

        Rule = rule;
        OnPropertyChanged(string.Empty);
        _changed();
    }
}

public sealed record AlertLogEntry(string Time, string Title, string Message);
