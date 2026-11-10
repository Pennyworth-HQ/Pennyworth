using Pennyworth.Cleanup;
using Pennyworth.Core.Privilege;
using Pennyworth.Scanning;

namespace Pennyworth.Platform;

/// <summary>What <see cref="IPrivilegedOperations"/> can do.</summary>
public enum PrivilegedOperation
{
    /// <summary>Read the firmware tables that list the memory modules.</summary>
    FirmwareTables,

    /// <summary>Measure snapshots and file system metadata that only an administrator can see.</summary>
    SystemSpace,

    /// <summary>Measure folders the person cannot list.</summary>
    Folders,

    /// <summary>Delete the listed files of a system folder.</summary>
    SystemCleanup,
}

/// <summary>The size of one folder the person could not list, as measured with an administrator's rights.</summary>
/// <param name="SizeOnDisk">Allocated bytes, counted the way the scan counts them.</param>
/// <param name="Complete">False when something inside could not be read or measuring stopped early: the size is a lower bound.</param>
public sealed record MeasuredFolder(string Path, long SizeOnDisk, long LogicalSize, long Files, bool Complete);

/// <summary>Where administrator access stands right now.</summary>
public enum AdministratorAccessState
{
    /// <summary>Not turned on. The next privileged request starts it with one password prompt.</summary>
    Off,

    /// <summary>Turned on: privileged requests are answered without asking again until it is locked, idle for too long, or the app closes.</summary>
    Active,

    /// <summary>The helper that serves the session is not installed (or not trustworthy, or outdated): a one-time, explicitly approved setup installs it. <see cref="IAdministratorAccess.Reason"/> says which.</summary>
    NeedsSetup,

    /// <summary>There is no way to ask on this machine at all (no <c>pkexec</c>); <see cref="IAdministratorAccess.Reason"/> says why.</summary>
    Unavailable,
}

/// <summary>What the one-time setup would install and from where, shown to the person before anything runs as root.</summary>
/// <param name="HelperSource">The helper binary inside this copy of the app.</param>
/// <param name="HelperSha256">Lower-case hex SHA-256 of <paramref name="HelperSource"/> as read just now; the root-side installer refuses a copy that does not match it.</param>
/// <param name="HelperBytes">Size of the helper in bytes.</param>
/// <param name="PolicySource">The polkit policy inside this copy of the app.</param>
/// <param name="PolicySha256">Lower-case hex SHA-256 of <paramref name="PolicySource"/>.</param>
/// <param name="HelperDestination">Where the helper will be installed (root-owned).</param>
/// <param name="PolicyDestination">Where the policy will be installed (root-owned).</param>
public sealed record AdministratorSetupPlan(
    string HelperSource,
    string HelperSha256,
    long HelperBytes,
    string PolicySource,
    string PolicySha256,
    string HelperDestination,
    string PolicyDestination);

/// <summary>
/// One administrator session for everything that needs one: a single long-lived helper started by one password prompt serves
/// every privileged request until it is locked, idle for <see cref="IdleTimeout"/>, or the app closes. Cancelling a prompt is final
/// for that action: nothing here asks again by itself.
/// </summary>
public interface IAdministratorAccess
{
    AdministratorAccessState State { get; }

    /// <summary>Why <see cref="State"/> is <see cref="AdministratorAccessState.NeedsSetup"/> or <see cref="AdministratorAccessState.Unavailable"/>; null otherwise.</summary>
    PrivilegedUnavailableReason? Reason { get; }

    /// <summary>How long an active session may go without a request before it locks itself.</summary>
    TimeSpan IdleTimeout { get; }

    /// <summary>Raised, from any thread, whenever <see cref="State"/> or <see cref="Reason"/> may have changed.</summary>
    event Action? StateChanged;

    /// <summary>
    /// Turns administrator access on: one password prompt. <see cref="PrivilegedOutcome.Done"/> when it is active (also when it
    /// already was, without a prompt). Only ever called for something the person asked for.
    /// </summary>
    Task<PrivilegedResult<bool>> TurnOnAsync(CancellationToken cancellationToken);

    /// <summary>Ends the session now; the next privileged request asks for the password again.</summary>
    void Lock();

    /// <summary>Reads what the setup would install from this copy of the app, without installing or asking anything.</summary>
    Task<PrivilegedResult<AdministratorSetupPlan>> PrepareSetupAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Installs exactly the two files of <paramref name="plan"/> as root (one password prompt) and nothing else; it starts nothing.
    /// <see cref="PrivilegedOutcome.Done"/> when the helper is installed and trusted afterwards.
    /// </summary>
    Task<PrivilegedResult<bool>> SetupAsync(AdministratorSetupPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// The few things Pennyworth does only when a person asks, with administrator rights. All of them go through one
/// <see cref="Access"/> session: the first starts it (the system's own password dialog; the app never sees the password), the rest
/// reuse it. Where the helper is not installed, the two read-only features still work as one-off prompts and the other two say
/// they need the setup. <see cref="Capability"/> answers without asking anything.
/// </summary>
public interface IPrivilegedOperations
{
    /// <summary>The administrator session every operation below runs in.</summary>
    IAdministratorAccess Access { get; }

    /// <summary>Whether <paramref name="operation"/> can be asked for now; cheap, never shows a prompt, re-evaluated on every call.</summary>
    PrivilegedCapability Capability(PrivilegedOperation operation);

    /// <summary>
    /// The raw SMBIOS structure table (<c>/sys/firmware/dmi/tables/DMI</c>). Read only.
    /// </summary>
    /// <param name="promptIfNeeded">False for a refresh nobody asked for: it never starts a session and answers <see cref="PrivilegedUnavailableReason.NotActive"/> when none is running.</param>
    Task<PrivilegedResult<byte[]>> ReadFirmwareTablesAsync(CancellationToken cancellationToken, bool promptIfNeeded = true);

    /// <summary>The text of <c>dmidecode -t 17</c>, for firmware the raw table reader finds nothing in. Read only; needs <c>dmidecode</c> installed.</summary>
    Task<PrivilegedResult<string>> ReadMemoryDevicesTextAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Snapshot space and file system metadata of the file system mounted at <paramref name="mountPoint"/>
    /// (<see cref="HiddenSpaceReport.NtfsMetadata"/> keeps its Windows name). A part that could not be measured is null. Read only.
    /// </summary>
    Task<PrivilegedResult<HiddenSpaceReport>> MeasureSystemSpaceAsync(string mountPoint, CancellationToken cancellationToken, bool promptIfNeeded = true);

    /// <summary>Sizes of folders (absolute paths). Read only; needs the helper installed (<see cref="IAdministratorAccess.PrepareSetupAsync"/>).</summary>
    Task<PrivilegedResult<IReadOnlyList<MeasuredFolder>>> MeasureFoldersAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken, bool promptIfNeeded = true);

    /// <summary>
    /// Deletes exactly the files of the preview in <paramref name="estimate"/> (a rule with <c>requiresAdmin</c>),
    /// inside the folders the helper itself lists for that rule. Needs the helper installed.
    /// </summary>
    Task<PrivilegedResult<CleanupResult>> CleanSystemAsync(CleanupEstimate estimate, CancellationToken cancellationToken);
}
