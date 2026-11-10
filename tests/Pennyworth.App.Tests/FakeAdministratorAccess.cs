using Pennyworth.Core.Privilege;
using Pennyworth.Platform;

namespace Pennyworth.App.Tests;

/// <summary>
/// The administrator session as the view models see it, with no process behind it: the state is set by the test and by the
/// operations it answers (turning on makes it active, locking makes it off, a finished setup makes it off with a helper).
/// </summary>
internal sealed class FakeAdministratorAccess : IAdministratorAccess
{
    public AdministratorAccessState State { get; set; } = AdministratorAccessState.Off;

    public PrivilegedUnavailableReason? Reason { get; set; }

    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(15);

    public event Action? StateChanged;

    public int TurnOnCalls { get; private set; }

    public int LockCalls { get; private set; }

    public int SetupCalls { get; private set; }

    public PrivilegedResult<bool> TurnOnResult { get; set; } = PrivilegedResult<bool>.Done(true);

    public PrivilegedResult<AdministratorSetupPlan> PlanResult { get; set; } = PrivilegedResult<AdministratorSetupPlan>.Done(Plan);

    public PrivilegedResult<bool> SetupResult { get; set; } = PrivilegedResult<bool>.Done(true);

    public static AdministratorSetupPlan Plan { get; } = new(
        "/opt/pennyworth/libexec/pennyworth/pennyworth-helper",
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        3_400_000,
        "/opt/pennyworth/share/polkit-1/actions/app.pennyworth.policy",
        "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210",
        "/usr/libexec/pennyworth/pennyworth-helper",
        "/usr/share/polkit-1/actions/app.pennyworth.policy");

    /// <summary>The state changed from outside (the idle lock, a crash, a helper that appeared).</summary>
    public void Set(AdministratorAccessState state, PrivilegedUnavailableReason? reason = null)
    {
        State = state;
        Reason = reason;
        StateChanged?.Invoke();
    }

    public Task<PrivilegedResult<bool>> TurnOnAsync(CancellationToken cancellationToken)
    {
        TurnOnCalls++;
        if (State == AdministratorAccessState.Active)
        {
            return Task.FromResult(PrivilegedResult<bool>.Done(true));
        }

        if (TurnOnResult.Outcome == PrivilegedOutcome.Done)
        {
            Set(AdministratorAccessState.Active);
        }

        return Task.FromResult(TurnOnResult);
    }

    public void Lock()
    {
        LockCalls++;
        Set(AdministratorAccessState.Off);
    }

    public Task<PrivilegedResult<AdministratorSetupPlan>> PrepareSetupAsync(CancellationToken cancellationToken) => Task.FromResult(PlanResult);

    public Task<PrivilegedResult<bool>> SetupAsync(AdministratorSetupPlan plan, CancellationToken cancellationToken)
    {
        SetupCalls++;
        if (SetupResult.Outcome == PrivilegedOutcome.Done)
        {
            Set(AdministratorAccessState.Off);
        }

        return Task.FromResult(SetupResult);
    }
}
