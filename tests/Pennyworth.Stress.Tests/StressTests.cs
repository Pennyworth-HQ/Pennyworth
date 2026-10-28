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
        var runner = new StressRunner(new StressOptions(StressKind.Cpu | StressKind.Disk | StressKind.Gpu, Short, DiskFolder: Path.GetTempPath(), DiskBytes: 1024));

        Assert.Equal([StressKind.Cpu, StressKind.Disk, StressKind.Gpu], runner.Workloads.Select(w => w.Kind));
    }

    [Fact]
    public async Task Gpu_answers_are_checked_against_the_first_ones()
    {
        var card = new FakeGpu();
        var gpu = new GpuWorkload(() => card);
        var runner = new StressRunner(new StressOptions(StressKind.Gpu, Short), [gpu]);

        Assert.Equal(StressOutcome.Passed, await runner.RunAsync(TestContext.Current.CancellationToken));
        WorkloadSnapshot snapshot = runner.Read()[StressKind.Gpu]!;
        Assert.True(snapshot.Operations > 0);
        Assert.Equal(0, snapshot.Errors);
        Assert.Equal("Fake card", snapshot.Device);
        Assert.True(card.Disposed);
    }

    [Fact]
    public async Task A_wrong_gpu_answer_is_found()
    {
        int units = 0;
        var gpu = new GpuWorkload(() => new FakeGpu())
        {
            Tamper = results =>
            {
                if (Interlocked.Increment(ref units) == 3)
                {
                    results[7] ^= 1;
                    results[9] ^= 1;
                }
            },
        };
        var runner = new StressRunner(new StressOptions(StressKind.Gpu, TimeSpan.FromSeconds(30)), [gpu]);

        Assert.Equal(StressOutcome.ErrorFound, await runner.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, runner.Read().Errors);
    }

    [Fact]
    public async Task A_driver_reset_counts_as_an_error()
    {
        var gpu = new GpuWorkload(() => new FakeGpu { LoseAfter = 2 });
        var runner = new StressRunner(new StressOptions(StressKind.Gpu, TimeSpan.FromSeconds(30)), [gpu]);

        Assert.Equal(StressOutcome.ErrorFound, await runner.RunAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task No_graphics_card_fails_the_run()
    {
        var runner = new StressRunner(new StressOptions(StressKind.Gpu, TimeSpan.FromSeconds(30)), [new GpuWorkload(() => null)]);

        Assert.Equal(StressOutcome.Failed, await runner.RunAsync(TestContext.Current.CancellationToken));
        Assert.Equal(WorkloadFailure.NoGpu, runner.Read()[StressKind.Gpu]!.Failure);
    }

    [Fact]
    public async Task The_real_graphics_card_gives_repeatable_answers()
    {
        var gpu = new GpuWorkload();
        var runner = new StressRunner(new StressOptions(StressKind.Gpu, TimeSpan.FromSeconds(2)), [gpu]);

        StressOutcome outcome = await runner.RunAsync(TestContext.Current.CancellationToken);

        WorkloadSnapshot snapshot = runner.Read()[StressKind.Gpu]!;
        if (snapshot.Failure == WorkloadFailure.NoGpu)
        {
            Assert.Skip("No Direct3D 11 graphics card on this machine.");
        }

        Assert.Equal(StressOutcome.Passed, outcome);
        Assert.True(snapshot.Operations > 0);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Device));
    }

    private sealed class FakeGpu : IGpuCompute
    {
        private int _units;

        public string Name => "Fake card";

        public int Width => 16;

        public long OperationsPerUnit => 1000;

        public int LoseAfter { get; init; } = int.MaxValue;

        public bool Disposed { get; private set; }

        public void Run(Span<uint> results)
        {
            if (++_units > LoseAfter)
            {
                throw new GpuLostException(unchecked((int)0x887A0005));
            }

            for (int i = 0; i < results.Length; i++)
            {
                results[i] = (uint)(i * 2654435761);
            }

            Thread.Sleep(1);
        }

        public void Dispose() => Disposed = true;
    }
}
