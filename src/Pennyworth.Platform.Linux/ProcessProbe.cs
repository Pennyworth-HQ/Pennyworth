using System.ComponentModel;
using System.Diagnostics;

namespace Pennyworth.Platform.Linux;

/// <summary>
/// Runs a helper program (<c>btrfs</c>, <c>nvidia-smi</c>, <c>systemctl</c>...) and returns what it
/// printed. A missing program, a failure and a hang are all the same answer, null: every caller
/// treats "this source is not available here" as the normal case, never as an error.
/// </summary>
public static class ProcessProbe
{
    /// <summary>
    /// Returns standard output, or null when the program cannot start, exits non-zero or outlives
    /// <paramref name="timeout"/>. Both pipes are drained while it runs (reading stdout to the end
    /// first would make the timeout unreachable, and an undrained stderr can block the child), and
    /// the locale is fixed to <c>C</c> so the output parses the same on every machine.
    /// </summary>
    public static string? Run(string fileName, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            Environment = { ["LC_ALL"] = "C" },
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null; // Not installed, or not executable.
        }

        if (process is null)
        {
            return null;
        }

        using (process)
        {
            process.StandardInput.Close(); // Nothing to say, and a program that waits for input must not hang.
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                Kill(process);
                return null;
            }

            // The child is gone, but a grandchild can hold a pipe open: never wait on one forever.
            if (!Task.WaitAll([output, error], TimeSpan.FromSeconds(5)))
            {
                return null;
            }

            return process.ExitCode == 0 ? output.Result : null;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // It exited between the timeout and the kill.
        }
    }
}
