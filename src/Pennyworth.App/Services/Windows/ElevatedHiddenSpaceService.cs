using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Pennyworth.Platform;
using Pennyworth.Scanning;

namespace Pennyworth.App.Services.Windows;

/// <summary>
/// Starts a short-lived elevated copy of Pennyworth that only measures and writes a report.
/// The window you see never runs elevated.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ElevatedHiddenSpaceService : IHiddenSpaceService
{
    private const int ErrorCancelled = 1223;

    /// <summary>
    /// The elevated copy's side of the exchange: when <paramref name="args"/> ask for a measurement,
    /// measure, write the report and say so. No window is ever created.
    /// </summary>
    public static bool TryRunHelper(string[] args, out int exitCode)
    {
        if (args is not [HiddenSpace.Argument, string drive, string reportPath])
        {
            exitCode = 0;
            return false;
        }

        if (!HiddenSpace.IsDriveLetter(drive))
        {
            exitCode = 2;
            return true;
        }

        HiddenSpace.WriteReport(HiddenSpace.Measure(drive), reportPath);
        exitCode = 0;
        return true;
    }

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
