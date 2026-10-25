using System.Buffers;
using System.Runtime.InteropServices;

namespace Pennyworth.Monitoring.Native;

/// <summary>
/// Reads every process with one NtQuerySystemInformation call: no handles are opened,
/// so it works without admin rights and also sees protected processes.
/// </summary>
internal static unsafe class ProcessReader
{
    // SYSTEM_PROCESS_INFORMATION field offsets on 64-bit Windows (x64 and ARM64).
    private const int NextEntryOffset = 0;
    private const int WorkingSetPrivateSize = 8;
    private const int CreateTime = 32;
    private const int UserTime = 40;
    private const int KernelTime = 48;
    private const int ImageNameLength = 56;
    private const int ImageNameBuffer = 64;
    private const int UniqueProcessId = 80;
    private const int ParentProcessId = 88;
    private const int WorkingSetSize = 144;

    private static int s_bufferSize = 512 * 1024;

    public static List<ProcessSample> Read()
    {
        while (true)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(s_bufferSize);
            try
            {
                fixed (byte* start = buffer)
                {
                    int status = MonitorNative.NtQuerySystemInformation(
                        MonitorNative.SystemProcessInformation, start, buffer.Length, out int needed);
                    if (status == MonitorNative.StatusInfoLengthMismatch)
                    {
                        s_bufferSize = Math.Max(needed, buffer.Length) + (64 * 1024);
                        continue;
                    }

                    if (status < 0)
                    {
                        throw new InvalidOperationException($"NtQuerySystemInformation failed (0x{status:X8}).");
                    }

                    return Parse(start);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static List<ProcessSample> Parse(byte* start)
    {
        var samples = new List<ProcessSample>(400);
        byte* entry = start;
        while (true)
        {
            int pid = (int)*(nint*)(entry + UniqueProcessId);
            if (pid != 0) // The idle "process" is not a real process.
            {
                int nameBytes = *(ushort*)(entry + ImageNameLength);
                char* nameChars = *(char**)(entry + ImageNameBuffer);
                string name = nameChars is null ? "" : new string(nameChars, 0, nameBytes / 2);
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^4];
                }

                samples.Add(new ProcessSample(
                    pid,
                    (int)*(nint*)(entry + ParentProcessId),
                    name,
                    *(long*)(entry + WorkingSetPrivateSize),
                    (long)*(nuint*)(entry + WorkingSetSize),
                    *(long*)(entry + UserTime) + *(long*)(entry + KernelTime),
                    *(long*)(entry + CreateTime)));
            }

            uint next = *(uint*)(entry + NextEntryOffset);
            if (next == 0)
            {
                return samples;
            }

            entry += next;
        }
    }

    /// <summary>The executable's full path, or null for protected processes.</summary>
    public static string? GetImagePath(int pid)
    {
        nint handle = MonitorNative.OpenProcess(MonitorNative.ProcessQueryLimitedInformation, false, pid);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            char* buffer = stackalloc char[1024];
            int size = 1024;
            return MonitorNative.QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            MonitorNative.CloseHandle(handle);
        }
    }

    /// <param name="free">Free and zeroed bytes from the performance counter, when available.</param>
    public static MemoryStatus ReadMemory(long compressed, long? free)
    {
        var status = new MonitorNative.MemoryStatusEx { Length = (uint)sizeof(MonitorNative.MemoryStatusEx) };
        MonitorNative.GlobalMemoryStatusEx(ref status);

        var performance = new MonitorNative.PerformanceInformation { Size = (uint)sizeof(MonitorNative.PerformanceInformation) };
        long cache = MonitorNative.GetPerformanceInfo(ref performance, performance.Size)
            ? (long)performance.SystemCache * (long)performance.PageSize
            : 0;

        long available = (long)status.AvailPhys;
        long cached = free is { } freeBytes ? available - Math.Min(freeBytes, available) : Math.Min(cache, available);
        return new MemoryStatus((long)status.TotalPhys, available, cached, compressed);
    }
}
