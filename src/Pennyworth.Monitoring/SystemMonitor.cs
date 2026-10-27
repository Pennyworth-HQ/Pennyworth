using System.Diagnostics;
using System.Runtime.Versioning;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring.Native;

namespace Pennyworth.Monitoring;

/// <summary>Everything the live screens show, sampled at one moment.</summary>
/// <param name="CpuPercent">Whole-machine load, 0 to 100.</param>
/// <param name="GpuPercent">Busiest GPU engine, 0 to 100, or null when unavailable.</param>
/// <param name="Apps">Processes grouped by app, largest memory first.</param>
/// <param name="DiskPercent">Disk active time, 0 to 100, or null when unavailable.</param>
public sealed record MonitorSample(MemoryStatus Memory, double CpuPercent, double? GpuPercent, IReadOnlyList<AppGroup> Apps, double? DiskPercent = null);

public interface ISystemMonitor
{
    /// <summary>Call about once per second; CPU values are averages since the previous call.</summary>
    MonitorSample Sample();
}

/// <summary>Live memory, CPU, GPU and per-app usage, without admin rights.</summary>
[SupportedOSPlatform("windows")]
public sealed class SystemMonitor : ISystemMonitor, IDisposable
{
    private readonly CpuTracker _cpu = new();
    private readonly AppGrouper _grouper;
    private readonly GpuMeter _gpu = new();
    private readonly DiskMeter _disk = new();
    private readonly MemoryCounters _memory = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastSample;

    public SystemMonitor(IEnumerable<ProcessExplanation> explanations)
    {
        _grouper = new AppGrouper(explanations, new WindowsProcessDetails());
        MonitorNative.GetSystemTimes(out long idle, out long kernel, out long user);
        _cpu.UpdateSystem(idle, kernel, user);
        _cpu.UpdateProcesses(ProcessReader.Read(), TimeSpan.Zero, Environment.ProcessorCount);
        _lastSample = _clock.Elapsed;
    }

    public MonitorSample Sample()
    {
        TimeSpan now = _clock.Elapsed;
        TimeSpan elapsed = now - _lastSample;
        _lastSample = now;

        List<ProcessSample> processes = ProcessReader.Read();
        var cpuByPid = _cpu.UpdateProcesses(processes, elapsed, Environment.ProcessorCount);
        MonitorNative.GetSystemTimes(out long idle, out long kernel, out long user);
        double total = _cpu.UpdateSystem(idle, kernel, user);

        long compressed = processes.FirstOrDefault(p => p.Name == "Memory Compression").WorkingSet;
        return new MonitorSample(ProcessReader.ReadMemory(compressed, _memory.ReadFree()), total, _gpu.Sample(), _grouper.Group(processes, cpuByPid), _disk.Sample());
    }

    public void Dispose()
    {
        _gpu.Dispose();
        _disk.Dispose();
        _memory.Dispose();
    }

    /// <summary>Caches what doesn't change while a process lives.</summary>
    private sealed class WindowsProcessDetails : IProcessDetails
    {
        private readonly Dictionary<int, string?> _paths = [];
        private readonly Dictionary<string, (string?, string?)> _versions = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyDictionary<int, List<string>> _services = new Dictionary<int, List<string>>();
        private long? _servicesReadAt;

        public string? GetImagePath(int pid)
        {
            if (_paths.Count > 4096)
            {
                _paths.Clear(); // Process IDs get reused; start fresh now and then.
            }

            if (!_paths.TryGetValue(pid, out string? path))
            {
                _paths[pid] = path = ProcessReader.GetImagePath(pid);
            }

            return path;
        }

        public (string? Description, string? Company) GetVersionInfo(string imagePath)
        {
            if (!_versions.TryGetValue(imagePath, out var info))
            {
                try
                {
                    var version = FileVersionInfo.GetVersionInfo(imagePath);
                    info = (version.FileDescription, version.CompanyName);
                }
                catch (FileNotFoundException)
                {
                    info = (null, null);
                }

                _versions[imagePath] = info;
            }

            return info;
        }

        // Services start and stop rarely; refreshing every 10 s is plenty.
        public IReadOnlyDictionary<int, List<string>> GetServicesByPid()
        {
            if (_servicesReadAt is not { } readAt || Environment.TickCount64 - readAt > 10_000)
            {
                _services = ServiceReader.Read();
                _servicesReadAt = Environment.TickCount64;
            }

            return _services;
        }
    }
}
