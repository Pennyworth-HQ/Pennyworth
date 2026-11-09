using System.Globalization;
using Pennyworth.App.Resources;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Privilege;
using Pennyworth.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

public enum PrivilegedDialogState
{
    /// <summary>Says what will be read and asks the person to go ahead.</summary>
    Asking,

    /// <summary>The system's password prompt is open.</summary>
    Waiting,

    /// <summary>The prompt was dismissed: nothing was done, and the person may ask again.</summary>
    Cancelled,

    /// <summary>There is no way to ask on this machine; the sentence says why.</summary>
    Unavailable,

    /// <summary>It was asked for and did not work.</summary>
    Failed,
}

/// <summary>
/// A request that needs administrator access: first what will be read and what will not be touched, then the wait for the
/// system's own prompt, then a calm message when it did not happen. One shape for every such request, so the window has a
/// single template; each subclass says its own words and what pressing the button runs. When the helper that serves
/// administrator access is not installed yet, the same dialog explains that and offers the one-time setup; a request that
/// can also run once without it (<see cref="RequiresHelper"/> false) still offers to.
/// </summary>
public abstract partial class PrivilegedDialogViewModel(MainWindowViewModel shell) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsking), nameof(IsIdle), nameof(IsWaiting), nameof(IsMessage), nameof(Message), nameof(ShowSetupOffer), nameof(ShowAsk), nameof(OffersSetupAfterMessage))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial PrivilegedDialogState State { get; private set; }

    /// <summary>Why the request cannot be made, with <see cref="PrivilegedDialogState.Unavailable"/>.</summary>
    public PrivilegedUnavailableReason? Reason { get; private set; }

    /// <summary>The request started by <see cref="StartCommand"/>; lets tests await it.</summary>
    public Task Run { get; private set; } = Task.CompletedTask;

    public abstract string Title { get; }

    /// <summary>Why this is being asked, and that the system (not the app) takes the password.</summary>
    public abstract string Why { get; }

    /// <summary>What will be read.</summary>
    public abstract string Reads { get; }

    /// <summary>What will not be touched.</summary>
    public abstract string Wont { get; }

    /// <summary>The request cannot run at all without the helper (it has no one-off way); with no helper installed only the setup moves forward.</summary>
    public virtual bool RequiresHelper => false;

    /// <summary>The helper is not installed, trusted or current: administrator access needs its one-time setup.</summary>
    public virtual bool NeedsSetup => Shell.Access.NeedsSetup;

    /// <summary>Asking, and the one-time setup is what is missing: its button and its sentence are on screen.</summary>
    public bool ShowSetupOffer => IsIdle && NeedsSetup;

    /// <summary>The button that goes ahead is on screen: not when the only way forward is the setup.</summary>
    public bool ShowAsk => IsIdle && !(NeedsSetup && RequiresHelper);

    /// <summary>"Ask for my password" normally; "Ask just this once" while the setup is still missing.</summary>
    public virtual string AskText => NeedsSetup ? Strings.PrivAskOnce : Strings.PrivAsk;

    /// <summary>Why the setup is offered, and what the one-off way would mean.</summary>
    public string? SetupNote => NeedsSetup ? (RequiresHelper ? Strings.PrivNeedsSetup : Strings.PrivNeedsSetupOneOff) : null;

    /// <summary>A message about a helper that turned out to be missing or old also offers the setup.</summary>
    public bool OffersSetupAfterMessage => IsMessage && NeedsSetup;

    /// <summary>Asking or waiting: the explanation is on screen.</summary>
    public bool IsAsking => State is PrivilegedDialogState.Asking or PrivilegedDialogState.Waiting;

    /// <summary>Asking and not yet pressed: the buttons that begin or decline are shown.</summary>
    public bool IsIdle => State == PrivilegedDialogState.Asking;

    public bool IsWaiting => State == PrivilegedDialogState.Waiting;

    /// <summary>Cancelled, unavailable or failed: the one message is on screen.</summary>
    public bool IsMessage => State is PrivilegedDialogState.Cancelled or PrivilegedDialogState.Unavailable or PrivilegedDialogState.Failed;

    /// <summary>The calm sentence for the non-asking states; null while asking.</summary>
    public string? Message => State switch
    {
        PrivilegedDialogState.Cancelled => Strings.PrivCancelled,
        PrivilegedDialogState.Unavailable => PrivilegedText.Reason(Reason),
        PrivilegedDialogState.Failed => FailureText,
        _ => null,
    };

    /// <summary>The sentence for <see cref="PrivilegedDialogState.Failed"/>.</summary>
    protected virtual string FailureText => Strings.PrivFailed;

    /// <summary>The shell that does the work and closes the dialog.</summary>
    protected MainWindowViewModel Shell => shell;

    /// <summary>Starts the request and returns the work to await; called with the state already <see cref="PrivilegedDialogState.Waiting"/>.</summary>
    protected abstract Task StartAsync();

    /// <summary>Opens the request again once the setup has finished (the person came here to do something; the setup was a detour).</summary>
    protected virtual void Reopen()
    {
    }

    /// <summary>Re-reads the texts after a language switch or a change of the access state.</summary>
    internal void RefreshTexts() => OnPropertyChanged(string.Empty);

    internal void Cancelled() => State = PrivilegedDialogState.Cancelled;

    internal void Failed() => State = PrivilegedDialogState.Failed;

    internal void Unavailable(PrivilegedUnavailableReason? reason)
    {
        Reason = reason;
        State = PrivilegedDialogState.Unavailable;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Start()
    {
        State = PrivilegedDialogState.Waiting;
        Run = StartAsync();
    }

    /// <summary>Back to the explanation after a cancelled or failed attempt.</summary>
    [RelayCommand]
    private void TryAgain() => State = PrivilegedDialogState.Asking;

    /// <summary>The one-time setup of the helper, then this request again.</summary>
    [RelayCommand]
    private void SetUp() => Run = Shell.RequestSetup(Reopen);

    [RelayCommand]
    private void NotNow() => shell.CloseDialog();

    [RelayCommand]
    private void Close() => shell.CloseDialog();
}

/// <summary>"Turn on administrator access?": the one prompt that covers every privileged action of the session.</summary>
public sealed class AccessDialogViewModel(MainWindowViewModel shell, Func<Task>? afterActivate = null) : PrivilegedDialogViewModel(shell)
{
    internal Func<Task>? AfterActivate { get; } = afterActivate;

    public override string Title => Strings.AccessDialogTitle;

    public override string Why => string.Format(CultureInfo.CurrentCulture, Strings.AccessDialogWhy, Shell.Access.IdleMinutes);

    public override string Reads => Strings.AccessDialogReads;

    public override string Wont => Strings.AccessDialogWont;

    public override bool RequiresHelper => true;

    public override string AskText => Strings.AccessTurnOn;

    protected override Task StartAsync() => Shell.TurnOnAccessAsync(this);

    protected override void Reopen() => Shell.RequestTurnOn(AfterActivate);
}

/// <summary>
/// "Set up administrator access?": exactly which two files will be installed and where, what is checked, what is not
/// touched. Built after the app has read and fingerprinted the files; with no plan it only says why there is nothing to install.
/// </summary>
/// <param name="plan">What will be installed; null when this copy of the app has nothing to install (the reason is then set).</param>
/// <param name="then">Run when the setup finished: the request the person came from, opened again.</param>
public sealed class SetupDialogViewModel(MainWindowViewModel shell, AdministratorSetupPlan? plan, Action? then) : PrivilegedDialogViewModel(shell)
{
    public AdministratorSetupPlan? Plan { get; } = plan;

    /// <summary>Run after a finished setup.</summary>
    internal Action? Then { get; } = then;

    public override string Title => Strings.AdminSetupTitle;

    public override string Why => Strings.AdminSetupWhy;

    public override string Reads => Plan is { } plan
        ? string.Join(
            '\n',
            Strings.AdminSetupReads,
            string.Format(CultureInfo.CurrentCulture, Strings.AdminSetupHelperLine, plan.HelperDestination, ByteSize.Format(plan.HelperBytes)),
            string.Format(CultureInfo.CurrentCulture, Strings.AdminSetupPolicyLine, plan.PolicyDestination),
            string.Format(CultureInfo.CurrentCulture, Strings.AdminSetupFingerprints, Fingerprint(plan.HelperSha256), Fingerprint(plan.PolicySha256)))
        : "";

    public override string Wont => Strings.AdminSetupWont;

    public override string AskText => Strings.AdminSetupGo;

    protected override string FailureText => Strings.AdminSetupFailed;

    /// <summary>This dialog is the setup: it never offers itself.</summary>
    public override bool NeedsSetup => false;

    /// <summary>The first 16 hex digits, grouped: enough to compare by eye with the release's published value.</summary>
    internal static string Fingerprint(string sha256) =>
        string.Join(' ', Enumerable.Range(0, 4).Select(i => sha256.Substring(i * 4, 4)));

    protected override Task StartAsync() => Shell.SetupAccessAsync(this);
}

/// <summary>"Include what needs administrator access?": snapshots and file system bookkeeping of the system drive.</summary>
public sealed class AdminSpaceDialogViewModel(MainWindowViewModel shell) : PrivilegedDialogViewModel(shell)
{
    public override string Title => Strings.AdminSpaceTitle;

    public override string Why => Strings.AdminSpaceWhy;

    public override string Reads => Strings.AdminSpaceReads;

    public override string Wont => Strings.AdminSpaceWont;

    protected override Task StartAsync() => Shell.IncludeAdminSpaceAsync(this);

    protected override void Reopen() => Shell.RequestAdminSpace();
}

/// <summary>"Measure the folders I couldn't read?": the size, and only the size, of what a person cannot list.</summary>
/// <param name="summary">The drive whose unreadable folders are measured.</param>
public sealed class LockedMeasureDialogViewModel(MainWindowViewModel shell, StorageSummaryViewModel summary) : PrivilegedDialogViewModel(shell)
{
    public StorageSummaryViewModel Summary { get; } = summary;

    public override string Title => Strings.LockedMeasureTitle;

    public override string Why => Strings.LockedMeasureWhy;

    public override string Reads => Strings.LockedMeasureReads;

    public override string Wont => Strings.LockedMeasureWont;

    /// <summary>There is no one-off way to measure folders: nothing arbitrary runs as root.</summary>
    public override bool RequiresHelper => true;

    protected override Task StartAsync() => Shell.MeasureLockedAsync(this);

    protected override void Reopen() => Shell.RequestLockedMeasure(Summary);
}
