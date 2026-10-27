using System.Collections.ObjectModel;
using System.Globalization;
using Pennyworth.App.Resources;
using Pennyworth.Core.Formatting;
using Pennyworth.Stress;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

public enum StressState
{
    Idle,
    Running,
    Finished,
}

/// <summary>How the last run ended, as the result card shows it.</summary>
public enum StressResult
{
    None,
    Passed,
    ErrorFound,
    Stopped,
    Failed,
    TooHot,
}

/// <summary>
/// The stress test screen, after OCCT, Prime95 and HeavyLoad: choose what to load and for how
/// long, then watch a radar of the whole PC, a dial per part, the work done per second, the
/// errors found and a log. Stops by itself at the end, at the first wrong result (if asked) and
/// when the PC reports a dangerous temperature.
/// </summary>
public sealed partial class StressViewModel : ObservableObject
{
    public const int HistoryLength = 60;

    /// <summary>Stop when the hottest sensor stays at or above this for <see cref="TooHotSamples"/> readings.</summary>
    public const double TooHotCelsius = 95;
    public const int TooHotSamples = 3;

    /// <summary>Power is drawn against this until the PC has drawn more: a typical desktop processor.</summary>
    private const double ReferenceWatts = 65;

    private const long DefaultDiskBytes = 1024L * 1024 * 1024;
    private const long MinDiskBytes = 64L * 1024 * 1024;

    public static IReadOnlyList<int> DurationChoices { get; } = [1, 5, 15, 30, 60];

    public static IReadOnlyList<int> MemoryChoices { get; } = [25, 50, 75];

    private readonly LiveViewModel _live;
    private readonly IDialogHost _host;
    private readonly Func<StressOptions, IStressRunner> _createRunner;
    private readonly Func<DateTimeOffset> _clock;
    private readonly HashSet<StressKind> _reported = [];
    private IStressRunner? _runner;
    private CancellationTokenSource? _cancel;
    private StressSnapshot _last = StressSnapshot.Empty;
    private StressKind _kinds;
    private TimeSpan _duration;
    private int _hotSamples;
    private bool _tooHot;
    private double _maxDiskRate;
    private double _maxGpuRate;
    private double _maxWatts = ReferenceWatts;
    private double _peakCpu;
    private double? _peakCelsius;
    private double? _peakWatts;
    private bool _peakWattsIsEstimate;

    public StressViewModel(LiveViewModel live, IDialogHost host, Func<StressOptions, IStressRunner>? createRunner = null, Func<DateTimeOffset>? clock = null)
    {
        _live = live;
        _host = host;
        _createRunner = createRunner ?? (options => new StressRunner(options));
        _clock = clock ?? (() => DateTimeOffset.Now);
        Cpu = new StressMeterViewModel("Cpu", () => Strings.StressCpu);
        Memory = new StressMeterViewModel("Memory", () => Strings.StressMemory);
        Disk = new StressMeterViewModel("Disk", () => Strings.StressDisk);
        Gpu = new StressMeterViewModel("Gpu", () => Strings.StressGpu);
        Temperature = new StressMeterViewModel("Temperature", () => Strings.StressTemperature);
        Power = new StressMeterViewModel("Power", () => Strings.StressPower);
        Meters = [Cpu, Memory, Disk, Gpu, Temperature, Power];
    }

    public StressMeterViewModel Cpu { get; }

    public StressMeterViewModel Memory { get; }

    public StressMeterViewModel Disk { get; }

    public StressMeterViewModel Gpu { get; }

    public StressMeterViewModel Temperature { get; }

    public StressMeterViewModel Power { get; }

    /// <summary>Every dial, in radar order.</summary>
    public IReadOnlyList<StressMeterViewModel> Meters { get; }

    public ObservableCollection<StressLogEntry> Log { get; } = [];

    public bool HasLog => Log.Count > 0;

    // ---- Choices ----

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IncludeCpu { get; set; } = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IncludeMemory { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IncludeDisk { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool IncludeGpu { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFloatMode), nameof(IsIntegerMode), nameof(IsMixedMode))]
    public partial CpuMode CpuMode { get; set; }

    public bool IsFloatMode => CpuMode == CpuMode.FloatingPoint;

    public bool IsIntegerMode => CpuMode == CpuMode.Integer;

    public bool IsMixedMode => CpuMode == CpuMode.Mixed;

    [ObservableProperty]
    public partial int DurationMinutes { get; set; } = 5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MemoryHint))]
    public partial int MemoryPercent { get; set; } = 50;

    [ObservableProperty]
    public partial bool StopOnError { get; set; } = true;

    public IReadOnlyList<DurationChoice> Durations => [.. DurationChoices.Select(m => new DurationChoice(m, m == DurationMinutes))];

    public IReadOnlyList<PercentChoice> MemoryShares => [.. MemoryChoices.Select(p => new PercentChoice(p, p == MemoryPercent))];

    public string CpuHint => string.Format(CultureInfo.CurrentCulture, Strings.StressCpuHint, "", Environment.ProcessorCount);

    public string MemoryHint => string.Format(CultureInfo.CurrentCulture, Strings.StressMemoryHint, ByteSize.Format(PlannedMemoryBytes()));

    public string DiskHint => string.Format(CultureInfo.CurrentCulture, Strings.StressDiskHint, ByteSize.Format(PlannedDiskBytes()));

    // ---- Run state ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsIdle), nameof(HasResult), nameof(CanChoose))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    public partial StressState State { get; private set; }

    public bool IsRunning => State == StressState.Running;

    public bool IsIdle => !IsRunning;

    public bool CanChoose => !IsRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(ResultTitle), nameof(ResultText), nameof(ResultGlyph), nameof(IsResultBad))]
    public partial StressResult Result { get; private set; }

    public bool HasResult => State == StressState.Finished && Result != StressResult.None;

    public bool IsResultBad => Result is StressResult.ErrorFound or StressResult.Failed or StressResult.TooHot;

    public string ResultGlyph => Result switch
    {
        StressResult.Passed => "✓",
        StressResult.ErrorFound => "✕",
        StressResult.TooHot => "▲",
        StressResult.Failed => "!",
        _ => "■",
    };

    public string ResultTitle => Result switch
    {
        StressResult.Passed => Strings.StressPassed,
        StressResult.ErrorFound => Strings.StressErrorFound,
        StressResult.TooHot => Strings.StressTooHot,
        StressResult.Failed => Strings.StressFailed,
        _ => Strings.StressStopped,
    };

    public string ResultText => Result switch
    {
        StressResult.Passed => string.Format(CultureInfo.CurrentCulture, Strings.StressPassedSub, Clock(Elapsed)),
        StressResult.ErrorFound => Strings.StressErrorFoundSub,
        StressResult.TooHot => string.Format(CultureInfo.CurrentCulture, Strings.StressTooHotSub, Celsius(_peakCelsius)),
        StressResult.Failed => Strings.StressFailedSub,
        _ => string.Format(CultureInfo.CurrentCulture, Strings.StressStoppedSub, Clock(Elapsed)),
    };

    /// <summary>The run's highlights, for the result card.</summary>
    public IReadOnlyList<StressStat> Stats =>
    [
        new(Strings.StressStatDuration, Clock(Elapsed)),
        new(Strings.StressStatPeakCpu, $"{Math.Round(_peakCpu):0}%"),
        new(Strings.StressStatPeakTemp, Celsius(_peakCelsius)),
        new(Strings.StressStatPeakPower, (_peakWattsIsEstimate && _peakWatts is not null ? "~" : "") + Watts(_peakWatts)),
        new(Strings.StressErrors, TotalErrors.ToString("N0", CultureInfo.CurrentCulture)),
    ];

    public TimeSpan Elapsed => _last.Elapsed;

    public long TotalErrors => _last.Errors;

    public string ElapsedText => Clock(Elapsed);

    public string RemainingText => Clock(_duration > Elapsed ? _duration - Elapsed : TimeSpan.Zero);

    public double Progress => _duration <= TimeSpan.Zero ? 0 : Math.Clamp(Elapsed / _duration, 0, 1);

    public string ErrorsText => TotalErrors == 0 ? Strings.StressNoErrors : string.Format(CultureInfo.CurrentCulture, Strings.StressErrorsCount, "", TotalErrors);

    // ---- Overall stress and radar ----

    /// <summary>
    /// The busiest part, 0 to 1: what the stress icon and level bars show. Memory in use only
    /// counts while it is under test; a full memory at rest is normal, not stress.
    /// </summary>
    public double StressLevel => Meters.Where(m => m != Memory || m.IsUnderTest).Max(m => m.Level);

    public string StressLevelText => StressLevel switch
    {
        < 0.25 => Strings.StressLevelCalm,
        < 0.5 => Strings.StressLevelModerate,
        < 0.85 => Strings.StressLevelHigh,
        _ => Strings.StressLevelExtreme,
    };

    public IReadOnlyList<string> RadarLabels => [.. Meters.Select(m => m.Title.ToUpper(CultureInfo.CurrentCulture))];

    public IReadOnlyList<double> RadarValues => [.. Meters.Select(m => m.Level)];

    public IReadOnlyList<double> RadarPeaks => [.. Meters.Select(m => m.Peak)];

    // ---- Commands ----

    [RelayCommand]
    private void SetCpuMode(string mode) => CpuMode = Enum.Parse<CpuMode>(mode);

    [RelayCommand]
    private void SetDuration(int minutes)
    {
        DurationMinutes = minutes;
        OnPropertyChanged(nameof(Durations));
    }

    [RelayCommand]
    private void SetMemoryPercent(int percent)
    {
        MemoryPercent = percent;
        OnPropertyChanged(nameof(MemoryShares));
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        var kinds = (IncludeCpu ? StressKind.Cpu : StressKind.None)
            | (IncludeMemory ? StressKind.Memory : StressKind.None)
            | (IncludeDisk ? StressKind.Disk : StressKind.None)
            | (IncludeGpu ? StressKind.Gpu : StressKind.None);
        var options = new StressOptions(
            kinds,
            TimeSpan.FromMinutes(DurationMinutes),
            CpuMode,
            MemoryBytes: IncludeMemory ? PlannedMemoryBytes() : 0,
            DiskFolder: IncludeDisk ? DiskFolder : null,
            DiskBytes: IncludeDisk ? PlannedDiskBytes() : 0,
            StopOnError: StopOnError);

        _kinds = kinds;
        _duration = options.Duration;
        _reported.Clear();
        _last = StressSnapshot.Empty;
        _hotSamples = 0;
        _tooHot = false;
        _maxDiskRate = 0;
        _maxGpuRate = 0;
        _peakCpu = 0;
        _peakCelsius = null;
        _peakWatts = null;
        _peakWattsIsEstimate = false;
        foreach (StressMeterViewModel meter in Meters)
        {
            meter.Reset();
        }

        Log.Clear();
        Result = StressResult.None;
        AddLog(string.Format(CultureInfo.CurrentCulture, Strings.StressLogStarted, Describe(options)), bad: false);

        _cancel = new CancellationTokenSource();
        _runner = _createRunner(options);
        State = StressState.Running;
        Run = RunAsync(_runner, _cancel);
        Update();
    }

    private bool CanStart() => !IsRunning && (IncludeCpu || IncludeMemory || IncludeDisk || IncludeGpu);

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        if (_cancel is { IsCancellationRequested: false } cancel)
        {
            if (!_tooHot)
            {
                AddLog(Strings.StressLogStoppedByUser, bad: false);
            }

            cancel.Cancel();
        }
    }

    /// <summary>The run started by <see cref="StartCommand"/>; lets tests await it.</summary>
    public Task Run { get; private set; } = Task.CompletedTask;

    /// <summary>Stops a running test; called when the app closes.</summary>
    public void Shutdown() => _cancel?.Cancel();

    /// <summary>Once per second: live readings, work rates, errors and the temperature guard.</summary>
    public void Update()
    {
        StressSnapshot now = _runner?.Read() ?? StressSnapshot.Empty;
        StressSnapshot previous = _last;
        if (_runner is not null)
        {
            _last = now;
        }

        double seconds = (now.Elapsed - previous.Elapsed).TotalSeconds;
        double Rate(StressKind kind) =>
            seconds <= 0 || now[kind] is not { } current ? 0 : Math.Max(current.Operations - (previous[kind]?.Operations ?? 0), 0) / seconds;

        bool running = IsRunning;
        UpdateCpu(now[StressKind.Cpu], running ? Rate(StressKind.Cpu) : 0, running);
        UpdateMemory(now[StressKind.Memory], running ? Rate(StressKind.Memory) : 0, running);
        UpdateDisk(now[StressKind.Disk], running ? Rate(StressKind.Disk) : 0, running);
        UpdateGpu(now[StressKind.Gpu], running ? Rate(StressKind.Gpu) : 0, running);
        UpdateSensors(running);

        if (running)
        {
            ReportProblems(now);
        }

        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ErrorsText));
        OnPropertyChanged(nameof(TotalErrors));
        OnPropertyChanged(nameof(StressLevel));
        OnPropertyChanged(nameof(StressLevelText));
        OnPropertyChanged(nameof(RadarValues));
        OnPropertyChanged(nameof(RadarPeaks));
        OnPropertyChanged(nameof(MemoryHint));
    }

    /// <summary>Re-reads every text after a language switch.</summary>
    public void RefreshTexts()
    {
        foreach (StressMeterViewModel meter in Meters)
        {
            meter.Refresh();
        }

        OnPropertyChanged(string.Empty);
    }

    private void UpdateCpu(WorkloadSnapshot? work, double rate, bool running)
    {
        double load = _live.Cpu;
        if (running)
        {
            _peakCpu = Math.Max(_peakCpu, load);
        }

        bool tested = running && work is not null;
        string detail = tested
            ? $"{Rate(rate / 1e9)} {(CpuMode == CpuMode.FloatingPoint ? "GFLOP/s" : "G ops/s")} · {string.Format(CultureInfo.CurrentCulture, Strings.StressThreads, "", work!.Threads)}"
            : Strings.StressNotTested;
        Cpu.Set(load / 100, $"{Math.Round(load):0}", "%", detail, tested, work?.Errors ?? 0);
    }

    private void UpdateMemory(WorkloadSnapshot? work, double rate, bool running)
    {
        bool tested = running && work is not null;
        string detail = tested
            ? $"{ByteSize.Format((long)rate)}/s · {Phase(work!)} {ByteSize.Format(work!.Bytes)}"
            : Strings.StressNotTested;
        Memory.Set(_live.MemoryUsedFraction, $"{Math.Round(_live.MemoryUsedFraction * 100):0}", "%", detail, tested, work?.Errors ?? 0);
    }

    private void UpdateDisk(WorkloadSnapshot? work, double rate, bool running)
    {
        bool tested = running && work is not null;
        if (!tested)
        {
            Disk.Set(0, "—", "", Strings.StressNotTested, false, work?.Errors ?? 0);
            return;
        }

        // There is no "100 %" for a drive: the dial is relative to the fastest second of this run.
        _maxDiskRate = Math.Max(_maxDiskRate, rate);
        string[] size = ByteSize.Format((long)rate).Split(' ');
        Disk.Set(_maxDiskRate <= 0 ? 0 : rate / _maxDiskRate, size[0], $"{size[^1]}/s", $"{Phase(work!)} · {ByteSize.Format(work!.Bytes)}", true, work.Errors);
    }

    private void UpdateGpu(WorkloadSnapshot? work, double rate, bool running)
    {
        bool tested = running && work is not null;
        if (!tested)
        {
            if (_live.Gpu is { } usage)
            {
                Gpu.Set(usage / 100, $"{Math.Round(usage):0}", "%", Strings.StressGpuNote, false, work?.Errors ?? 0);
            }
            else
            {
                Gpu.Set(0, "—", "", Strings.StressNotReported, false, work?.Errors ?? 0);
            }

            return;
        }

        string detail = work!.Phase == WorkPhase.Preparing
            ? Strings.StressPreparing
            : $"{Rate(rate / 1e9)} GFLOP/s · {work.Device}";
        if (_live.Gpu is { } gpu)
        {
            Gpu.Set(gpu / 100, $"{Math.Round(gpu):0}", "%", detail, true, work.Errors);
        }
        else
        {
            // Without Windows' usage counter, the dial is relative to the fastest second of this run.
            _maxGpuRate = Math.Max(_maxGpuRate, rate);
            Gpu.Set(_maxGpuRate <= 0 ? 0 : rate / _maxGpuRate, Rate(rate / 1e9), "GFLOP/s", detail, true, work.Errors);
        }
    }

    private void UpdateSensors(bool running)
    {
        if (_live.Sample?.TemperatureCelsius is { } celsius)
        {
            Temperature.Set(Math.Clamp(celsius / 100, 0, 1), $"{Math.Round(celsius):0}", "°C", Strings.StressSensorAcpi);
            if (running)
            {
                _peakCelsius = Math.Max(_peakCelsius ?? celsius, celsius);
                _hotSamples = celsius >= TooHotCelsius ? _hotSamples + 1 : 0;
                if (_hotSamples >= TooHotSamples && !_tooHot)
                {
                    _tooHot = true;
                    AddLog(string.Format(CultureInfo.CurrentCulture, Strings.StressLogTooHot, Celsius(celsius)), bad: true);
                    _cancel?.Cancel();
                }
            }
        }
        else
        {
            Temperature.Set(0, "—", "", Strings.StressNotReported);
        }

        if (_live.Sample?.PowerWatts is { } watts)
        {
            // An estimate is marked with "~" everywhere it shows, and its own caption says how it is made.
            bool estimate = _live.Sample.PowerIsEstimate;
            _maxWatts = Math.Max(_maxWatts, watts);
            Power.Set(watts / _maxWatts, $"{(estimate ? "~" : "")}{Math.Round(watts):0}", "W", estimate ? Strings.StressSensorPowerEstimate : Strings.StressSensorPower);
            if (running)
            {
                _peakWatts = Math.Max(_peakWatts ?? watts, watts);
                _peakWattsIsEstimate = estimate;
            }
        }
        else
        {
            Power.Set(0, "—", "", Strings.StressNotReported);
        }
    }

    /// <summary>Logs each test's first wrong result and any test that could not run, once.</summary>
    private void ReportProblems(StressSnapshot snapshot)
    {
        foreach (WorkloadSnapshot work in snapshot.Workloads)
        {
            if (_reported.Contains(work.Kind))
            {
                continue;
            }

            string? message = work.Failure switch
            {
                WorkloadFailure.NotEnoughMemory => Strings.StressLogNoMemory,
                WorkloadFailure.NotEnoughDiskSpace => Strings.StressLogNoDisk,
                WorkloadFailure.NoGpu => Strings.StressLogNoGpu,
                WorkloadFailure.Crashed => string.Format(CultureInfo.CurrentCulture, Strings.StressLogCrashed, Title(work.Kind)),
                _ when work.Errors > 0 => work.Kind switch
                {
                    StressKind.Cpu => Strings.StressLogCpuError,
                    StressKind.Memory => Strings.StressLogMemoryError,
                    StressKind.Gpu => Strings.StressLogGpuError,
                    _ => Strings.StressLogDiskError,
                },
                _ => null,
            };

            if (message is not null)
            {
                _reported.Add(work.Kind);
                AddLog(message, bad: true);
            }
        }
    }

    private async Task RunAsync(IStressRunner runner, CancellationTokenSource cancel)
    {
        StressOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(cancel.Token);
        }
        catch (Exception ex)
        {
            // The runner reports problems as outcomes; anything else must not take the window down.
            System.Diagnostics.Debug.WriteLine(ex);
            outcome = StressOutcome.Failed;
        }

        Update();
        ReportProblems(_last = runner.Read());
        cancel.Dispose();
        _cancel = null;
        _runner = null;

        Result = _tooHot ? StressResult.TooHot : outcome switch
        {
            StressOutcome.Passed => StressResult.Passed,
            StressOutcome.ErrorFound => StressResult.ErrorFound,
            StressOutcome.Failed => StressResult.Failed,
            _ => StressResult.Stopped,
        };
        State = StressState.Finished;
        AddLog(ResultTitle, IsResultBad);
        Update();
        OnPropertyChanged(nameof(Stats));
        _host.ShowToast($"{Strings.StressTitle}: {ResultTitle}");
    }

    private void AddLog(string text, bool bad)
    {
        Log.Insert(0, new StressLogEntry(_clock().ToString("HH:mm:ss", CultureInfo.InvariantCulture), text, bad));
        OnPropertyChanged(nameof(HasLog));
    }

    private string Describe(StressOptions options)
    {
        var parts = new List<string>();
        if (options.Kinds.HasFlag(StressKind.Cpu))
        {
            string mode = options.CpuMode switch
            {
                CpuMode.FloatingPoint => Strings.StressModeFloat,
                CpuMode.Integer => Strings.StressModeInteger,
                _ => Strings.StressModeMixed,
            };
            parts.Add($"{Strings.StressCpu} ({mode.ToLower(CultureInfo.CurrentCulture)})");
        }

        if (options.Kinds.HasFlag(StressKind.Memory))
        {
            parts.Add($"{Strings.StressMemory} {ByteSize.Format(options.MemoryBytes)}");
        }

        if (options.Kinds.HasFlag(StressKind.Disk))
        {
            parts.Add($"{Strings.StressDisk} {ByteSize.Format(options.DiskBytes)}");
        }

        if (options.Kinds.HasFlag(StressKind.Gpu))
        {
            parts.Add(Strings.StressGpu);
        }

        parts.Add($"{options.Duration.TotalMinutes:0} min");
        return string.Join(" · ", parts);
    }

    private long PlannedMemoryBytes()
    {
        long available = _live.Memory.Available;
        return available > 0 ? available * MemoryPercent / 100 : 1024L * 1024 * 1024;
    }

    private static string DiskFolder => Path.Combine(Path.GetTempPath(), "Pennyworth", "stress");

    /// <summary>1 GB, or less on a nearly full drive: the test always leaves its reserve free.</summary>
    private static long PlannedDiskBytes()
    {
        try
        {
            long free = new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).AvailableFreeSpace;
            return Math.Clamp((free - DiskWorkload.Reserve) / 2, MinDiskBytes, DefaultDiskBytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return MinDiskBytes;
        }
    }

    private static string Title(StressKind kind) => kind switch
    {
        StressKind.Cpu => Strings.StressCpu,
        StressKind.Memory => Strings.StressMemory,
        StressKind.Gpu => Strings.StressGpu,
        _ => Strings.StressDisk,
    };

    private static string Phase(WorkloadSnapshot work) => work.Phase switch
    {
        WorkPhase.Preparing => Strings.StressPreparing,
        WorkPhase.Writing => Strings.StressWriting,
        WorkPhase.Reading => Strings.StressReading,
        _ => Strings.StressChecking,
    };

    private static string Rate(double value) => value.ToString(value < 10 ? "0.00" : "0.0", CultureInfo.CurrentCulture);

    private static string Celsius(double? celsius) => celsius is { } c ? $"{Math.Round(c):0} °C" : "—";

    private static string Watts(double? watts) => watts is { } w ? $"{Math.Round(w):0} W" : "—";

    private static string Clock(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
}

/// <summary>One dial: a part of the PC, how hard it is working (0 to 1) and the last minute of it.</summary>
public sealed partial class StressMeterViewModel(string kind, Func<string> title) : ObservableObject
{
    private readonly Queue<double> _history = new(Enumerable.Repeat(0d, StressViewModel.HistoryLength));

    /// <summary>The icon: Cpu, Memory, Disk, Gpu, Temperature or Power.</summary>
    public string Kind { get; } = kind;

    public string Title => title();

    public double Level { get; private set; }

    /// <summary>The highest level since the last run started.</summary>
    public double Peak { get; private set; }

    public bool IsHot => Level >= Controls.RadialGauge.Hot;

    /// <summary>The number in the dial ("97"); <see cref="UnitText"/> goes under it ("%").</summary>
    public string ValueText { get; private set; } = "—";

    public string UnitText { get; private set; } = "";

    public string Detail { get; private set; } = "";

    public bool IsUnderTest { get; private set; }

    public long Errors { get; private set; }

    public bool HasErrors => Errors > 0;

    /// <summary>Only parts with a check (processor, memory, disk, graphics card) under test show an error count.</summary>
    public bool ShowErrors => IsUnderTest || HasErrors;

    public string ErrorsText => Errors == 0 ? Strings.StressNoErrors : string.Format(CultureInfo.CurrentCulture, Strings.StressErrorsCount, "", Errors);

    /// <summary>The last 60 levels, in percent, oldest first.</summary>
    public IReadOnlyList<double> History => [.. _history];

    internal void Set(double level, string value, string unit, string detail, bool underTest = false, long errors = 0)
    {
        Level = Math.Clamp(double.IsFinite(level) ? level : 0, 0, 1);
        Peak = Math.Max(Peak, Level);
        ValueText = value;
        UnitText = unit;
        Detail = detail;
        IsUnderTest = underTest;
        Errors = errors;
        _history.Enqueue(Level * 100);
        while (_history.Count > StressViewModel.HistoryLength)
        {
            _history.Dequeue();
        }

        OnPropertyChanged(string.Empty);
    }

    internal void Reset()
    {
        Peak = 0;
        Errors = 0;
        OnPropertyChanged(string.Empty);
    }

    internal void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed record StressLogEntry(string Time, string Text, bool IsBad);

public sealed record StressStat(string Label, string Value);

public sealed record DurationChoice(int Minutes, bool IsSelected)
{
    public string Text => $"{Minutes} MIN";
}

public sealed record PercentChoice(int Percent, bool IsSelected)
{
    public string Text => $"{Percent}%";
}
