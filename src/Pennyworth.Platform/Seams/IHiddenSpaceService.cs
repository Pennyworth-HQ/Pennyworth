using Pennyworth.Scanning;

namespace Pennyworth.Platform;

public enum UnlockOutcome
{
    Measured,

    /// <summary>The user said no to the system's permission prompt.</summary>
    Declined,

    Failed,
}

public sealed record UnlockResult(UnlockOutcome Outcome, HiddenSpaceReport? Report = null);

public interface IHiddenSpaceService
{
    /// <summary>Asks the system for permission and measures the drive's hidden system space.</summary>
    Task<UnlockResult> MeasureAsync(string driveRoot, CancellationToken cancellationToken);
}
