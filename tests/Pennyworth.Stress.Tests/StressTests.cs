namespace Pennyworth.Stress.Tests;

public sealed class StressTests
{
    // Long enough for a unit of work even while other test projects load the machine: the test
    // threads run below normal priority.
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(1);

    [Theory]
    [InlineData(CpuMode.FloatingPoint)]
    [InlineData(CpuMode.Integer)]
    [InlineData(CpuMode.Mixed)]
    public async Task Cpu_runs_and_finds_no_errors_on_a_healthy_processor(CpuMode mode)
    {
        var cpu = new CpuWorkload(mode, threads: 2, unitScale: 0.001);
        var runner = new StressRunner(new StressOptions(StressKind.Cpu, Short), [cpu]);

        StressOutcome outcome = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StressOutcome.Passed, outcome);
        WorkloadSnapshot snapshot = runner.Read()[StressKind.Cpu]!;
        Assert.True(snapshot.Operations > 0);
        Assert.Equal(0, snapshot.Errors);
        Assert.Equal(WorkPhase.Done, snapshot.Phase);
    }

    [Fact]
    public void Cpu_units_are_repeatable()
    {
        Assert.Equal(CpuWorkload.FloatUnit(1000), CpuWorkload.FloatUnit(1000));
        Assert.Equal(CpuWorkload.IntegerUnit(1000), CpuWorkload.IntegerUnit(1000));
        Assert.NotEqual(CpuWorkload.IntegerUnit(1000), CpuWorkload.IntegerUnit(1001));
    }

    [Fact]
    public void Cpu_units_stay_repeatable_after_the_jit_optimizes_them()
    {
        // The first result is the reference; a later, re-jitted copy must not round differently.
        ulong first = CpuWorkload.FloatUnit(2000);
        ulong integer = CpuWorkload.IntegerUnit(2000);
        for (int i = 0; i < 3000; i++)
        {
            Assert.Equal(first, CpuWorkload.FloatUnit(2000));
            Assert.Equal(integer, CpuWorkload.IntegerUnit(2000));
        }
    }

    [Fact]
    public async Task A_wrong_cpu_result_stops_the_run()
    {
        int units = 0;
        var cpu = new CpuWorkload(CpuMode.Integer, threads: 1, unitScale: 0.01)
        {
            Tamper = (_, result) => Interlocked.Increment(ref units) == 5 ? result ^ 1 : result,
        };
        var runner = new StressRunner(new StressOptions(StressKind.Cpu, TimeSpan.FromSeconds(30)), [cpu]);

        StressOutcome outcome = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StressOutcome.ErrorFound, outcome);
        Assert.True(runner.Read().Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Memory_is_written_and_checked()
    {
        var memory = new MemoryWorkload(4 * 1024 * 1024, threads: 2, blockBytes: 1024 * 1024);
        var runner = new StressRunner(new StressOptions(StressKind.Memory, Short), [memory]);

        Assert.Equal(StressOutcome.Passed, await runner.RunAsync(TestContext.Current.CancellationToken));
        WorkloadSnapshot snapshot = runner.Read()[StressKind.Memory]!;
        Assert.True(snapshot.Operations >= 2L * 1024 * 1024);
        Assert.Equal(4 * 1024 * 1024, snapshot.Bytes);
        Assert.Equal(2, snapshot.Threads);
    }

    [Fact]
    public async Task A_flipped_memory_bit_is_found()
    {
        var memory = new MemoryWorkload(1024 * 1024, threads: 1, blockBytes: 1024 * 1024)
        {
            Tamper = block => block[123] ^= 1UL << 7,
        };
        var runner = new StressRunner(new StressOptions(StressKind.Memory, TimeSpan.FromSeconds(30), StopOnError: false), [memory]);
        using var stop = new CancellationTokenSource(Short);

        StressOutcome outcome = await runner.RunAsync(stop.Token);

        Assert.Equal(StressOutcome.ErrorFound, outcome);
        Assert.True(runner.Read().Errors > 0);
    }

    [Fact]
    public async Task Disk_file_is_written_checked_and_deleted()
    {
        string folder = Path.Combine(Path.GetTempPath(), "pennyworth-stress-tests", Guid.NewGuid().ToString("N"));
        var disk = new DiskWorkload(folder, 2 * 1024 * 1024, chunkBytes: 1024 * 1024);
        var runner = new StressRunner(new StressOptions(StressKind.Disk, Short), [disk]);

        StressOutcome outcome = await runner.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StressOutcome.Passed, outcome);
        Assert.True(runner.Read()[StressKind.Disk]!.Operations > 0);
        Assert.Empty(Directory.GetFiles(folder));
        Directory.Delete(folder);
    }

    [Fact]
    public async Task A_bad_disk_read_is_found()
    {
        string folder = Path.Combine(Path.GetTempPath(), "pennyworth-stress-tests", Guid.NewGuid().ToString("N"));
        var disk = new DiskWorkload(folder, 1024 * 1024, chunkBytes: 1024 * 1024)
        {
            Tamper = chunk => chunk[10] ^= 0xFF,
        };
        var runner = new StressRunner(new StressOptions(StressKind.Disk, TimeSpan.FromSeconds(30)), [disk]);

        Assert.Equal(StressOutcome.ErrorFound, await runner.RunAsync(TestContext.Current.CancellationToken));
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task Cancelling_stops_every_test()
    {
        var runner = new StressRunner(new StressOptions(StressKind.Cpu | StressKind.Memory, TimeSpan.FromMinutes(5)),
            [new CpuWorkload(CpuMode.Mixed, threads: 2, unitScale: 0.01), new MemoryWorkload(1024 * 1024, blockBytes: 512 * 1024)]);
        using var stop = new CancellationTokenSource(Short);

        StressOutcome outcome = await runner.RunAsync(stop.Token);

        Assert.Equal(StressOutcome.Stopped, outcome);
        Assert.All(runner.Read().Workloads, w => Assert.Equal(WorkPhase.Done, w.Phase));
    }

    [Fact]
    public async Task Not_enough_memory_fails_the_run()
    {
        var runner = new StressRunner(new StressOptions(StressKind.Memory, TimeSpan.FromSeconds(30)),
            [new MemoryWorkload(long.MaxValue / 2, blockBytes: int.MaxValue - 1024 * 1024)]);

        Assert.Equal(StressOutcome.Failed, await runner.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(WorkloadFailure.NotEnoughMemory, runner.Read()[StressKind.Memory]!.Failure);
    }

    [Fact]
    public void Options_choose_the_tests()
    {
        var runner = new StressRunner(new StressOptions(StressKind.Cpu | StressKind.Disk, Short, DiskFolder: Path.GetTempPath(), DiskBytes: 1024));

        Assert.Equal([StressKind.Cpu, StressKind.Disk], runner.Workloads.Select(w => w.Kind));
    }
}
