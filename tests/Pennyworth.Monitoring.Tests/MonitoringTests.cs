using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring.Native;

namespace Pennyworth.Monitoring.Tests;

public class CpuTrackerTests
{
    [Fact]
    public void System_load_comes_from_busy_share_of_time()
    {
        var tracker = new CpuTracker();
        Assert.Equal(0, tracker.UpdateSystem(idle: 0, kernel: 0, user: 0));

        // 1000 units passed (kernel 900, which includes 750 idle, + user 100): 250 busy = 25%.
        double load = tracker.UpdateSystem(idle: 750, kernel: 900, user: 100);

        Assert.Equal(25, load, 3);
    }

    [Fact]
    public void Process_share_is_relative_to_all_cores()
    {
        var tracker = new CpuTracker();
        var second = TimeSpan.FromSeconds(1);
        tracker.UpdateProcesses([Sample(1, cpu: 0)], second, processorCount: 4);

        // One full core for one second = 1e7 ticks of CPU time, on 4 cores = 25%.
        var result = tracker.UpdateProcesses([Sample(1, cpu: TimeSpan.TicksPerSecond), Sample(2, cpu: 5)], second, processorCount: 4);

        Assert.Equal(25, result[1], 3);
        Assert.Equal(0, result[2]); // first time seen
    }

    [Fact]
    public void A_reused_process_id_starts_over()
    {
        var tracker = new CpuTracker();
        tracker.UpdateProcesses([Sample(1, cpu: 0, created: 1)], TimeSpan.FromSeconds(1), 1);

        var result = tracker.UpdateProcesses([Sample(1, cpu: 5_000_000, created: 2)], TimeSpan.FromSeconds(1), 1);

        Assert.Equal(0, result[1]);
    }

    internal static ProcessSample Sample(int pid, long cpu = 0, long created = 0, string name = "app", long memory = 0) =>
        new(pid, 0, name, memory, memory, cpu, created);
}

public class MemoryStatusTests
{
    [Fact]
    public void Splits_physical_memory()
    {
        var memory = new MemoryStatus(Total: 16, Available: 5, Cached: 4, Compressed: 1);

        Assert.Equal(11, memory.Used);
        Assert.Equal(10, memory.InUse);
        Assert.Equal(1, memory.Free);
    }
}

public class GpuMeterTests
{
    [Fact]
    public void Reports_the_busiest_engine_type()
    {
        double load = GpuMeter.Combine([
            ("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 20),
            ("pid_2_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 15),
            ("pid_2_luid_0x0_0x1_phys_0_eng_4_engtype_VideoDecode", 30),
        ]);

        Assert.Equal(35, load, 3);
        Assert.Equal(100, GpuMeter.Combine([("engtype_3D", 70), ("engtype_3D", 60)]));
        Assert.Equal(0, GpuMeter.Combine([]));
    }
}

public class AppGrouperTests
{
    private static readonly ProcessExplanation Services = new(
        "windows-services", ["svchost"], new("Windows services", "Serviços do Windows"), new("", ""), null,
        IsProtected: true, CanClose: false, ListServices: true);

    private static readonly ProcessExplanation Compression = new(
        "memory-compression", ["Memory Compression"], new("Memory Compression", "Compressão"), new("", ""), null,
        IsProtected: true, CanClose: false);

    [Fact]
    public void Groups_processes_of_one_app_and_sorts_by_memory()
    {
        var grouper = new AppGrouper([Services], new FakeDetails());
        var processes = new[]
        {
            CpuTrackerTests.Sample(10, name: "chrome", memory: 300),
            CpuTrackerTests.Sample(11, name: "Chrome", memory: 200),
            CpuTrackerTests.Sample(20, name: "code", memory: 900),
        };

        var groups = grouper.Group(processes, new Dictionary<int, double> { [10] = 1.5, [11] = 0.5 });

        Assert.Equal(["code", "chrome"], groups.Select(g => g.Key));
        var chrome = groups[1];
        Assert.Equal(500, chrome.Memory);
        Assert.Equal(2.0, chrome.Cpu, 3);
        Assert.Equal("Google Chrome", chrome.Description);
        Assert.Equal("Google LLC", chrome.Company);
        Assert.True(chrome.CanClose);
        Assert.Equal([10, 11], chrome.Processes.Select(p => p.Pid));
    }

    [Fact]
    public void Explained_processes_list_their_services_and_cannot_be_closed()
    {
        var grouper = new AppGrouper([Services], new FakeDetails());

        var group = Assert.Single(grouper.Group([CpuTrackerTests.Sample(4, name: "svchost", memory: 10)], new Dictionary<int, double>()));

        Assert.Equal("windows-services", group.Explanation?.Id);
        Assert.True(group.IsProtected);
        Assert.False(group.CanClose);
        Assert.Null(group.ImagePath);
        Assert.Equal(["Windows Update", "Print Spooler"], group.Processes[0].Services);
    }

    [Fact]
    public void Memory_compression_counts_its_working_set()
    {
        var grouper = new AppGrouper([Compression], new FakeDetails());
        var sample = new ProcessSample(7, 4, "Memory Compression", PrivateMemory: 1, WorkingSet: 900, CpuTime: 0, CreateTime: 0);

        Assert.Equal(900, Assert.Single(grouper.Group([sample], new Dictionary<int, double>())).Memory);
    }

    private sealed class FakeDetails : IProcessDetails
    {
        public string? GetImagePath(int pid) => pid switch
        {
            10 or 11 => @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            20 => @"C:\Code\code.exe",
            _ => null,
        };

        public (string? Description, string? Company) GetVersionInfo(string imagePath) =>
            imagePath.EndsWith("chrome.exe", StringComparison.Ordinal) ? ("Google Chrome", "Google LLC") : (null, null);

        public IReadOnlyDictionary<int, List<string>> GetServicesByPid() =>
            new Dictionary<int, List<string>> { [4] = ["Windows Update", "Print Spooler"] };
    }
}

/// <summary>Runs against the real machine: only checks that the numbers are sane.</summary>
public class SystemMonitorTests
{
    [Fact]
    public void Reads_this_machine()
    {
        using var monitor = new SystemMonitor(KnowledgeBaseLoader.LoadProcesses().Processes);

        MonitorSample sample = monitor.Sample();

        Assert.True(sample.Memory.Total > 512L * 1024 * 1024);
        Assert.InRange(sample.Memory.Available, 1, sample.Memory.Total);
        Assert.InRange(sample.CpuPercent, 0, 100);
        Assert.True(sample.Memory.Free > 0, "Some memory is always free.");
        Assert.True(sample.Memory.Cached > 0);
        Assert.Contains(sample.Apps, app => app.Processes.Any(p => p.Pid == Environment.ProcessId));
        Assert.Contains(sample.Apps, app => app.Explanation?.Id == "windows-services" && app.Processes.Any(p => p.Services.Count > 0));
    }

    [Fact]
    public void Measures_cpu_per_process()
    {
        using var monitor = new SystemMonitor(KnowledgeBaseLoader.LoadProcesses().Processes);
        using var stop = new CancellationTokenSource();
        var busy = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Thread.SpinWait(1000);
            }
        }, TestContext.Current.CancellationToken);

        monitor.Sample();
        Thread.Sleep(600);
        MonitorSample sample = monitor.Sample();
        stop.Cancel();

        double mine = sample.Apps.SelectMany(app => app.Processes).Single(p => p.Pid == Environment.ProcessId).Cpu;
        Assert.True(mine > 100.0 / Environment.ProcessorCount / 2, $"Expected about one busy core, got {mine:0.00}%.");
    }
}
