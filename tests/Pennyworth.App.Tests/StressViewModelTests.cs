using Pennyworth.App.Localization;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.Monitoring;
using Pennyworth.Stress;

namespace Pennyworth.App.Tests;

// Asserts English texts and number formats, so it runs apart from tests that switch language.
[Collection(nameof(LanguageSwitching))]
public sealed class StressViewModelTests
{
    private const long Gb = 1024L * 1024 * 1024;

    public StressViewModelTests() => Localizer.Instance.SetLanguage(LanguagePreference.English);

    [Fact]
    public void Starts_the_chosen_tests_with_the_chosen_options()
    {
        var live = Live(cpu: 10);
        StressOptions? started = null;
        var runner = new FakeRunner();
        var stress = new StressViewModel(live, new Host(), options => { started = options; return runner; });
        stress.IncludeMemory = true;
        stress.SetCpuModeCommand.Execute("Integer");
        stress.SetDurationCommand.Execute(15);
        stress.SetMemoryPercentCommand.Execute(25);

        stress.StartCommand.Execute(null);

        Assert.NotNull(started);
        Assert.Equal(StressKind.Cpu | StressKind.Memory, started.Kinds);
        Assert.Equal(CpuMode.Integer, started.CpuMode);
        Assert.Equal(TimeSpan.FromMinutes(15), started.Duration);
        Assert.Equal(2 * Gb, started.MemoryBytes); // 25 % of the 8 GB available
        Assert.True(stress.IsRunning);
        Assert.False(stress.StartCommand.CanExecute(null));
        Assert.Single(stress.Log);
    }

    [Fact]
    public void Nothing_to_start_without_a_part_chosen()
    {
        var stress = new StressViewModel(Live(cpu: 0), new Host(), _ => new FakeRunner()) { IncludeCpu = false };

        Assert.False(stress.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Shows_work_rates_and_a_passed_result()
    {
        var live = Live(cpu: 97);
        var runner = new FakeRunner();
        var host = new Host();
        var stress = new StressViewModel(live, host, _ => runner);
        stress.StartCommand.Execute(null);

        runner.Snapshot = Snapshot(TimeSpan.FromSeconds(1), new WorkloadSnapshot(StressKind.Cpu, 50_000_000_000, 0, WorkPhase.Working, 8, 0, WorkloadFailure.None, null));
        stress.Update();

        Assert.Equal("97", stress.Cpu.ValueText);
        Assert.Equal("%", stress.Cpu.UnitText);
        Assert.True(stress.Cpu.IsUnderTest);
        Assert.Contains("50.0 GFLOP/s", stress.Cpu.Detail);
        Assert.Equal(0.97, stress.StressLevel, 3);
        Assert.Equal("Extreme", stress.StressLevelText);
        Assert.Equal(0.97, stress.RadarValues[0], 3);

        runner.Finish(StressOutcome.Passed);
        await stress.Run;

        Assert.Equal(StressState.Finished, stress.State);
        Assert.Equal(StressResult.Passed, stress.Result);
        Assert.False(stress.IsResultBad);
        Assert.Equal("Stress test: Passed", host.Toast);
        Assert.Equal(0.97, stress.Cpu.Peak, 3);
    }

    [Fact]
    public async Task Logs_the_first_wrong_result_once()
    {
        var runner = new FakeRunner();
        var stress = new StressViewModel(Live(cpu: 100), new Host(), _ => runner);
        stress.StartCommand.Execute(null);

        var bad = new WorkloadSnapshot(StressKind.Cpu, 1000, 2, WorkPhase.Working, 8, 0, WorkloadFailure.None, null);
        runner.Snapshot = Snapshot(TimeSpan.FromSeconds(1), bad);
        stress.Update();
        runner.Snapshot = Snapshot(TimeSpan.FromSeconds(2), bad with { Errors = 5 });
        stress.Update();

        Assert.Equal(2, stress.Log.Count); // started, then the error
        Assert.True(stress.Log[0].IsBad);
        Assert.Equal("5 errors", stress.Cpu.ErrorsText);

        runner.Finish(StressOutcome.ErrorFound);
        await stress.Run;

        Assert.Equal(StressResult.ErrorFound, stress.Result);
        Assert.True(stress.IsResultBad);
    }

    [Fact]
    public async Task Tests_the_graphics_card_and_logs_one_that_gives_wrong_answers()
    {
        StressOptions? started = null;
        var runner = new FakeRunner();
        var stress = new StressViewModel(Live(cpu: 20), new Host(), options => { started = options; return runner; })
        {
            IncludeCpu = false,
            IncludeGpu = true,
        };

        stress.StartCommand.Execute(null);
        Assert.Equal(StressKind.Gpu, started!.Kinds);

        var gpu = new WorkloadSnapshot(StressKind.Gpu, 2_000_000_000_000, 0, WorkPhase.Working, 1, 0, WorkloadFailure.None, null, "Test Card");
        runner.Snapshot = Snapshot(TimeSpan.FromSeconds(1), gpu);
        stress.Update();

        // Windows reports no usage here, so the dial shows the work rate against this run's fastest second.
        Assert.True(stress.Gpu.IsUnderTest);
        Assert.Equal("2000.0", stress.Gpu.ValueText);
        Assert.Equal("GFLOP/s", stress.Gpu.UnitText);
        Assert.Equal(1, stress.Gpu.Level, 3);
        Assert.Contains("Test Card", stress.Gpu.Detail);

        runner.Snapshot = Snapshot(TimeSpan.FromSeconds(2), gpu with { Operations = 3_000_000_000_000, Errors = 3 });
        stress.Update();

        Assert.Equal(0.5, stress.Gpu.Level, 3);
        Assert.Equal("3 errors", stress.Gpu.ErrorsText);
        Assert.StartsWith("The graphics card gave a wrong result", stress.Log[0].Text);

        runner.Finish(StressOutcome.ErrorFound);
        await stress.Run;
        Assert.Equal(StressResult.ErrorFound, stress.Result);
    }

    [Fact]
    public async Task Says_when_there_is_no_graphics_card_to_test()
    {
        var runner = new FakeRunner();
        var stress = new StressViewModel(Live(cpu: 5), new Host(), _ => runner) { IncludeGpu = true };
        stress.StartCommand.Execute(null);

        runner.Snapshot = Snapshot(TimeSpan.FromSeconds(1), new WorkloadSnapshot(StressKind.Gpu, 0, 0, WorkPhase.Done, 1, 0, WorkloadFailure.NoGpu, null));
        runner.Finish(StressOutcome.Failed);
        await stress.Run;

        Assert.Equal(StressResult.Failed, stress.Result);
        Assert.Contains(stress.Log, entry => entry.IsBad && entry.Text.Contains("no graphics card with DirectX 11", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Marks_estimated_power_as_an_estimate()
    {
        var live = new LiveViewModel();
        live.Apply(new MonitorSample(new MemoryStatus(16 * Gb, 8 * Gb, Gb, 0), 100, null, [], PowerWatts: 35, PowerIsEstimate: true));
        var runner = new FakeRunner();
        var stress = new StressViewModel(live, new Host(), _ => runner);
        stress.StartCommand.Execute(null);

        stress.Update();

        Assert.Equal("~35", stress.Power.ValueText);
        Assert.Equal("W", stress.Power.UnitText);
        Assert.StartsWith("Estimate", stress.Power.Detail);

        runner.Finish(StressOutcome.Passed);
        await stress.Run;
        Assert.Equal("~35 W", stress.Stats.Single(s => s.Label == "Peak power").Value);
    }

    [Fact]
    public void Measured_power_has_no_estimate_mark()
    {
        var live = new LiveViewModel();
        live.Apply(new MonitorSample(new MemoryStatus(16 * Gb, 8 * Gb, Gb, 0), 50, null, [], PowerWatts: 42));
        var stress = new StressViewModel(live, new Host(), _ => new FakeRunner());

        stress.Update();

        Assert.Equal("42", stress.Power.ValueText);
        Assert.Equal("Processor power, as Windows reports it", stress.Power.Detail);
    }

    [Fact]
    public async Task Stops_by_itself_when_the_pc_gets_too_hot()
    {
        var live = Live(cpu: 100, celsius: 97);
        var runner = new FakeRunner();
        var stress = new StressViewModel(live, new Host(), _ => runner);
        stress.StartCommand.Execute(null);

        for (int i = 0; i < StressViewModel.TooHotSamples; i++)
        {
            stress.Update();
        }

        Assert.True(runner.Cancelled);
        runner.Finish(StressOutcome.Stopped);
        await stress.Run;

        Assert.Equal(StressResult.TooHot, stress.Result);
        Assert.Equal("97", stress.Temperature.ValueText);
        Assert.Equal("°C", stress.Temperature.UnitText);
    }

    [Fact]
    public async Task Stop_cancels_the_run()
    {
        var runner = new FakeRunner();
        var stress = new StressViewModel(Live(cpu: 50), new Host(), _ => runner);
        stress.StartCommand.Execute(null);

        stress.StopCommand.Execute(null);
        runner.Finish(StressOutcome.Stopped);
        await stress.Run;

        Assert.True(runner.Cancelled);
        Assert.Equal(StressResult.Stopped, stress.Result);
        Assert.True(stress.StartCommand.CanExecute(null));
    }

    [Fact]
    public void Sensors_the_pc_does_not_report_say_so()
    {
        var stress = new StressViewModel(Live(cpu: 5), new Host(), _ => new FakeRunner());

        stress.Update();

        Assert.Equal("—", stress.Temperature.ValueText);
        Assert.Equal("Not reported by this PC", stress.Power.Detail);
        Assert.Equal("Calm", stress.StressLevelText);
        Assert.Equal(6, stress.RadarLabels.Count);
    }

    [Fact]
    public async Task The_shell_shows_the_page_and_a_running_test_in_the_status_bar()
    {
        var runner = new FakeRunner();
        var viewModel = new MainWindowViewModel(TestApp.Services() with { StressRunner = _ => runner });

        viewModel.GoStressCommand.Execute(null);
        Assert.Same(viewModel.Stress, viewModel.CurrentPage);
        Assert.True(viewModel.IsStressPage);
        Assert.Equal("", viewModel.StressStatus);

        viewModel.Stress.StartCommand.Execute(null);
        Assert.Equal("STRESS 00:00", viewModel.StressStatus);

        runner.Finish(StressOutcome.Stopped);
        await viewModel.Stress.Run;
        Assert.Equal("", viewModel.StressStatus);
    }

    private static LiveViewModel Live(double cpu, double? celsius = null)
    {
        var live = new LiveViewModel();
        live.Apply(new MonitorSample(new MemoryStatus(16 * Gb, 8 * Gb, Gb, 0), cpu, null, [], TemperatureCelsius: celsius));
        return live;
    }

    private static StressSnapshot Snapshot(TimeSpan elapsed, params WorkloadSnapshot[] workloads) => new(elapsed, workloads);

    private sealed class FakeRunner : IStressRunner
    {
        private readonly TaskCompletionSource<StressOutcome> _done = new();

        public StressSnapshot Snapshot { get; set; } = StressSnapshot.Empty;

        public bool Cancelled { get; private set; }

        public void Finish(StressOutcome outcome) => _done.TrySetResult(outcome);

        public async Task<StressOutcome> RunAsync(CancellationToken token)
        {
            using (token.Register(() => Cancelled = true))
            {
                return await _done.Task;
            }
        }

        public StressSnapshot Read() => Snapshot;
    }

    private sealed class Host : IDialogHost
    {
        public string? Toast { get; private set; }

        public void ShowDialog(object dialog)
        {
        }

        public void CloseDialog()
        {
        }

        public void ShowToast(string message) => Toast = message;
    }
}
