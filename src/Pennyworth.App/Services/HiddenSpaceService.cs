using System.ComponentModel;
using System.Diagnostics;
using Pennyworth.Scanning;

namespace Pennyworth.App.Services;

public enum UnlockOutcome
{
    Measured,

    /// <summary>The user said no to Windows' permission prompt.</summary>
    Declined,

    Failed,
}

public sealed record UnlockResult(UnlockOutcome Outcome, HiddenSpaceReport? Report = null);

public interface IHiddenSpaceService
{
    /// <summary>Asks Windows for permission and measures the drive's hidden system space.</summary>
    Task<UnlockResult> MeasureAsync(string driveRoot, CancellationToken cancellationToken);
}

/// <summary>
/// Starts a short-lived elevated copy of Pennyworth that only measures and writes a report.
/// The window you see never runs elevated.
/// </summary>
public sealed class ElevatedHiddenSpaceService : IHiddenSpaceService
{
    private const int ErrorCancelled = 1223;

    public async Task<UnlockResult> MeasureAsync(string driveRoot, CancellationToken cancellationToken)
    {
        string drive = driveRoot.TrimEnd('\\');
        if (!HiddenSpace.IsDriveLetter(drive) || Environment.ProcessPath is not { } exe)
        {
            return new UnlockResult(UnlockOutcome.Failed);
        }

        string report = Path.Combine(Path.GetTempPath(), $"pennyworth-hidden-{Guid.NewGuid():N}.json");
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe)
            {
                ArgumentList = { HiddenSpace.Argument, drive, report },
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
            {
                return new UnlockResult(UnlockOutcome.Failed);
            }

            await process.WaitForExitAsync(cancellationToken);
            return HiddenSpace.ReadReport(report) is { } measured
                ? new UnlockResult(UnlockOutcome.Measured, measured)
                : new UnlockResult(UnlockOutcome.Failed);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new UnlockResult(UnlockOutcome.Declined);
        }
        catch (Win32Exception)
        {
            return new UnlockResult(UnlockOutcome.Failed);
        }
        finally
        {
            try
            {
                File.Delete(report);
            }
            catch (IOException)
            {
            }
        }
    }
}
