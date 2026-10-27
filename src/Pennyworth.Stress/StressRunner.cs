using System.Diagnostics;

namespace Pennyworth.Stress;

/// <summary>A stress test run: starts the chosen tests together and ends them together.</summary>
public interface IStressRunner
{
    /// <summary>Runs until the duration ends, <paramref name="token"/> is cancelled or (if asked) an error is found.</summary>
    Task<StressOutcome> RunAsync(CancellationToken token);

    /// <summary>The counters right now; safe to call from any thread while running.</summary>
    StressSnapshot Read();
}

public sealed class StressRunner : IStressRunner
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(100);

    private readonly StressOptions _options;
    private readonly Stopwatch _clock = new();

    public StressRunner(StressOptions options)
        : this(options, Create(options))
    {
    }

    internal StressRunner(StressOptions options, IReadOnlyList<Workload> workloads)
    {
        _options = options;
        Workloads = workloads;
    }

    public IReadOnlyList<Workload> Workloads { get; }

    public StressSnapshot Read() => new(_clock.Elapsed, [.. Workloads.Select(w => w.Read())]);

    public async Task<StressOutcome> RunAsync(CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _clock.Restart();
        Task[] running = [.. Workloads.Select(w => w.RunAsync(stop.Token))];
        Task all = Task.WhenAll(running);

        StressOutcome outcome = StressOutcome.Passed;
        while (!all.IsCompleted)
        {
            if (token.IsCancellationRequested)
            {
                outcome = StressOutcome.Stopped;
                break;
            }

            if (Workloads.Any(w => w.Failure != WorkloadFailure.None))
            {
                outcome = StressOutcome.Failed;
                break;
            }

            if (_options.StopOnError && Workloads.Any(w => w.Errors > 0))
            {
                outcome = StressOutcome.ErrorFound;
                break;
            }

            if (_clock.Elapsed >= _options.Duration)
            {
                break;
            }

            await Task.WhenAny(all, Task.Delay(CheckInterval, CancellationToken.None)).ConfigureAwait(false);
        }

        stop.Cancel();
        await all.ConfigureAwait(false);
        _clock.Stop();
        if (outcome == StressOutcome.Passed && token.IsCancellationRequested)
        {
            outcome = StressOutcome.Stopped;
        }

        // Errors found while stopping still count; a test that could not start makes the run fail.
        if (outcome is StressOutcome.Passed or StressOutcome.Stopped)
        {
            if (Workloads.Any(w => w.Failure != WorkloadFailure.None))
            {
                outcome = StressOutcome.Failed;
            }
            else if (Workloads.Any(w => w.Errors > 0))
            {
                outcome = StressOutcome.ErrorFound;
            }
        }

        return outcome;
    }

    private static List<Workload> Create(StressOptions options)
    {
        var workloads = new List<Workload>();
        if (options.Kinds.HasFlag(StressKind.Cpu))
        {
            workloads.Add(new CpuWorkload(options.CpuMode, options.CpuThreads));
        }

        if (options.Kinds.HasFlag(StressKind.Memory) && options.MemoryBytes > 0)
        {
            workloads.Add(new MemoryWorkload(options.MemoryBytes));
        }

        if (options.Kinds.HasFlag(StressKind.Disk) && options.DiskFolder is { } folder && options.DiskBytes > 0)
        {
            workloads.Add(new DiskWorkload(folder, options.DiskBytes));
        }

        if (options.Kinds.HasFlag(StressKind.Gpu))
        {
            workloads.Add(new GpuWorkload());
        }

        return workloads;
    }
}
