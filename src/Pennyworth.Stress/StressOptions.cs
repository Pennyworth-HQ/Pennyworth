namespace Pennyworth.Stress;

/// <summary>The parts of the PC a stress test can load. Several can run at once.</summary>
[Flags]
public enum StressKind
{
    None = 0,
    Cpu = 1,
    Memory = 2,
    Disk = 4,
}

/// <summary>How the processor is loaded, after the classic stress tools.</summary>
public enum CpuMode
{
    /// <summary>Fused multiply-add on vector registers: the most heat and power (Prime95 "small FFTs", OCCT "AVX").</summary>
    FloatingPoint,

    /// <summary>Integer multiply, shift and xor chains: stresses the integer units, runs cooler.</summary>
    Integer,

    /// <summary>Alternates both, so the load and the power draw keep changing.</summary>
    Mixed,
}

/// <summary>What to run and for how long.</summary>
/// <param name="CpuThreads">Processor threads to load; 0 means every logical processor.</param>
/// <param name="MemoryBytes">Memory the memory test fills and checks.</param>
/// <param name="DiskFolder">Where the disk test writes its temporary file (deleted afterwards).</param>
/// <param name="DiskBytes">Size of that file.</param>
/// <param name="StopOnError">Stop everything at the first wrong result, as OCCT does by default.</param>
public sealed record StressOptions(
    StressKind Kinds,
    TimeSpan Duration,
    CpuMode CpuMode = CpuMode.FloatingPoint,
    int CpuThreads = 0,
    long MemoryBytes = 0,
    string? DiskFolder = null,
    long DiskBytes = 0,
    bool StopOnError = true);

/// <summary>How a run ended.</summary>
public enum StressOutcome
{
    /// <summary>Ran for the whole duration without a wrong result.</summary>
    Passed,

    /// <summary>Stopped by the person (or by the app, e.g. too hot) before the end.</summary>
    Stopped,

    /// <summary>A computation, memory or disk check gave a wrong result.</summary>
    ErrorFound,

    /// <summary>A test could not run: not enough memory or disk space, or it crashed.</summary>
    Failed,
}

/// <summary>What a test is doing right now.</summary>
public enum WorkPhase
{
    Idle,
    Preparing,
    Working,
    Writing,
    Reading,
    Done,
}

/// <summary>Why a test could not run.</summary>
public enum WorkloadFailure
{
    None,
    NotEnoughMemory,
    NotEnoughDiskSpace,
    Crashed,
}

/// <summary>One test's counters at one moment. Rates come from differencing two snapshots.</summary>
/// <param name="Operations">CPU: arithmetic operations; memory and disk: bytes moved.</param>
/// <param name="Errors">Wrong results found so far.</param>
/// <param name="Threads">Threads the test runs on.</param>
/// <param name="Bytes">Memory or file size under test; 0 for the CPU.</param>
public sealed record WorkloadSnapshot(
    StressKind Kind,
    long Operations,
    long Errors,
    WorkPhase Phase,
    int Threads,
    long Bytes,
    WorkloadFailure Failure,
    string? FailureDetail);

public sealed record StressSnapshot(TimeSpan Elapsed, IReadOnlyList<WorkloadSnapshot> Workloads)
{
    public static StressSnapshot Empty { get; } = new(TimeSpan.Zero, []);

    public WorkloadSnapshot? this[StressKind kind] => Workloads.FirstOrDefault(w => w.Kind == kind);

    public long Errors => Workloads.Sum(w => w.Errors);
}
