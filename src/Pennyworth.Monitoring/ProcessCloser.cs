using System.ComponentModel;
using System.Diagnostics;

namespace Pennyworth.Monitoring;

public interface IProcessCloser
{
    /// <summary>
    /// Asks the app to close the normal way (like clicking its X), so it can offer to save.
    /// Returns true when every process exited within <paramref name="timeout"/>.
    /// </summary>
    Task<bool> CloseAsync(IReadOnlyList<int> pids, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Ends the processes immediately. Unsaved work is lost.</summary>
    void ForceClose(IReadOnlyList<int> pids);
}

public sealed class ProcessCloser : IProcessCloser
{
    public async Task<bool> CloseAsync(IReadOnlyList<int> pids, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var processes = Open(pids);
        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    process.CloseMainWindow();
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }
            }

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < timeout)
            {
                if (processes.All(HasExited))
                {
                    return true;
                }

                await Task.Delay(200, cancellationToken);
            }

            return processes.All(HasExited);
        }
        finally
        {
            processes.ForEach(p => p.Dispose());
        }
    }

    public void ForceClose(IReadOnlyList<int> pids)
    {
        foreach (Process process in Open(pids))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Gone already, or Windows refused (protected); nothing more to do.
                }
            }
        }
    }

    private static List<Process> Open(IReadOnlyList<int> pids)
    {
        var processes = new List<Process>(pids.Count);
        foreach (int pid in pids)
        {
            try
            {
                processes.Add(Process.GetProcessById(pid));
            }
            catch (ArgumentException)
            {
                // Already exited.
            }
        }

        return processes;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return true;
        }
    }
}
