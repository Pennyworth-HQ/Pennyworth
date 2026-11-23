using Pennyworth.Core.Privilege;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// The broker behind <see cref="IAdministratorAccess"/> on Linux: decides whether a request can go to the helper's session
/// (running, or startable because the helper is installed and trusted), starts it on the first request that may ask, and runs the
/// one-time <see cref="HelperSetup"/>. Everything privileged in the app reaches the helper through <see cref="CallAsync"/>, so the
/// rules (one prompt, no automatic retry, restart on the next explicit action) are written once.
/// </summary>
internal sealed class LinuxAdministratorAccess : IAdministratorAccess, IDisposable
{
    internal static readonly string[] Pkexec = ["/usr/bin/pkexec", "/bin/pkexec"];

    private readonly ElevationSession _session;
    private readonly HelperSetup _setup;
    private readonly Func<string, bool> _exists;
    private readonly Func<string, EntryInfo> _lstat;
    private volatile bool _outdated;

    internal LinuxAdministratorAccess(ElevationSession session, HelperSetup setup, Func<string, bool> exists, Func<string, EntryInfo> lstat)
    {
        _session = session;
        _setup = setup;
        _exists = exists;
        _lstat = lstat;
        _session.Changed += () => StateChanged?.Invoke();
    }

    public event Action? StateChanged;

    public TimeSpan IdleTimeout => HelperProtocol.IdleTimeout;

    public AdministratorAccessState State => Evaluate().State;

    public PrivilegedUnavailableReason? Reason => Evaluate().Reason;

    /// <summary>
    /// Whether a request may go to the helper's session now: it is running, or it can be started (installed, trusted, not known to be
    /// outdated). Otherwise the reason it cannot, with the same vocabulary the person sees.
    /// </summary>
    internal PrivilegedCapability HelperCapability()
    {
        if (Find(Pkexec) is null)
        {
            return PrivilegedCapability.Missing(PrivilegedUnavailableReason.NoPkexec);
        }

        if (_session.IsActive)
        {
            return PrivilegedCapability.Ready;
        }

        PrivilegedCapability trust = HelperTrust.Evaluate(_lstat);
        if (!trust.Available)
        {
            return trust;
        }

        return _outdated ? PrivilegedCapability.Missing(PrivilegedUnavailableReason.HelperOutdated) : PrivilegedCapability.Ready;
    }

    private (AdministratorAccessState State, PrivilegedUnavailableReason? Reason) Evaluate()
    {
        if (_session.IsActive)
        {
            return (AdministratorAccessState.Active, null);
        }

        PrivilegedCapability capability = HelperCapability();
        if (capability.Available)
        {
            return (AdministratorAccessState.Off, null);
        }

        return capability.Reason == PrivilegedUnavailableReason.NoPkexec
            ? (AdministratorAccessState.Unavailable, capability.Reason)
            : (AdministratorAccessState.NeedsSetup, capability.Reason);
    }

    /// <summary>
    /// Sends one request to the session. With no session running it starts one (one password prompt) only when
    /// <paramref name="allowPrompt"/>; a dismissed prompt is <see cref="PrivilegedOutcome.Cancelled"/> and is not repeated here.
    /// An answer is <see cref="PrivilegedOutcome.Done"/> whatever its exit code says; see <see cref="ElevationSession.RequestAsync"/>.
    /// </summary>
    internal async Task<PrivilegedResult<ServeResponse>> CallAsync(ServeRequest request, bool allowPrompt, CancellationToken cancellationToken)
    {
        if (Find(Pkexec) is not { } pkexec)
        {
            return PrivilegedResult<ServeResponse>.NotAvailable(PrivilegedUnavailableReason.NoPkexec);
        }

        if (!_session.IsActive && HelperCapability() is { Available: false } missing)
        {
            return PrivilegedResult<ServeResponse>.NotAvailable(missing.Reason!.Value);
        }

        PrivilegedResult<ServeResponse> result = await _session.RequestAsync(pkexec, request, allowPrompt, HelperProtocol.MaxRuntime, cancellationToken).ConfigureAwait(false);
        if (result is { Outcome: PrivilegedOutcome.NotAvailable, Reason: PrivilegedUnavailableReason.HelperOutdated } && !_outdated)
        {
            _outdated = true;
            StateChanged?.Invoke();
        }

        return result;
    }

    public async Task<PrivilegedResult<bool>> TurnOnAsync(CancellationToken cancellationToken)
    {
        if (_session.IsActive)
        {
            return PrivilegedResult<bool>.Done(true);
        }

        PrivilegedResult<ServeResponse> hello = await CallAsync(new ServeRequest(HelperProtocol.Version, 0, HelperProtocol.Hello), allowPrompt: true, cancellationToken).ConfigureAwait(false);
        return hello.Outcome switch
        {
            PrivilegedOutcome.Done when hello.Value is { Exit: HelperExit.Ok } => PrivilegedResult<bool>.Done(true),
            PrivilegedOutcome.Done => PrivilegedResult<bool>.Failed(hello.Value!.Error),
            _ => hello.As<bool>(),
        };
    }

    public void Lock() => _session.Lock();

    public Task<PrivilegedResult<AdministratorSetupPlan>> PrepareSetupAsync(CancellationToken cancellationToken) =>
        Find(Pkexec) is null
            ? Task.FromResult(PrivilegedResult<AdministratorSetupPlan>.NotAvailable(PrivilegedUnavailableReason.NoPkexec))
            : _setup.PrepareAsync(cancellationToken);

    public async Task<PrivilegedResult<bool>> SetupAsync(AdministratorSetupPlan plan, CancellationToken cancellationToken)
    {
        if (Find(Pkexec) is not { } pkexec)
        {
            return PrivilegedResult<bool>.NotAvailable(PrivilegedUnavailableReason.NoPkexec);
        }

        PrivilegedResult<bool> installed = await _setup.InstallAsync(pkexec, plan, cancellationToken).ConfigureAwait(false);
        if (installed.Outcome == PrivilegedOutcome.Done)
        {
            _outdated = false; // A fresh helper replaced whatever was outdated.
            StateChanged?.Invoke();
        }

        return installed;
    }

    public void Dispose() => _session.Dispose();

    internal string? Find(string[] candidates) => candidates.FirstOrDefault(_exists);
}
