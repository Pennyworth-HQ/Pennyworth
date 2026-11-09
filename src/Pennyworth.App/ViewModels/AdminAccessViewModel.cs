using System.Globalization;
using Pennyworth.App.Resources;
using Pennyworth.Core.Privilege;
using Pennyworth.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>
/// The one "Administrator access" concept of the window: Off, Active (with Lock) or Needs setup. Everything that needs an
/// administrator asks the same session, so this is also the only place that says where it stands. Null access (Windows, which
/// elevates the whole app instead) means <see cref="IsSupported"/> is false and nothing shows.
/// </summary>
public sealed partial class AdminAccessViewModel : ObservableObject
{
    private readonly IAdministratorAccess? _access;
    private readonly MainWindowViewModel _shell;
    private readonly SynchronizationContext? _context;
    private AdministratorAccessState _lastState;

    public AdminAccessViewModel(IAdministratorAccess? access, MainWindowViewModel shell)
    {
        _access = access;
        _shell = shell;
        _context = SynchronizationContext.Current;
        _lastState = access?.State ?? AdministratorAccessState.Unavailable;
        if (access is not null)
        {
            access.StateChanged += OnStateChanged;
        }
    }

    /// <summary>Raised on the UI thread after the state changed, with the new state and the one before.</summary>
    public event Action<AdministratorAccessState, AdministratorAccessState>? Changed;

    /// <summary>Whether this platform has administrator access at all; the window shows nothing about it otherwise.</summary>
    public bool IsSupported => _access is not null;

    public AdministratorAccessState State => _access?.State ?? AdministratorAccessState.Unavailable;

    public PrivilegedUnavailableReason? Reason => _access?.Reason;

    public bool IsOff => State == AdministratorAccessState.Off;

    public bool IsActive => State == AdministratorAccessState.Active;

    public bool NeedsSetup => State == AdministratorAccessState.NeedsSetup;

    public bool IsUnavailable => State == AdministratorAccessState.Unavailable;

    /// <summary>"Administrator access: Off".</summary>
    public string StatusText => string.Format(CultureInfo.CurrentCulture, Strings.AccessStatus, Strings.AccessTitle, State switch
    {
        AdministratorAccessState.Active => Strings.AccessStateActive,
        AdministratorAccessState.NeedsSetup => Strings.AccessStateNeedsSetup,
        AdministratorAccessState.Unavailable => Strings.AccessStateUnavailable,
        _ => Strings.AccessStateOff,
    });

    /// <summary>What a click on the status does, in a sentence.</summary>
    public string Tip => State switch
    {
        AdministratorAccessState.Active => string.Format(CultureInfo.CurrentCulture, Strings.AccessTipActive, IdleMinutes),
        AdministratorAccessState.NeedsSetup => Strings.AccessTipNeedsSetup,
        AdministratorAccessState.Unavailable => PrivilegedText.Reason(Reason),
        _ => Strings.AccessTipOff,
    };

    /// <summary>How many idle minutes lock an active session, for the texts that say so.</summary>
    public int IdleMinutes => (int)Math.Round((_access?.IdleTimeout ?? TimeSpan.FromMinutes(15)).TotalMinutes);

    /// <summary>The status control: turns access on, or opens the setup, or (while active) does nothing but say so.</summary>
    [RelayCommand]
    private void Primary()
    {
        switch (State)
        {
            case AdministratorAccessState.Off:
                _shell.RequestTurnOn();
                break;
            case AdministratorAccessState.NeedsSetup:
                SetUp();
                break;
        }
    }

    /// <summary>Shows what the one-time setup installs; after it, offers to turn access on.</summary>
    [RelayCommand]
    private void SetUp() => _ = _shell.RequestSetup();

    /// <summary>Ends the session now; the next privileged action asks for the password again.</summary>
    [RelayCommand]
    private void Lock() => _shell.LockAccess();

    /// <summary>Re-reads every text after a language switch.</summary>
    internal void RefreshTexts() => OnPropertyChanged(string.Empty);

    private void OnStateChanged()
    {
        if (_context is null)
        {
            Apply();
        }
        else
        {
            _context.Post(_ => Apply(), null);
        }
    }

    private void Apply()
    {
        AdministratorAccessState before = _lastState;
        _lastState = State;
        OnPropertyChanged(string.Empty);
        Changed?.Invoke(_lastState, before);
    }
}
