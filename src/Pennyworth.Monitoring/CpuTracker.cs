namespace Pennyworth.Monitoring;

/// <summary>
/// Turns cumulative CPU times into "% of the whole machine" between two samples.
/// </summary>
public sealed class CpuTracker
{
    private Dictionary<(int Pid, long CreateTime), long> _lastProcessTimes = [];
    private (long Idle, long Kernel, long User)? _lastSystem;

    /// <summary>Total CPU load from GetSystemTimes values (kernel time includes idle time). 0 to 100.</summary>
    public double UpdateSystem(long idle, long kernel, long user)
    {
        var previous = _lastSystem;
        _lastSystem = (idle, kernel, user);
        if (previous is not { } last)
        {
            return 0;
        }

        long total = (kernel - last.Kernel) + (user - last.User);
        long busy = total - (idle - last.Idle);
        return total <= 0 ? 0 : Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    /// <summary>
    /// CPU per process since the previous call, as a share of all cores (0 to 100).
    /// Processes seen for the first time report 0.
    /// </summary>
    /// <param name="elapsed">Wall-clock time since the previous call.</param>
    public Dictionary<int, double> UpdateProcesses(IReadOnlyList<ProcessSample> processes, TimeSpan elapsed, int processorCount)
    {
        var current = new Dictionary<(int, long), long>(processes.Count);
        var result = new Dictionary<int, double>(processes.Count);
        double capacity = elapsed.Ticks * (double)Math.Max(processorCount, 1);
        foreach (ProcessSample process in processes)
        {
            var key = (process.Pid, process.CreateTime);
            current[key] = process.CpuTime;
            double percent = 0;
            if (capacity > 0 && _lastProcessTimes.TryGetValue(key, out long before))
            {
                percent = Math.Clamp((process.CpuTime - before) * 100.0 / capacity, 0, 100);
            }

            result[process.Pid] = percent;
        }

        _lastProcessTimes = current;
        return result;
    }
}
