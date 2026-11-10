using System.Diagnostics;
using System.Globalization;
using Pennyworth.App.Localization;
using Pennyworth.App.Resources;
using Pennyworth.Assistant;
using Pennyworth.Monitoring.History;
using Pennyworth.Monitoring.Logs;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

public enum HistoryRange
{
    Day,
    Week,
}

public enum LogState
{
    NotRead,
    Reading,
    Read,
    Failed,
}

/// <summary>
/// Usage over the last 24 hours or 7 days, the spikes in it, and the system log over the same
/// range, which the person can read here and hand to the assistant.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    /// <summary>Bars per chart: 5-minute buckets over a day, 30-minute buckets over a week.</summary>
    private const int DayBuckets = 288;
    private const int WeekBuckets = 336;
    private const int MaxSpikeRows = 12;

    private readonly TelemetryStore? _store;
    private readonly ISystemLogReader? _logs;
    private readonly Func<DateTimeOffset> _clock;
    private readonly List<TelemetryPoint> _points = [];
    private Task? _loading;
    private List<TelemetryPoint> _inRange = [];
    private List<Spike> _spikes = [];
    private List<LogGroup> _logGroups = [];
    private int _logEntryCount;

    public HistoryViewModel(TelemetryStore? store, ISystemLogReader? logs, Func<DateTimeOffset> clock)
    {
        _store = store;
        _logs = logs;
        _clock = clock;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDay), nameof(IsWeek))]
    public partial HistoryRange Range { get; private set; }

    public bool IsDay => Range == HistoryRange.Day;

    public bool IsWeek => Range == HistoryRange.Week;

    public TimeSpan RangeSpan => Range == HistoryRange.Day ? TimeSpan.FromHours(24) : TimeSpan.FromDays(TelemetryStore.KeepDays);

    public IReadOnlyList<MetricChartViewModel> Charts { get; private set; } = [];

    public IReadOnlyList<SpikeRowViewModel> Spikes { get; private set; } = [];

    public bool HasData => _inRange.Count > 0;

    public bool HasSpikes => Spikes.Count > 0;

    public bool NoSpikes => HasData && !HasSpikes;

    /// <summary>"Recorded 6 h 12 min of the last 24 hours…".</summary>
    public string CoverageText => HasData
        ? string.Format(CultureInfo.CurrentCulture, Strings.HistoryRecorded, Duration(Recorded), (int)RangeSpan.TotalHours)
        : Strings.HistoryEmpty;

    public TimeSpan Recorded => TimeSpan.FromMinutes(_inRange.Count);

    /// <summary>"Ask about this": set by the shell when the assistant is available.</summary>
    public Action<AssistantTopic, string>? AskAbout { get; set; }

    public bool CanAsk => AskAbout is not null;

    public bool CanAskAboutSpikes => CanAsk && HasData;

    // System log

    public bool HasLogReader => _logs is not null;

    public string LogSource => _logs?.SourceName ?? "";

    public string LogsSub => string.Format(CultureInfo.CurrentCulture, Strings.LogsSub, LogSource);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadingLogs), nameof(LogsRead), nameof(LogStatus), nameof(HasLogStatus), nameof(CanAskAboutLogs))]
    [NotifyCanExecuteChangedFor(nameof(ReadLogsCommand))]
    public partial LogState LogState { get; private set; }

    public bool IsReadingLogs => LogState == LogState.Reading;

    public bool LogsRead => LogState == LogState.Read;

    public IReadOnlyList<LogRowViewModel> LogRows { get; private set; } = [];

    public string LogStatus => LogState switch
    {
        LogState.Reading => Strings.ReadingLogs,
        LogState.Failed => Strings.LogsFailed,
        LogState.Read when _logGroups.Count == 0 => Strings.LogsNone,
        LogState.Read => string.Format(CultureInfo.CurrentCulture, Strings.LogsCount, _logEntryCount, _logGroups.Count),
        _ => "",
    };

    public bool HasLogStatus => LogStatus.Length > 0;

    public bool CanAskAboutLogs => CanAsk && LogsRead;

    /// <summary>The reading started by <see cref="ReadLogsCommand"/>; lets tests await it.</summary>
    public Task LogReading { get; private set; } = Task.CompletedTask;

    /// <summary>Reads the last week from disk once; later minutes arrive through <see cref="Add"/>.</summary>
    public Task EnsureLoaded() => _loading ??= LoadAsync();

    private async Task LoadAsync()
    {
        if (_store is { } store)
        {
            DateTimeOffset since = _clock() - TimeSpan.FromDays(TelemetryStore.KeepDays);
            try
            {
                List<TelemetryPoint> loaded = await Task.Run(() => store.Load(since));
                // Minutes recorded while loading are already in the list; the file has them too.
                DateTimeOffset? firstLive = _points.Count == 0 ? null : _points[0].At;
                _points.InsertRange(0, [.. loaded.Where(p => firstLive is null || p.At < firstLive)]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // No history on disk is the same as a new one; recording carries on.
                Debug.WriteLine(ex);
            }
        }

        Rebuild();
    }

    /// <summary>A minute just finished recording (called by the shell, once per minute).</summary>
    public void Add(TelemetryPoint point)
    {
        _points.Add(point);
        DateTimeOffset oldest = _clock() - TimeSpan.FromDays(TelemetryStore.KeepDays);
        int stale = _points.FindIndex(p => p.At >= oldest);
        if (stale > 0)
        {
            _points.RemoveRange(0, stale);
        }

        if (_loading is { IsCompleted: true })
        {
            Rebuild();
        }
    }

    /// <summary>What the assistant gets for a History question.</summary>
    public HistoryReport Report() => new(
        RangeSpan,
        Recorded,
        [.. Enum.GetValues<Metric>().Select(m => SpikeFinder.Summarize(_inRange, m)).OfType<MetricSummary>()],
        _spikes,
        LogsRead ? _logGroups : null,
        _logs?.SourceName);

    public void RefreshTexts()
    {
        Rebuild();
        LogRows = [.. _logGroups.Select(g => new LogRowViewModel(g))];
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void ShowDay() => SetRange(HistoryRange.Day);

    [RelayCommand]
    private void ShowWeek() => SetRange(HistoryRange.Week);

    private void SetRange(HistoryRange range)
    {
        if (Range == range)
        {
            return;
        }

        Range = range;

        // The log was read for the other range; reading it again keeps the two in step.
        if (LogState != LogState.Reading)
        {
            _logGroups = [];
            LogRows = [];
            LogState = LogState.NotRead;
            OnPropertyChanged(nameof(LogRows));
        }

        Rebuild();
    }

    [RelayCommand(CanExecute = nameof(CanReadLogs))]
    private void ReadLogs() => LogReading = ReadLogsAsync();

    private bool CanReadLogs() => _logs is not null && LogState != LogState.Reading;

    private async Task ReadLogsAsync()
    {
        if (_logs is not { } reader)
        {
            return;
        }

        LogState = LogState.Reading;
        try
        {
            DateTimeOffset since = _clock() - RangeSpan;
            IReadOnlyList<SystemLogEntry> entries = await Task.Run(() => reader.ReadAsync(since, CancellationToken.None));
            _logEntryCount = entries.Count;
            _logGroups = LogDigest.Group(entries);
            LogRows = [.. _logGroups.Select(g => new LogRowViewModel(g))];
            OnPropertyChanged(nameof(LogRows));
            LogState = LogState.Read;
        }
        catch (Exception ex)
        {
            // wevtutil or journalctl missing, refused or killed: say so, keep the screen usable.
            Debug.WriteLine(ex);
            LogState = LogState.Failed;
        }
    }

    [RelayCommand]
    private void AskAboutSpikes() =>
        AskAbout?.Invoke(new AssistantTopic(TopicKind.History), IsDay ? Strings.AskSpikesDay : Strings.AskSpikesWeek);

    [RelayCommand]
    private void AskAboutLogs() =>
        AskAbout?.Invoke(new AssistantTopic(TopicKind.History, AssistantTopic.LogsKey), IsDay ? Strings.AskLogsDay : Strings.AskLogsWeek);

    private void Rebuild()
    {
        DateTimeOffset now = _clock();
        DateTimeOffset since = now - RangeSpan;
        _inRange = [.. _points.Where(p => p.At >= since)];
        _spikes = SpikeFinder.Find(_inRange);

        int buckets = IsDay ? DayBuckets : WeekBuckets;
        TimeSpan bucket = RangeSpan / buckets;
        DateTimeOffset start = now - RangeSpan;
        var charts = new List<MetricChartViewModel>();
        foreach (Metric metric in Enum.GetValues<Metric>())
        {
            if (SpikeFinder.Summarize(_inRange, metric) is not { } summary)
            {
                // No GPU or disk counter on this PC: no empty chart either. CPU and memory always show.
                if (metric is Metric.Disk or Metric.Gpu)
                {
                    continue;
                }

                summary = new MetricSummary(metric, 0, 0, 0, 0, 0);
            }

            charts.Add(MetricChartViewModel.Build(metric, summary, _inRange, start, bucket, buckets, IsDay));
        }

        Charts = charts;
        Spikes = [.. _spikes.Take(MaxSpikeRows).Select(s => new SpikeRowViewModel(s))];
        OnPropertyChanged(nameof(Charts));
        OnPropertyChanged(nameof(Spikes));
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(HasSpikes));
        OnPropertyChanged(nameof(NoSpikes));
        OnPropertyChanged(nameof(CoverageText));
        OnPropertyChanged(nameof(CanAskAboutSpikes));
    }

    internal static string MetricName(Metric metric) => metric switch
    {
        Metric.Cpu => "CPU",
        Metric.Memory => Strings.Memory,
        Metric.Disk => Strings.MetricDisk,
        _ => "GPU",
    };

    /// <summary>"45 min", "6 h 12 min", "3 days".</summary>
    internal static string Duration(TimeSpan span) => span.TotalMinutes < 60
        ? $"{Math.Max((int)Math.Round(span.TotalMinutes), 1)} min"
        : span.TotalHours < 48
            ? span.Minutes == 0 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalHours} h {span.Minutes} min"
            : string.Format(CultureInfo.CurrentCulture, Strings.Days, (int)Math.Round(span.TotalDays));

    internal static string Percent(double value) => value.ToString("0", CultureInfo.CurrentCulture) + "%";

    /// <summary>"Tue 27/10 14:32" in the person's language and time zone.</summary>
    internal static string When(DateTimeOffset at) =>
        at.ToLocalTime().ToString("ddd dd/MM HH:mm", Localizer.Instance.Culture);
}

/// <summary>One chart card: the bars, the line, the numbers under the title, the time axis.</summary>
public sealed record MetricChartViewModel(
    Metric Metric,
    string Title,
    string SummaryText,
    double Threshold,
    IReadOnlyList<double> Peaks,
    IReadOnlyList<double> Averages,
    IReadOnlyList<string> Labels,
    string StartLabel,
    string MiddleLabel)
{
    public string ThresholdText => $"{Threshold:0}%";

    internal static MetricChartViewModel Build(Metric metric, MetricSummary summary, List<TelemetryPoint> points, DateTimeOffset start, TimeSpan bucket, int buckets, bool isDay)
    {
        var peaks = new double[buckets];
        var sums = new double[buckets];
        var counts = new int[buckets];
        Array.Fill(peaks, double.NaN);
        foreach (TelemetryPoint point in points)
        {
            if (point.Get(metric) is not { } reading)
            {
                continue;
            }

            int i = (int)((point.At - start) / bucket);
            if (i < 0 || i >= buckets)
            {
                continue;
            }

            peaks[i] = double.IsNaN(peaks[i]) ? reading.Peak : Math.Max(peaks[i], reading.Peak);
            sums[i] += reading.Average;
            counts[i]++;
        }

        var averages = new double[buckets];
        var labels = new string[buckets];
        CultureInfo culture = Localizer.Instance.Culture;
        for (int i = 0; i < buckets; i++)
        {
            averages[i] = counts[i] == 0 ? double.NaN : sums[i] / counts[i];
            string time = (start + (bucket * i)).ToLocalTime().ToString(isDay ? "HH:mm" : "ddd HH:mm", culture);
            labels[i] = counts[i] == 0
                ? $"{time} · {Strings.NotRecorded}"
                : $"{time} · {Strings.Peak} {HistoryViewModel.Percent(peaks[i])} · {Strings.Average} {HistoryViewModel.Percent(averages[i])}";
        }

        string text = summary.Minutes == 0
            ? Strings.NotRecorded
            : $"{Strings.Average} {HistoryViewModel.Percent(summary.Average)} · {Strings.Peak} {HistoryViewModel.Percent(summary.Peak)} · {string.Format(culture, Strings.MinutesAtLimit, summary.MinutesAbove)}";
        DateTimeOffset middle = start + (bucket * (buckets / 2));
        string format = isDay ? "HH:mm" : "ddd dd/MM";
        return new MetricChartViewModel(
            metric,
            HistoryViewModel.MetricName(metric),
            text,
            SpikeFinder.ThresholdOf(metric),
            peaks,
            averages,
            labels,
            start.ToLocalTime().ToString(format, culture),
            middle.ToLocalTime().ToString(format, culture));
    }
}

/// <summary>"Tue 27/10 14:32 · 3 min — Disk 100% (usual 5%) · MsMpEng".</summary>
public sealed record SpikeRowViewModel(Spike Spike)
{
    public string When => HistoryViewModel.When(Spike.Start);

    public string DurationText => HistoryViewModel.Duration(Spike.Duration);

    public string MetricText => HistoryViewModel.MetricName(Spike.Metric);

    public string PeakText => HistoryViewModel.Percent(Spike.Peak);

    public string UsualText => string.Format(CultureInfo.CurrentCulture, Strings.SpikeUsual, HistoryViewModel.Percent(Spike.Baseline));

    public string AppText => Spike.TopApp is { } app ? string.Format(CultureInfo.CurrentCulture, Spike.Metric == Metric.Memory ? Strings.SpikeAppMemory : Strings.SpikeAppCpu, app) : "";

    public bool HasApp => Spike.TopApp is not null;
}

/// <summary>One grouped problem from the system log.</summary>
public sealed record LogRowViewModel(LogGroup Group)
{
    public string SeverityText => Group.Severity switch
    {
        LogSeverity.Critical => Strings.SeverityCritical,
        LogSeverity.Error => Strings.SeverityError,
        _ => Strings.SeverityWarning,
    };

    public bool IsSerious => Group.Severity >= LogSeverity.Error;

    /// <summary>"disk 153 · System".</summary>
    public string SourceText => Group.EventId is null ? $"{Group.Source} · {Group.Log}" : $"{Group.Source} {Group.EventId} · {Group.Log}";

    /// <summary>"12× · last Tue 27/10 14:33".</summary>
    public string TimesText => string.Format(CultureInfo.CurrentCulture, Strings.LogTimes, HistoryViewModel.When(Group.Last), Group.Count);

    public string Message => Group.Message;
}
