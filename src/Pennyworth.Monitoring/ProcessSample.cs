namespace Pennyworth.Monitoring;

/// <summary>One process at one moment.</summary>
/// <param name="Name">Image name without ".exe" ("chrome", "Memory Compression").</param>
/// <param name="PrivateMemory">Private working set: memory only this process uses.</param>
/// <param name="CpuTime">User + kernel time so far, in 100 ns units.</param>
/// <param name="CreateTime">Distinguishes a reused process ID.</param>
public readonly record struct ProcessSample(
    int Pid,
    int ParentPid,
    string Name,
    long PrivateMemory,
    long WorkingSet,
    long CpuTime,
    long CreateTime);

/// <summary>Physical memory split the way the design shows it.</summary>
/// <param name="Cached">Standby memory Windows hands back instantly when an app needs it.</param>
/// <param name="Compressed">Held by Windows' memory compression.</param>
public readonly record struct MemoryStatus(long Total, long Available, long Cached, long Compressed)
{
    public long Used => Math.Max(Total - Available, 0);

    public long InUse => Math.Max(Used - Compressed, 0);

    public long Free => Math.Max(Available - Cached, 0);
}
