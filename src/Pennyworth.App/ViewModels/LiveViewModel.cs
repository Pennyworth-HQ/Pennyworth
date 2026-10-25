using System.Globalization;
using Pennyworth.Core.Formatting;
using Pennyworth.Monitoring;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Pennyworth.App.ViewModels;

/// <summary>Live memory, CPU and GPU numbers, updated once per second.</summary>
public sealed partial class LiveViewModel : ObservableObject
{
    public const int CpuHistoryLength = 60;
    public const int GpuHistoryLength = 30;
    private const double Gib = 1024d * 1024 * 1024;

    private readonly Queue<double> _cpu = new(Enumerable.Repeat(0d, CpuHistoryLength));
    private readonly Queue<double> _gpu = new(Enumerable.Repeat(0d, GpuHistoryLength));

    [ObservableProperty]
    public partial MonitorSample? Sample { get; private set; }

    [ObservableProperty]
    public partial string Bits { get; private set; } = Ascii.RandomBits(64);

    public MemoryStatus Memory => Sample?.Memory ?? default;

    public IReadOnlyList<AppGroup> Apps => Sample?.Apps ?? [];

    public double Cpu => Sample?.CpuPercent ?? 0;

    public double? Gpu => Sample?.GpuPercent;

    /// <summary>The last 60 CPU readings, oldest first.</summary>
    public IReadOnlyList<double> CpuHistory => [.. _cpu];

    /// <summary>The last 30 GPU readings, oldest first.</summary>
    public IReadOnlyList<double> GpuHistory => [.. _gpu];

    public bool HasSample => Sample is not null;

    /// <summary>"11.2" (GB in use, one decimal).</summary>
    public string MemoryUsedText => (Memory.Used / Gib).ToString("0.0", CultureInfo.CurrentCulture);

    /// <summary>"16 GB": physical memory, rounded the way it is sold.</summary>
    public string MemoryTotalText => $"{Math.Round(Memory.Total / Gib):0} GB";

    public string CpuText => $"{Math.Round(Cpu):0}%";

    public string GpuText => Gpu is { } gpu ? $"{Math.Round(gpu):0}%" : "—";

    public double MemoryUsedFraction => Memory.Total == 0 ? 0 : (double)Memory.Used / Memory.Total;

    /// <summary>In use, compressed and cached, drawn with the Games/Apps/Your files patterns as in the design.</summary>
    public IReadOnlyList<BarSegment> MemorySegments => Memory.Total == 0 ? [] :
    [
        new("Games", (double)Memory.InUse / Memory.Total),
        new("Apps", (double)Memory.Compressed / Memory.Total),
        new("YourFiles", (double)Memory.Cached / Memory.Total),
    ];

    public IReadOnlyList<BarSegment> InUseSwatch { get; } = [new("Games", 1)];

    public IReadOnlyList<BarSegment> CompressedSwatch { get; } = [new("Apps", 1)];

    public IReadOnlyList<BarSegment> CachedSwatch { get; } = [new("YourFiles", 1)];

    public string InUseText => ByteSize.Format(Memory.InUse);

    public string CompressedText => ByteSize.Format(Memory.Compressed);

    public string CachedText => ByteSize.Format(Memory.Cached);

    public string FreeText => ByteSize.Format(Memory.Free);

    public void Apply(MonitorSample sample)
    {
        Push(_cpu, sample.CpuPercent, CpuHistoryLength);
        Push(_gpu, sample.GpuPercent ?? 0, GpuHistoryLength);
        Sample = sample;
        Bits = Ascii.RandomBits(64);
        OnPropertyChanged(string.Empty);
    }

    /// <summary>Only the flickering bits change (no monitor in tests or before the first sample).</summary>
    public void Flicker() => Bits = Ascii.RandomBits(64);

    private static void Push(Queue<double> history, double value, int length)
    {
        history.Enqueue(value);
        while (history.Count > length)
        {
            history.Dequeue();
        }
    }
}
