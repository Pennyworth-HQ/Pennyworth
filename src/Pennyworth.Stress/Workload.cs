namespace Pennyworth.Stress;

/// <summary>
/// One test: prepares its buffers, then runs small units of work on dedicated threads until
/// cancelled. Units are short (a few milliseconds) so stopping is immediate. Counters are
/// updated with interlocked adds and read from any thread.
/// </summary>
public abstract class Workload
{
    private long _operations;
    private long _errors;
    private int _phase;

    public abstract StressKind Kind { get; }

    public abstract int Threads { get; }

    /// <summary>Memory or file size under test; 0 when it does not apply.</summary>
    public virtual long Bytes => 0;

    public WorkloadFailure Failure { get; private set; }

    public string? FailureDetail { get; private set; }

    public WorkPhase Phase
    {
        get => (WorkPhase)Volatile.Read(ref _phase);
        protected set => Volatile.Write(ref _phase, (int)value);
    }

    public long Errors => Interlocked.Read(ref _errors);

    public WorkloadSnapshot Read() =>
        new(Kind, Interlocked.Read(ref _operations), Errors, Phase, Threads, Bytes, Failure, FailureDetail);

    /// <summary>Prepares on the calling thread pool, then runs <see cref="Threads"/> threads until cancelled.</summary>
    public Task RunAsync(CancellationToken token) => Task.Run(() => Run(token), CancellationToken.None);

    private void Run(CancellationToken token)
    {
        Phase = WorkPhase.Preparing;
        try
        {
            if (!Prepare())
            {
                return;
            }

            Phase = WorkPhase.Working;
            var threads = new Thread[Threads];
            for (int i = 0; i < threads.Length; i++)
            {
                int index = i;
                // Below normal: the PC is still fully loaded, but the window and the mouse stay responsive.
                threads[i] = new Thread(() => Guard(index, token))
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = $"Pennyworth stress {Kind} {index}",
                };
                threads[i].Start();
            }

            foreach (Thread thread in threads)
            {
                thread.Join();
            }
        }
        catch (OutOfMemoryException ex)
        {
            Fail(WorkloadFailure.NotEnoughMemory, ex.Message);
        }
        catch (Exception ex)
        {
            // Preparing failed (no access to the folder, a drive that went away): report it, never throw.
            Fail(WorkloadFailure.Crashed, ex.Message);
        }
        finally
        {
            Release();
            Phase = WorkPhase.Done;
        }
    }

    private void Guard(int index, CancellationToken token)
    {
        try
        {
            Work(index, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (OutOfMemoryException ex)
        {
            Fail(WorkloadFailure.NotEnoughMemory, ex.Message);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 0x70 or 0x27)
        {
            // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL.
            Fail(WorkloadFailure.NotEnoughDiskSpace, ex.Message);
        }
        catch (Exception ex)
        {
            Fail(WorkloadFailure.Crashed, ex.Message);
        }
    }

    /// <summary>Allocates what the test needs; false (after <see cref="Fail"/>) when it cannot run.</summary>
    protected virtual bool Prepare() => true;

    /// <summary>Runs units of work on thread <paramref name="index"/> until cancelled.</summary>
    protected abstract void Work(int index, CancellationToken token);

    /// <summary>Frees buffers and files; runs once, after every thread has stopped.</summary>
    protected virtual void Release()
    {
    }

    protected void Count(long operations) => Interlocked.Add(ref _operations, operations);

    protected void Error(long count = 1) => Interlocked.Add(ref _errors, count);

    protected void Fail(WorkloadFailure failure, string? detail)
    {
        if (Failure == WorkloadFailure.None)
        {
            FailureDetail = detail;
            Failure = failure;
        }
    }
}
