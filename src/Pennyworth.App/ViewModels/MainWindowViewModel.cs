using System.Collections.ObjectModel;
using System.Diagnostics;
using Pennyworth.App.Localization;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Assistant;
using Pennyworth.Cleanup;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Privilege;
using Pennyworth.Core.Storage;
using Pennyworth.Core.Wording;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.Hardware;
using Pennyworth.Platform;
using Pennyworth.Scanning;
using Pennyworth.Stress;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>Everything the app talks to; tests pass fakes.</summary>
public sealed record AppServices(
    IDriveService Drives,
    IStorageScanner Scanner,
    IPathClassifier Classifier,
    ILocationInfo Locations,
    IExplorerLauncher Explorer,
    ICleanupService Cleanup,
    IHiddenSpaceService HiddenSpace,
    ISettingsStore Settings,
    ISpecsReader Specs,
    ISystemMonitor? Monitor = null,
    IProcessCloser? Closer = null,
    CleanupHistory? CleanupHistory = null,
    Func<DateTimeOffset>? Clock = null,
    IClipboardService? Clipboard = null,
    AssistantServices? Assistant = null,
    Func<StressOptions, IStressRunner>? StressRunner = null,
    bool ExtendsIntoTitleBar = true,
    IMemoryModuleReader? MemoryModules = null,
    IPrivilegedOperations? Privileged = null);

public enum Page
{
    Welcome,
    Home,
    Storage,
    Cleanup,
    Memory,
    Folders,
    Specs,
    Ask,
    Stress,
}

/// <summary>
/// The app shell: pages, Simple/3D mode, scans per drive, live monitoring, dialogs, toasts,
/// language and theme. Pages call back into it through small host interfaces.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IFolderHost, IMemoryHost, IAskHost
{
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(3.4);

    private readonly AppServices _services;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, StorageSummaryViewModel> _summaries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HiddenSpaceReport> _hidden = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _adminSpace = new(StringComparer.OrdinalIgnoreCase);
    private AppSettings _settings;
    private CancellationTokenSource? _scanCancellation;
    private int _toastVersion;
    private readonly Queue<MonitorSample> _recentSamples = new();

    public MainWindowViewModel(AppServices services)
    {
        _services = services;
        _clock = services.Clock ?? (() => DateTimeOffset.Now);
        _settings = services.Settings.Load();
        Language = _settings.Language;
        Localizer.Instance.SetLanguage(Language);
        IsDarkTheme = _settings.Theme switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => null,
        };

        Access = new AdminAccessViewModel(services.Privileged?.Access, this);
        Access.Changed += OnAccessChanged;
        Live = new LiveViewModel();
        Welcome = new WelcomeViewModel(this);
        Home = new HomeViewModel(this);
        Cleanup = new CleanupViewModel(services.Cleanup, this, () => Page = Page.Home, services.CleanupHistory, services.Explorer, Access);
        Memory = new MemoryViewModel(Live, this, services.Closer ?? new ProcessCloser());
        Folders = new FolderBrowserViewModel(this);
        Pc = new PcViewModel(this);
        Specs = new SpecsViewModel(services.Specs, this, services.Clipboard, services.MemoryModules, Access);
        Stress = new StressViewModel(Live, this, services.StressRunner, _clock);
        Stress.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(StressViewModel.State) or nameof(StressViewModel.ElapsedText))
            {
                OnPropertyChanged(nameof(StressStatus));
            }
        };
        Ask = services.Assistant is { } assistant ? new AskViewModel(assistant, this, _clock) : null;
        if (Ask is not null)
        {
            Memory.AskAbout = AskAbout;
            Cleanup.AskAbout = AskAbout;
            Specs.AskAbout = AskAbout;
        }
        Page = _settings.WelcomeSeen ? Page.Home : Page.Welcome;
        Localizer.Instance.LanguageChanged += (_, _) => RefreshTexts();
    }

    /// <summary>True when the window draws into the title bar and the system's caption buttons sit over it.</summary>
    public bool ExtendsIntoTitleBar => _services.ExtendsIntoTitleBar;

    /// <summary>One administrator session for everything that needs one: Off, Active or Needs setup, with Lock.</summary>
    public AdminAccessViewModel Access { get; }

    public LiveViewModel Live { get; }

    public WelcomeViewModel Welcome { get; }

    public HomeViewModel Home { get; }

    public CleanupViewModel Cleanup { get; }

    public MemoryViewModel Memory { get; }

    public FolderBrowserViewModel Folders { get; }

    public PcViewModel Pc { get; }

    public SpecsViewModel Specs { get; }

    public StressViewModel Stress { get; }

    /// <summary>The assistant screen; null when the app runs without assistant services (some tests).</summary>
    public AskViewModel? Ask { get; }

    public bool HasAssistant => Ask is not null;

    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    IReadOnlyList<DriveViewModel> IFolderHost.Drives => Drives;

    /// <summary>The drive the operating system runs from.</summary>
    public DriveViewModel? SystemDrive => Drives.FirstOrDefault(d => string.Equals(d.Summary.RootPath, _services.Drives.SystemDriveRoot, StringComparison.OrdinalIgnoreCase))
        ?? Drives.FirstOrDefault();

    /// <summary>The system drive's root, or the one the platform expects while no drive is known.</summary>
    public string SystemDriveRoot => SystemDrive?.Summary.RootPath ?? Localizer.Instance.Text(PlatformText.SystemDriveRoot);

    public string SystemDriveToken => SystemDrive?.Token ?? DriveViewModel.TokenOf(SystemDriveRoot);

    public StorageSummaryViewModel? SystemSummary => SystemDrive is { } drive ? SummaryOf(drive) : null;

    /// <summary>A drive's storage summary, once it has been opened (scanned or scanning).</summary>
    public StorageSummaryViewModel? SummaryOf(DriveViewModel drive) => _summaries.GetValueOrDefault(drive.Summary.RootPath);

    /// <summary>
    /// The drive shown. Clearing it is ignored: a drive list clears its selection when its
    /// page unloads, and that must not forget the drive.
    /// </summary>
    public DriveViewModel? SelectedDrive
    {
        get;
        set
        {
            if (value is null && field is not null)
            {
                OnPropertyChanged(); // Lets the list restore its selection.
                return;
            }

            if (SetProperty(ref field, value) && value is not null)
            {
                CurrentScan = ShowDriveAsync(value, forceRescan: false);
            }
        }
    }

    /// <summary>The summary of the selected drive, shown by the Storage page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(MainContent), nameof(StatusText))]
    public partial StorageSummaryViewModel? CurrentSummary { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(MainContent), nameof(ShowSidebar),
        nameof(IsHomePage), nameof(IsStoragePage), nameof(IsCleanupPage), nameof(IsMemoryPage), nameof(IsFoldersPage), nameof(IsSpecsPage), nameof(IsAskPage), nameof(IsStressPage))]
    public partial Page Page { get; private set; }

    /// <summary>3D mode replaces the pages with the ASCII PC.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MainContent), nameof(ShowSidebar))]
    public partial bool Is3d { get; private set; }

    // Leaving the Specs screen withdraws a pending password request: nobody is looking at it any more.
    partial void OnPageChanged(Page value)
    {
        if (value != Page.Specs)
        {
            Specs.CancelModulesRead();
        }
    }

    partial void OnIs3dChanged(bool value)
    {
        if (value)
        {
            Specs.CancelModulesRead();
        }
    }

    /// <summary>The page view model for <see cref="Page"/>.</summary>
    public object? CurrentPage => Page switch
    {
        Page.Welcome => Welcome,
        Page.Home => Home,
        Page.Storage => CurrentSummary,
        Page.Cleanup => Cleanup,
        Page.Memory => Memory,
        Page.Specs => Specs,
        Page.Ask => Ask,
        Page.Stress => Stress,
        _ => Folders,
    };

    /// <summary>What fills the window: the welcome screen, the 3D PC, or the current page.</summary>
    public object? MainContent => Page == Page.Welcome ? Welcome : Is3d ? Pc : CurrentPage;

    public bool ShowSidebar => Page != Page.Welcome && !Is3d;

    public bool IsHomePage => Page == Page.Home;

    public bool IsStoragePage => Page == Page.Storage;

    public bool IsCleanupPage => Page == Page.Cleanup;

    public bool IsMemoryPage => Page == Page.Memory;

    public bool IsFoldersPage => Page == Page.Folders;

    public bool IsSpecsPage => Page == Page.Specs;

    public bool IsAskPage => Page == Page.Ask;

    public bool IsStressPage => Page == Page.Stress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusShort))]
    public partial bool IsScanning { get; private set; }

    /// <summary>A dialog over the window: unlock, clean confirmation or closing an app.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial object? Dialog { get; private set; }

    public bool HasDialog => Dialog is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    public partial string? Toast { get; private set; }

    public bool HasToast => Toast is not null;

    /// <summary>True once the hidden system space of the system drive has been measured.</summary>
    [ObservableProperty]
    public partial bool IsUnlocked { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnglish), nameof(IsPortuguese))]
    public partial LanguagePreference Language { get; private set; }

    public bool IsPortuguese => Localizer.Instance.IsPortuguese;

    public bool IsEnglish => !IsPortuguese;

    /// <summary>Null until the view reports the system theme.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeLabel))]
    public partial bool? IsDarkTheme { get; private set; }

    public string ThemeLabel => IsDarkTheme == false ? Strings.ThemeLight : Strings.ThemeDark;

    public string Greeting => _clock().Hour switch
    {
        < 12 => Strings.GreetMorning,
        < 18 => Strings.GreetAfternoon,
        _ => Strings.GreetEvening,
    };

    /// <summary>"Scanning… 1,552 files found" or "Scanned 2 min ago · 1,747,000 files".</summary>
    public string StatusText => IsScanning
        ? string.Format(Strings.ScanningFiles, CurrentSummary?.FileCount ?? 0)
        : CurrentSummary is { IsComplete: true, CompletedAt: { } at } summary
            ? string.Format(Strings.ScannedAgo, Ago(at), summary.FileCount)
            : "";

    public string StatusShort => IsScanning ? Strings.StatusScanning : Strings.StatusReady;

    public string SystemDriveStatus => SystemDrive is { } d ? $"{d.Token} {d.UsedPercentText}" : "";

    public string RamStatus => Live.HasSample ? $"RAM {Live.MemoryUsedText}/{Live.MemoryTotalText}" : "";

    public string CpuStatus => Live.HasSample ? $"CPU {Live.CpuText}" : "";

    /// <summary>"STRESS 02:13" while a stress test runs, so it is never forgotten on another page.</summary>
    public string StressStatus => Stress.IsRunning ? string.Format(Strings.StressStatus, Stress.ElapsedText) : "";

    public string BitsLine => Ascii.BitsLine(Live.Bits);

    public string ChipArt => Ascii.ChipArt(Live.Bits);

    /// <summary>The scan started by the latest drive selection; lets tests await it.</summary>
    public Task CurrentScan { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the drives, starts scanning the system drive and measuring what can be cleaned.</summary>
    public void Initialize()
    {
        Drives.Clear();
        foreach (DriveSummary drive in _services.Drives.GetDrives())
        {
            Drives.Add(new DriveViewModel(drive));
        }

        SelectedDrive = SystemDrive;
        _ = MeasureCleanupAsync();
        RefreshPages();
    }

    private async Task MeasureCleanupAsync()
    {
        await Cleanup.MeasureAsync();
        RefreshPages();
    }

    /// <summary>Once per second: live numbers, and the bits that flicker.</summary>
    public async Task TickAsync()
    {
        if (_services.Monitor is { } monitor)
        {
            try
            {
                MonitorSample sample = await Task.Run(monitor.Sample);
                Live.Apply(sample);
                _recentSamples.Enqueue(sample);
                while (_recentSamples.Count > FactCollector.SustainedSamples)
                {
                    _recentSamples.Dequeue();
                }
            }
            catch (Exception ex)
            {
                // Sampling must never take the app down: Windows refuses, a driver disappears, a
                // counter query fails. Keep the last numbers and flicker the status.
                Debug.WriteLine(ex);
                Live.Flicker();
            }
        }
        else
        {
            Live.Flicker();
        }

        try
        {
            Memory.Update();
            Stress.Update();
            Home.RefreshLive();
            Pc.RefreshLive();
        }
        catch (Exception ex)
        {
            // Refreshing the views must not escape either: TickAsync runs from an async void timer
            // handler, and a stale number is better than a dead window.
            Debug.WriteLine(ex);
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RamStatus));
        OnPropertyChanged(nameof(CpuStatus));
        OnPropertyChanged(nameof(BitsLine));
        OnPropertyChanged(nameof(ChipArt));
    }

    /// <summary>Opens a drive's storage summary (scanning it if needed).</summary>
    public void ShowDrive(DriveViewModel drive)
    {
        SelectedDrive = drive;
        Navigate(Page.Storage);
    }

    /// <summary>Storage for the system drive, with a category selected (null keeps the current one).</summary>
    public void ShowStorageCategory(StorageCategory? category)
    {
        if (SystemDrive is { } system)
        {
            ShowDrive(system);
        }

        if (category is { } wanted && CurrentSummary is { } summary)
        {
            summary.SelectedCategory = summary.Categories.First(row => row.Category == wanted);
        }
    }

    /// <summary>"Analyze my PC" on the welcome screen.</summary>
    public void Analyze()
    {
        SaveSettings(_settings with { WelcomeSeen = true });
        if (SystemDrive is { } system)
        {
            ShowDrive(system);
        }
        else
        {
            Navigate(Page.Home);
        }
    }

    public void SetLanguage(LanguagePreference language)
    {
        Language = language;
        Localizer.Instance.SetLanguage(language);
        SaveSettings(_settings with { Language = language });
        Welcome.Refresh();
    }

    /// <summary>The view reports the Windows theme until the user picks one.</summary>
    public void SyncSystemTheme(bool isDark) => IsDarkTheme ??= isDark;

    public void ShowDialog(object dialog) => Dialog = dialog;

    public void CloseDialog() => Dialog = null;

    public void ShowToast(string message)
    {
        Toast = message;
        int version = ++_toastVersion;
        _ = HideToastAsync(version);
    }

    public void RequestUnlock()
    {
        if (!IsUnlocked)
        {
            Dialog = new UnlockDialogViewModel(this);
        }
    }

    /// <summary>Measures the hidden system space, after the system's permission where it asks for one (called by the dialog).</summary>
    public async Task UnlockAsync(UnlockDialogViewModel dialog)
    {
        if (SystemDrive is not { } system)
        {
            CloseDialog();
            return;
        }

        UnlockResult result = await _services.HiddenSpace.MeasureAsync(system.Summary.RootPath, CancellationToken.None);
        switch (result.Outcome)
        {
            case UnlockOutcome.Measured when result.Report is { } report:
                _hidden[system.Summary.RootPath] = report;
                SystemSummary?.ApplyHiddenSpace(report);
                IsUnlocked = true;
                if (Access.IsActive)
                {
                    // Administrator access is already on: the parts only an administrator can measure come along, without a prompt.
                    await IncludeAdminSpaceCoreAsync(promptIfNeeded: false);
                }

                CloseDialog();
                ShowToast(Localizer.Instance.Resolve(Strings.UnlockedToast));
                ShowStorageCategory(StorageCategory.HiddenSystem);
                RefreshPages();
                break;
            case UnlockOutcome.Declined:
                dialog.Declined();
                break;
            default:
                CloseDialog();
                ShowToast(Localizer.Instance.Resolve(Strings.UnlockFailed));
                break;
        }
    }

    // ---- administrator access ----

    private bool _lockRequested;
    private Task<PrivilegedResult<HiddenSpaceReport>>? _spaceMeasure;
    private readonly Dictionary<StorageSummaryViewModel, Task<LockedMeasure>> _lockedMeasures = [];

    /// <summary>The work started by the last change of the access state (measuring what was waiting); lets tests await it.</summary>
    public Task AccessWork { get; private set; } = Task.CompletedTask;

    /// <summary>The header's "Off": explains what administrator access is used for, then turns it on with the system's one password prompt.</summary>
    public void RequestTurnOn()
    {
        if (_services.Privileged is not null && Access.IsOff)
        {
            Dialog = new AccessDialogViewModel(this);
        }
    }

    /// <summary>Turns administrator access on (called by the dialog).</summary>
    public async Task TurnOnAccessAsync(AccessDialogViewModel dialog)
    {
        if (_services.Privileged is not { } privileged)
        {
            CloseDialog();
            return;
        }

        PrivilegedResult<bool> result;
        try
        {
            result = await privileged.Access.TurnOnAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
            dialog.Failed();
            return;
        }

        switch (result.Outcome)
        {
            case PrivilegedOutcome.Done:
                CloseDialog();
                ShowToast(string.Format(Strings.AccessOnToast, Access.IdleMinutes));
                break;
            case PrivilegedOutcome.Cancelled:
                dialog.Cancelled();
                break;
            case PrivilegedOutcome.NotAvailable:
                dialog.Unavailable(result.Reason);
                break;
            default:
                dialog.Failed();
                break;
        }
    }

    /// <summary>Ends the session now: the next action that needs an administrator asks for the password again.</summary>
    public void LockAccess()
    {
        if (_services.Privileged is { } privileged && Access.IsActive)
        {
            _lockRequested = true;
            privileged.Access.Lock();
        }
    }

    /// <summary>
    /// Shows what the one-time setup would install (read from this copy of the app, nothing run yet). <paramref name="then"/> runs after it
    /// finished: the request the person came from, or the turning on. Returns the work, for tests.
    /// </summary>
    public Task RequestSetup(Action? then = null) =>
        _services.Privileged is { } privileged ? ShowSetupAsync(privileged.Access, then ?? RequestTurnOn) : Task.CompletedTask;

    private async Task ShowSetupAsync(IAdministratorAccess access, Action then)
    {
        PrivilegedResult<AdministratorSetupPlan> plan;
        try
        {
            plan = await access.PrepareSetupAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
            plan = PrivilegedResult<AdministratorSetupPlan>.Failed(ex.Message);
        }

        var dialog = new SetupDialogViewModel(this, plan.Outcome == PrivilegedOutcome.Done ? plan.Value : null, then);
        switch (plan.Outcome)
        {
            case PrivilegedOutcome.Done:
                break;
            case PrivilegedOutcome.NotAvailable:
                dialog.Unavailable(plan.Reason);
                break;
            default:
                dialog.Failed();
                break;
        }

        Dialog = dialog;
    }

    /// <summary>Installs the helper and the policy the dialog showed (called by the dialog), then goes on to what the person came for.</summary>
    public async Task SetupAccessAsync(SetupDialogViewModel dialog)
    {
        if (_services.Privileged is not { } privileged || dialog.Plan is not { } plan)
        {
            CloseDialog();
            return;
        }

        PrivilegedResult<bool> result;
        try
        {
            result = await privileged.Access.SetupAsync(plan, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
            dialog.Failed();
            return;
        }

        switch (result.Outcome)
        {
            case PrivilegedOutcome.Done:
                CloseDialog();
                ShowToast(Strings.AccessSetupToast);
                dialog.Then?.Invoke();
                break;
            case PrivilegedOutcome.Cancelled:
                dialog.Cancelled();
                break;
            case PrivilegedOutcome.NotAvailable:
                dialog.Unavailable(result.Reason);
                break;
            default:
                dialog.Failed();
                break;
        }
    }

    /// <summary>What follows a change of the access state: everything that was waiting for it, and the words about it.</summary>
    private void OnAccessChanged(AdministratorAccessState state, AdministratorAccessState before)
    {
        (Dialog as PrivilegedDialogViewModel)?.RefreshTexts();
        foreach (StorageSummaryViewModel summary in _summaries.Values)
        {
            summary.RefreshAccess();
        }

        Specs.RefreshAccess();
        Cleanup.RefreshAccess();
        if (state == AdministratorAccessState.Active && before != AdministratorAccessState.Active)
        {
            AccessWork = ActivateAsync();
        }
        else if (before == AdministratorAccessState.Active && state != AdministratorAccessState.Active)
        {
            ShowToast(_lockRequested ? Strings.AccessLockedToast : Strings.AccessEndedToast);
            _lockRequested = false;
        }
    }

    /// <summary>
    /// Administrator access just turned on: every drive's locked folders are measured and shown in its tree, what only an administrator can
    /// measure of the system drive joins the totals, and the memory modules fill in. All of it runs in the session that is already
    /// open, so none of it can ask for a password.
    /// </summary>
    private async Task ActivateAsync()
    {
        foreach (StorageSummaryViewModel summary in _summaries.Values.ToArray())
        {
            await MeasureLockedCoreAsync(summary, promptIfNeeded: false);
        }

        if (IsUnlocked)
        {
            await IncludeAdminSpaceCoreAsync(promptIfNeeded: false);
        }

        await Specs.ReadModulesQuietlyAsync();
        RefreshPages();
    }

    /// <summary>Offers the second step of the full system space: what only an administrator can measure (snapshots, file system bookkeeping).</summary>
    public void RequestAdminSpace()
    {
        if (_services.Privileged is null || !IsUnlocked)
        {
            return;
        }

        if (Access.IsActive)
        {
            AccessWork = IncludeAdminSpaceWithToastAsync();
            return;
        }

        Dialog = new AdminSpaceDialogViewModel(this);
    }

    private async Task IncludeAdminSpaceWithToastAsync()
    {
        PrivilegedResult<HiddenSpaceReport> result = await IncludeAdminSpaceCoreAsync(promptIfNeeded: false);
        if (result is { Outcome: PrivilegedOutcome.Done, Value: { } report } && (report.RestorePoints is not null || report.NtfsMetadata is not null))
        {
            ShowToast(string.Format(Strings.AdminSpaceToast, ByteSize.Format(report.Total)));
            ShowStorageCategory(StorageCategory.HiddenSystem);
        }
        else
        {
            ShowToast(PrivilegedText.Reason(result.Reason));
        }
    }

    /// <summary>Offers to measure the folders of <paramref name="summary"/> the person cannot list; with access already on, it just does.</summary>
    public void RequestLockedMeasure(StorageSummaryViewModel summary)
    {
        if (_services.Privileged is null || !summary.IsComplete)
        {
            return;
        }

        if (Access.IsActive)
        {
            AccessWork = MeasureLockedWithToastAsync(summary);
            return;
        }

        Dialog = new LockedMeasureDialogViewModel(this, summary);
    }

    private async Task MeasureLockedWithToastAsync(StorageSummaryViewModel summary)
    {
        LockedMeasure result = await MeasureLockedCoreAsync(summary, promptIfNeeded: false);
        ShowToast(result.Outcome == PrivilegedOutcome.Done ? result.Toast : PrivilegedText.Reason(result.Reason));
    }

    /// <summary>
    /// Measures the snapshots and file system bookkeeping of the system drive with an administrator's rights (called by the
    /// dialog) and merges them into the hidden space already measured: a number the administrator read replaces the same
    /// estimate, one it could not read leaves the estimate where it was. Nothing is added when nothing was read.
    /// </summary>
    public async Task IncludeAdminSpaceAsync(AdminSpaceDialogViewModel dialog)
    {
        if (SystemDrive is null || _services.Privileged is null)
        {
            CloseDialog();
            return;
        }

        PrivilegedResult<HiddenSpaceReport> result = await IncludeAdminSpaceCoreAsync(promptIfNeeded: true);
        switch (result.Outcome)
        {
            case PrivilegedOutcome.Done when result.Value is { RestorePoints: not null } or { NtfsMetadata: not null }:
                CloseDialog();
                ShowToast(string.Format(Strings.AdminSpaceToast, ByteSize.Format(result.Value.Total)));
                ShowStorageCategory(StorageCategory.HiddenSystem);
                RefreshPages();
                break;
            case PrivilegedOutcome.Cancelled:
                dialog.Cancelled();
                break;
            case PrivilegedOutcome.NotAvailable:
                dialog.Unavailable(result.Reason);
                break;
            default:
                dialog.Failed();
                break;
        }
    }

    /// <summary>
    /// Measures the system drive's administrator-only space and, when something was read, merges it into the totals. A measurement already
    /// running (the dialog's, while the session it started announces itself) is joined, never repeated.
    /// </summary>
    private Task<PrivilegedResult<HiddenSpaceReport>> IncludeAdminSpaceCoreAsync(bool promptIfNeeded)
    {
        if (_spaceMeasure is { } running)
        {
            return running;
        }

        var done = new TaskCompletionSource<PrivilegedResult<HiddenSpaceReport>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _spaceMeasure = done.Task;
        _ = Run();
        return done.Task;

        async Task Run()
        {
            try
            {
                done.SetResult(await MeasureAdminSpaceAsync(promptIfNeeded));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                _spaceMeasure = null;
            }
        }
    }

    private async Task<PrivilegedResult<HiddenSpaceReport>> MeasureAdminSpaceAsync(bool promptIfNeeded)
    {
        if (SystemDrive is not { } system || _services.Privileged is not { } privileged)
        {
            return PrivilegedResult<HiddenSpaceReport>.Failed();
        }

        string root = system.Summary.RootPath;
        PrivilegedResult<HiddenSpaceReport> result;
        try
        {
            result = await privileged.MeasureSystemSpaceAsync(root, CancellationToken.None, promptIfNeeded);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
            return PrivilegedResult<HiddenSpaceReport>.Failed();
        }

        if (result is { Outcome: PrivilegedOutcome.Done, Value: { RestorePoints: not null } or { NtfsMetadata: not null } })
        {
            HiddenSpaceReport merged = MergeAdminSpace(_hidden.GetValueOrDefault(root), result.Value);
            _hidden[root] = merged;
            _adminSpace.Add(root);
            IsUnlocked = true;
            SystemSummary?.ApplyHiddenSpace(merged, includesAdminSpace: true);
            RefreshPages();
        }

        return result;
    }

    /// <summary>The hidden space with what the administrator measured: per field the new number wins when there is one.</summary>
    internal static HiddenSpaceReport MergeAdminSpace(HiddenSpaceReport? measuredBefore, HiddenSpaceReport privileged) => new(
        privileged.RestorePoints ?? measuredBefore?.RestorePoints,
        privileged.NtfsMetadata ?? measuredBefore?.NtfsMetadata);

    /// <summary>How measuring the locked folders of one drive ended.</summary>
    /// <param name="Added">Folders whose size is now in the tree.</param>
    /// <param name="Incomplete">Of those, how many are lower bounds (part of them could not be read).</param>
    private sealed record LockedMeasure(PrivilegedOutcome Outcome, PrivilegedUnavailableReason? Reason, int Added, int Incomplete, long Bytes)
    {
        public string Toast
        {
            get
            {
                if (Added == 0)
                {
                    return Strings.LockedMeasuredNone;
                }

                string added = string.Format(Added == 1 ? Strings.LockedMeasuredToastOne : Strings.LockedMeasuredToastMany, Added, ByteSize.Format(Bytes));
                return Incomplete > 0 ? $"{added} {Strings.LockedMeasuredIncomplete}" : added;
            }
        }
    }

    /// <summary>
    /// Measures the folders the scan could not list, with an administrator's rights (called by the dialog), and rebuilds the
    /// drive's tree with their sizes so the categories and "not measured yet" add up again. Folders the helper did not answer
    /// for stay locked.
    /// </summary>
    public async Task MeasureLockedAsync(LockedMeasureDialogViewModel dialog)
    {
        if (_services.Privileged is null || dialog.Summary.Tree is null)
        {
            CloseDialog();
            return;
        }

        LockedMeasure result = await MeasureLockedCoreAsync(dialog.Summary, promptIfNeeded: true);
        switch (result.Outcome)
        {
            case PrivilegedOutcome.Done when result.Added > 0:
                CloseDialog();
                ShowToast(result.Toast);
                break;
            case PrivilegedOutcome.Done when dialog.Summary.LockedCount == 0:
                CloseDialog(); // Nothing was left to measure.
                break;
            case PrivilegedOutcome.Cancelled:
                dialog.Cancelled();
                break;
            case PrivilegedOutcome.NotAvailable:
                dialog.Unavailable(result.Reason);
                break;
            default:
                dialog.Failed();
                break;
        }
    }

    /// <summary>
    /// Asks the helper for the size of every locked folder of the summary's tree and, when sizes came back, rebuilds the tree with them.
    /// A measurement of the same drive already running (the dialog's, while the session it started announces itself) is joined, never repeated.
    /// </summary>
    private Task<LockedMeasure> MeasureLockedCoreAsync(StorageSummaryViewModel summary, bool promptIfNeeded)
    {
        if (_lockedMeasures.TryGetValue(summary, out Task<LockedMeasure>? running))
        {
            return running;
        }

        var done = new TaskCompletionSource<LockedMeasure>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lockedMeasures[summary] = done.Task;
        _ = Run();
        return done.Task;

        async Task Run()
        {
            try
            {
                done.SetResult(await MeasureLockedFoldersAsync(summary, promptIfNeeded));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                _lockedMeasures.Remove(summary);
            }
        }
    }

    private async Task<LockedMeasure> MeasureLockedFoldersAsync(StorageSummaryViewModel summary, bool promptIfNeeded)
    {
        if (_services.Privileged is not { } privileged || summary.Tree is not { } tree)
        {
            return new LockedMeasure(PrivilegedOutcome.Failed, null, 0, 0, 0);
        }

        string[] paths = [.. tree.GetLockedFolders().Select(node => node.FullPath)];
        if (paths.Length == 0)
        {
            return new LockedMeasure(PrivilegedOutcome.Done, null, 0, 0, 0);
        }

        PrivilegedResult<IReadOnlyList<MeasuredFolder>> result;
        try
        {
            result = await privileged.MeasureFoldersAsync(paths, CancellationToken.None, promptIfNeeded);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
            return new LockedMeasure(PrivilegedOutcome.Failed, null, 0, 0, 0);
        }

        if (result.Outcome != PrivilegedOutcome.Done || result.Value is not { } folders)
        {
            return new LockedMeasure(result.Outcome, result.Reason, 0, 0, 0);
        }

        var asked = paths.ToHashSet(StringComparer.Ordinal);
        var sizes = new Dictionary<string, FolderMeasurement>(StringComparer.Ordinal);
        int incomplete = 0;
        foreach (MeasuredFolder folder in folders.Where(folder => asked.Contains(folder.Path)))
        {
            sizes[folder.Path] = new FolderMeasurement(folder.SizeOnDisk, folder.LogicalSize, folder.Files);
            incomplete += folder.Complete ? 0 : 1;
        }

        if (sizes.Count == 0)
        {
            summary.MarkAdminMeasured(); // The administrator was asked and had no size for any of them: they stay closed, and that is said.
            return new LockedMeasure(PrivilegedOutcome.Failed, null, 0, 0, 0);
        }

        if (ReferenceEquals(summary.Tree, tree))
        {
            // A rescan that finished while the prompt was open has a newer tree; its folders are not these.
            summary.ApplyResult(tree.WithMeasuredFolders(sizes), summary.CompletedAt);
            summary.MarkAdminMeasured();
            RefreshPages();
        }

        return new LockedMeasure(PrivilegedOutcome.Done, null, sizes.Count, incomplete, sizes.Values.Sum(size => size.SizeOnDisk));
    }

    StorageTree? IFolderHost.TreeFor(DriveViewModel drive) =>
        _summaries.GetValueOrDefault(drive.Summary.RootPath) is { IsComplete: true } summary ? summary.Tree : null;

    void IFolderHost.ScanDrive(DriveViewModel drive) => ShowDrive(drive);

    void IFolderHost.ShowInExplorer(string path)
    {
        _services.Explorer.ShowInExplorer(path);
        ShowToast(string.Format(Localizer.Instance.Resolve(Strings.ExplorerToast), path));
    }

    /// <summary>The storage category of the folder an executable lives in.</summary>
    StorageCategory? IMemoryHost.CategoryOf(string imagePath)
    {
        string? folder = Path.GetDirectoryName(imagePath);
        if (folder is null || Path.GetPathRoot(folder) is not { Length: > 0 } root)
        {
            return null;
        }

        StorageCategory category = _services.Classifier.Classify(root, StorageCategory.Other);
        string path = root;
        foreach (string segment in folder[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            path = Path.Join(path, segment);
            category = _services.Classifier.Classify(path, category);
        }

        return category;
    }

    [RelayCommand]
    private void GoHome() => Navigate(Page.Home);

    [RelayCommand]
    private void ShowStorage()
    {
        if (CurrentSummary is null && SystemDrive is { } system)
        {
            SelectedDrive = system;
        }

        Navigate(Page.Storage);
    }

    [RelayCommand]
    private void GoCleanup() => Navigate(Page.Cleanup);

    [RelayCommand]
    private void GoMemory() => Navigate(Page.Memory);

    /// <summary>Folders opens the selected drive when its scan is done, otherwise "This PC".</summary>
    [RelayCommand]
    private void ShowFolders()
    {
        if (Page != Page.Folders)
        {
            Folders.Show(CurrentSummary is { IsComplete: true, Tree: { } tree } ? tree.Root : null, resetHistory: true);
        }

        Navigate(Page.Folders);
    }

    [RelayCommand]
    private void GoSpecs()
    {
        Navigate(Page.Specs);
        _ = Specs.EnsureLoaded();
    }

    [RelayCommand]
    private void GoStress() => Navigate(Page.Stress);

    [RelayCommand]
    private void GoAsk()
    {
        if (Ask is { } ask)
        {
            Navigate(Page.Ask);
            _ = ask.EnterAsync();
        }
    }

    /// <summary>"Ask about this" on another screen: opens Ask with the question ready, about that topic.</summary>
    public void AskAbout(AssistantTopic topic, string question)
    {
        if (Ask is { } ask)
        {
            ask.AskAbout(topic, question);
            GoAsk();
        }
    }

    AssistantSettings IAskHost.AssistantSettings => _settings.Assistant;

    void IAskHost.SaveAssistantSettings(AssistantSettings settings) => SaveSettings(_settings with { Assistant = settings });

    /// <summary>What the assistant may know about this PC: everything the app has measured so far.</summary>
    public async Task<PcSnapshot> SnapshotAsync()
    {
        try
        {
            await Specs.EnsureLoaded();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // The specs are optional; the assistant answers from the rest.
        }

        StorageTree? tree = SystemSummary is { IsComplete: true, Tree: { } t } ? t : null;
        return new PcSnapshot(
            Specs.Specs,
            [.. _recentSamples],
            [.. Drives.Select(d => new DriveFact(d.Summary.RootPath, d.Summary.Label, d.Summary.TotalBytes, d.Summary.FreeBytes, d == SystemDrive))],
            tree is null ? null : StorageCategories.All.ToDictionary(c => c, tree.GetCategoryBytes),
            [.. Cleanup.Items.Select(i => i.Estimate)],
            IsUnlocked);
    }

    /// <summary>The screen or dialog an assistant answer points to.</summary>
    public void Open(AssistantAction action)
    {
        switch (action.Kind)
        {
            case ActionKind.Cleanup:
                Navigate(Page.Cleanup);
                break;
            case ActionKind.CleanupItem:
                Navigate(Page.Cleanup);
                if (Cleanup.Items.FirstOrDefault(i => i.Rule.Id == action.Argument) is { } item)
                {
                    Cleanup.OpenPreviewCommand.Execute(item);
                }

                break;
            case ActionKind.Memory:
                Navigate(Page.Memory);
                break;
            case ActionKind.CloseApp:
                Navigate(Page.Memory);
                Memory.Update();
                if (Memory.Apps.FirstOrDefault(a => a.Key == action.Argument) is { } row)
                {
                    Memory.CloseCommand.Execute(row);
                }

                break;
            case ActionKind.Storage:
                ShowStorageCommand.Execute(null);
                break;
            case ActionKind.Folders:
                ShowFoldersCommand.Execute(null);
                break;
            case ActionKind.Specs:
                GoSpecsCommand.Execute(null);
                break;
            case ActionKind.Unlock:
                RequestUnlock();
                break;
        }
    }

    public string? AppName(string key) =>
        Live.Apps.FirstOrDefault(a => a.Key == key) is { } app
            ? app.Explanation?.Title.Get(Localizer.Instance.Culture) ?? app.Description ?? app.Key
            : null;

    [RelayCommand]
    private void SetSimpleMode() => Is3d = false;

    [RelayCommand]
    private void Set3dMode()
    {
        if (Page == Page.Welcome)
        {
            Navigate(Page.Home);
        }

        Is3d = true;
        Pc.Refresh();
        _ = ShowDisksIn3dAsync();
    }

    /// <summary>The 3D PC draws the real disks once the specs have been read.</summary>
    private async Task ShowDisksIn3dAsync()
    {
        try
        {
            await Specs.EnsureLoaded();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Without the specs the 3D PC keeps its one generic drive.
            return;
        }

        Pc.Refresh();
    }

    [RelayCommand]
    private void SetEnglish() => SetLanguage(LanguagePreference.English);

    [RelayCommand]
    private void SetPortuguese() => SetLanguage(LanguagePreference.Portuguese);

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = IsDarkTheme == false;
        SaveSettings(_settings with { Theme = IsDarkTheme == true ? ThemePreference.Dark : ThemePreference.Light });
    }

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private void Rescan()
    {
        if (SelectedDrive is { } drive)
        {
            CurrentScan = ShowDriveAsync(drive, forceRescan: true);
        }
    }

    private bool CanRescan() => !IsScanning;

    [RelayCommand]
    private void Unlock() => RequestUnlock();

    private void Navigate(Page page)
    {
        Is3d = false;
        Page = page;
        if (page == Page.Home)
        {
            Home.Refresh();
        }
    }

    private async Task ShowDriveAsync(DriveViewModel drive, bool forceRescan)
    {
        _scanCancellation?.Cancel();
        string root = drive.Summary.RootPath;

        if (!forceRescan && _summaries.TryGetValue(root, out var existing))
        {
            CurrentSummary = existing;
            return;
        }

        // What the screen showed before this attempt: a failed rescan must not replace it with a
        // half-built summary.
        StorageSummaryViewModel? previous = _summaries.GetValueOrDefault(root);

        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        var summary = new StorageSummaryViewModel(
            drive.Summary, _services.Locations, OpenFolder, RequestUnlock, isSystemDrive: drive == SystemDrive,
            privileged: _services.Privileged, requestAdminSpace: RequestAdminSpace, requestLockedMeasure: RequestLockedMeasure, access: Access);
        if (_hidden.TryGetValue(root, out var hidden))
        {
            summary.ApplyHiddenSpace(hidden, _adminSpace.Contains(root));
        }

        _summaries[root] = summary;
        CurrentSummary = summary;
        IsScanning = true;

        var progress = new Progress<ScanProgress>(p =>
        {
            summary.ApplyProgress(p);
            if (_scanCancellation == cancellation)
            {
                OnPropertyChanged(nameof(StatusText));
            }
        });

        try
        {
            StorageTree tree = await _services.Scanner.ScanAsync(root, _services.Classifier, progress, cancellation.Token);
            summary.ApplyResult(tree, _clock());
            RefreshPages();
            if (Access.IsActive)
            {
                // Administrator access is on: this drive's locked folders are measured as soon as they are known.
                AccessWork = MeasureLockedCoreAsync(summary, promptIfNeeded: false);
            }
        }
        catch (OperationCanceledException)
        {
            // Forget the partial result so the drive is scanned again next time.
            if (_summaries.GetValueOrDefault(root) == summary)
            {
                _summaries.Remove(root);
            }
        }
        catch (Exception ex)
        {
            // A scan fails for reasons the person cannot control: a drive that went away, a folder
            // deleted mid-walk, permissions. Tell them, put back what was on screen before and let a
            // retry rescan, instead of leaving an empty screen with nothing to click.
            Debug.WriteLine(ex);
            if (_summaries.GetValueOrDefault(root) == summary)
            {
                if (previous is null)
                {
                    _summaries.Remove(root);
                }
                else
                {
                    _summaries[root] = previous;
                    CurrentSummary = previous;
                }
            }

            ShowToast(string.Format(Strings.ScanFailed, drive.Name));
        }
        finally
        {
            if (_scanCancellation == cancellation)
            {
                IsScanning = false;
            }
        }
    }

    private void OpenFolder(StorageNode node)
    {
        Folders.Show(node, resetHistory: true);
        Navigate(Page.Folders);
    }

    private void RefreshPages()
    {
        Home.Refresh();
        Pc.Refresh();
        Folders.Refresh();
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SystemDriveStatus));
        OnPropertyChanged(nameof(RamStatus));
        OnPropertyChanged(nameof(CpuStatus));
    }

    private void RefreshTexts()
    {
        foreach (DriveViewModel drive in Drives)
        {
            drive.Refresh();
        }

        foreach (StorageSummaryViewModel summary in _summaries.Values)
        {
            summary.RefreshTexts();
        }

        Cleanup.RefreshTexts();
        (Dialog as PrivilegedDialogViewModel)?.RefreshTexts();
        Access.RefreshTexts();
        Memory.RefreshTexts();
        Specs.RefreshTexts();
        Stress.RefreshTexts();
        Ask?.RefreshTexts();
        Welcome.Refresh();
        RefreshPages();
        OnPropertyChanged(nameof(IsPortuguese));
        OnPropertyChanged(nameof(IsEnglish));
        OnPropertyChanged(nameof(ThemeLabel));
        OnPropertyChanged(nameof(StatusShort));
    }

    private void SaveSettings(AppSettings settings)
    {
        _settings = settings;
        _services.Settings.Save(settings);
    }

    private async Task HideToastAsync(int version)
    {
        await Task.Delay(ToastDuration);
        if (version == _toastVersion)
        {
            Toast = null;
        }
    }

    private string Ago(DateTimeOffset at)
    {
        TimeSpan elapsed = _clock() - at;
        return elapsed.TotalMinutes < 1 ? Strings.JustNow
            : elapsed.TotalHours < 1 ? string.Format(Strings.MinutesAgo, (int)elapsed.TotalMinutes)
            : string.Format(Strings.HoursAgo, (int)elapsed.TotalHours);
    }
}

public sealed partial class DriveViewModel(DriveSummary summary) : ObservableObject
{
    public DriveSummary Summary { get; } = summary;

    /// <summary>The label with the drive's token, or the label alone when it is the token (a mount point with no volume label).</summary>
    public string Name => Label == Token ? Label : $"{Label} ({Token})";

    /// <summary>What the drive is called in short: "C:" for <c>C:\</c>, "/home" for <c>/home</c>, "/" for the root.</summary>
    public string Token => TokenOf(Summary.RootPath);

    /// <summary>The volume label, or "Local Disk" when the drive has none.</summary>
    public string Label => string.IsNullOrWhiteSpace(Summary.Label) ? Localizer.Instance.Resolve(Strings.LocalDisk) : Summary.Label;

    /// <summary>The label to show beside the token: nothing when it would only repeat it.</summary>
    public string Caption => Label == Token ? "" : Label;

    public static string TokenOf(string root) => root.TrimEnd('\\', '/') is { Length: > 0 } trimmed ? trimmed : root;

    public string UsedPercentText => UsedFraction.ToString("P0");

    public string UsedText => string.Format(Strings.DriveUsed, ByteSize.Format(Summary.UsedBytes), ByteSize.Format(Summary.TotalBytes));

    public double UsedFraction => Summary.TotalBytes == 0 ? 0 : (double)Summary.UsedBytes / Summary.TotalBytes;

    public void Refresh() => OnPropertyChanged(string.Empty);
}
