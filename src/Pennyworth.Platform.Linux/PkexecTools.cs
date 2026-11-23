using Pennyworth.Core.Privilege;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Runs fixed system tools through <c>pkexec</c> for the read-only features that work without the helper, and turns what came
/// back (pkexec's exit code, its one line on standard error) into a <see cref="PrivilegedResult{T}"/>. It runs only what it is
/// given: the callers pass absolute paths of <c>cat</c>, <c>dmidecode</c>, <c>btrfs</c> and <c>bash</c> and fixed arguments.
/// </summary>
/// <remarks>
/// A dismissed or refused prompt settles the call (126 and 127 are pkexec's); nothing here asks twice. Every call through here is
/// a one-off prompt: it does not start the administrator session.
/// </remarks>
internal sealed class PkexecTools(ElevatedRunnerFunc run, TimeSpan promptTimeout)
{
    /// <summary>pkexec's exit code when the authentication dialog was dismissed.</summary>
    private const int Dismissed = 126;

    /// <summary>pkexec's exit code when the person is not authorised, or no authentication agent could ask.</summary>
    private const int NotAuthorized = 127;

    private const int MaxDetail = 300;

    /// <summary>Runs <c>pkexec &lt;program&gt;</c> (one of the fixed system tools); the caller reads the whole <see cref="ElevatedRun"/>.</summary>
    public Task<ElevatedRun> RunToolAsync(string pkexec, IReadOnlyList<string> program, CancellationToken cancellationToken) =>
        run(pkexec, program, null, ElevatedRunner.MaxOutput, promptTimeout, cancellationToken);

    /// <summary>
    /// What a run says about asking, before anything is read from it: <see cref="PrivilegedOutcome.Done"/> with the standard
    /// output when it exited 0; a settled outcome for a dismissed or refused prompt, a program that could not start, ran too
    /// long, printed too much or was killed; and null when the program ran and exited with an ordinary failure code
    /// (1 to 125), which only the caller can interpret.
    /// </summary>
    public static PrivilegedResult<byte[]>? Settle(ElevatedRun run) => run switch
    {
        { Status: ElevatedStatus.NotStarted } => PrivilegedResult<byte[]>.NotAvailable(PrivilegedUnavailableReason.NoPkexec),
        { Status: ElevatedStatus.TimedOut } => PrivilegedResult<byte[]>.Failed("Nobody answered in time, or it ran too long."),
        { Status: ElevatedStatus.TooLarge } => PrivilegedResult<byte[]>.Failed("The answer was larger than allowed."),
        { ExitCode: Dismissed } => PrivilegedResult<byte[]>.Cancelled,
        { ExitCode: NotAuthorized } => run.Error.Contains("No authentication agent", StringComparison.OrdinalIgnoreCase)
            ? PrivilegedResult<byte[]>.NotAvailable(PrivilegedUnavailableReason.NoAuthenticationAgent)
            : PrivilegedResult<byte[]>.Cancelled,
        { ExitCode: 0 } => PrivilegedResult<byte[]>.Done(run.Output),
        { ExitCode: > NotAuthorized } => PrivilegedResult<byte[]>.Failed($"Stopped by signal {run.ExitCode - 128}."),
        _ => null,
    };

    /// <summary>The first line of what the program said on standard error, or its exit code: a short English reason for a log.</summary>
    public static string Detail(ElevatedRun run)
    {
        string line = run.Error.Split('\n', 2)[0].Trim();
        if (line.Length == 0)
        {
            return $"Exited with code {run.ExitCode}.";
        }

        return line.Length <= MaxDetail ? line : line[..MaxDetail];
    }
}
