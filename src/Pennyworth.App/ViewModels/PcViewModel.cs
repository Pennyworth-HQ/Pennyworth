using System.Globalization;
using Pennyworth.App.Resources;
using Pennyworth.Assistant;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Storage;
using Pennyworth.Monitoring.Hardware;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>The six parts of the 3D PC, in the order of the chips under it.</summary>
public enum PcPart
{
    Drive,
    Memory,
    Processor,
    Graphics,
    Power,
    Trash,
}

/// <summary>
/// 3D mode: an ASCII PC you can rotate, and a panel with live status for the selected part.
/// </summary>
public sealed partial class PcViewModel(MainWindowViewModel shell) : ObservableObject
{
    public const double MinZoom = 0.5;
    public const double MaxZoom = 2.6;

    private static readonly Dictionary<StorageCategory, char> CategoryChars = new()
    {
        [StorageCategory.Games] = '#',
        [StorageCategory.Apps] = '%',
        [StorageCategory.YourFiles] = '+',
        [StorageCategory.Windows] = '=',
        [StorageCategory.HiddenSystem] = '*',
        [StorageCategory.Other] = ':',
    };

    public LiveViewModel Live => shell.Live;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chips), nameof(PartIndexText), nameof(PartName), nameof(PartHint),
        nameof(IsDrive), nameof(IsMemory), nameof(IsProcessor), nameof(IsGraphics), nameof(IsPower), nameof(IsTrash))]
    public partial PcPart Part { get; set; }

    [ObservableProperty]
    public partial bool ShowHint { get; private set; } = true;

    /// <summary>Target zoom; the 3D view eases toward it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    public partial double Zoom { get; set; } = 1;

    /// <summary>0 to 1: the drive drawing fills up when the drive part is selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriveArt))]
    public partial double DriveArtProgress { get; private set; }

    public bool IsDrive => Part == PcPart.Drive;

    public bool IsMemory => Part == PcPart.Memory;

    public bool IsProcessor => Part == PcPart.Processor;

    public bool IsGraphics => Part == PcPart.Graphics;

    public bool IsPower => Part == PcPart.Power;

    public bool IsTrash => Part == PcPart.Trash;

    public string ZoomText => $"{Math.Round(Zoom * 100):0}%";

    /// <summary>Upper-case labels floating above the parts, in <see cref="PcPart"/> order.</summary>
    public IReadOnlyList<string> PartLabels => Enum.GetValues<PcPart>().Select(Label).ToArray();

    public IReadOnlyList<PartChipViewModel> Chips => Enum.GetValues<PcPart>()
        .Select(part => new PartChipViewModel(part, $"0{(int)part + 1}", Label(part), part == Part))
        .ToArray();

    public string PartIndexText => $"0{(int)Part + 1} / 06 · {Label(Part)}";

    public string PartName => Strings.ResourceManager.GetString($"PartName_{Part}", Strings.Culture) ?? "";

    public string PartHint => Strings.ResourceManager.GetString($"PartHint_{Part}", Strings.Culture) ?? "";

    public string MachineText => string.Format(Strings.Machine, shell.SystemDrive?.Letter ?? "C:", Live.HasSample ? Live.MemoryTotalText : "—");

    /// <summary>
    /// The disks inside the PC that hold a partition, the system disk first, once the specs have been
    /// read; external USB and SD ones aren't part of the case.
    /// </summary>
    public IReadOnlyList<PcDisk> Disks => DisksInPc.Select(disk => new PcDisk(string.Join(" ", disk.Letters), disk.IsSsd)).ToArray();

    private DiskSpecs[] DisksInPc => shell.Specs.Specs?.Disks
        .Where(disk => disk.Bus is not ("USB" or "SD") && disk.Letters.Count > 0)
        .OrderByDescending(disk => disk.Letters.Contains(shell.SystemDrive?.Letter ?? "", StringComparer.OrdinalIgnoreCase))
        .ThenBy(disk => disk.Number)
        .ToArray() ?? [];

    // Drive: the disk picked in the 3D view, or just the Windows drive while the disks aren't known.

    /// <summary>Which of <see cref="Disks"/> the drive panel describes.</summary>
    [ObservableProperty]
    public partial int SelectedDisk { get; set; }

    private DiskSpecs? CurrentDisk => DisksInPc.ElementAtOrDefault(SelectedDisk);

    /// <summary>The partitions on the selected disk.</summary>
    private DriveViewModel[] DiskDrives => CurrentDisk is { } disk
        ? shell.Drives.Where(d => disk.Letters.Contains(d.Letter, StringComparer.OrdinalIgnoreCase)).ToArray()
        : shell.SystemDrive is { } system ? [system] : [];

    /// <summary>The partition whose scan the panel shows: Windows' when it's on this disk, else the first.</summary>
    private DriveViewModel? MainDrive => DiskDrives.FirstOrDefault(d => d == shell.SystemDrive) ?? DiskDrives.FirstOrDefault();

    private StorageSummaryViewModel? MainSummary => MainDrive is { } drive ? shell.SummaryOf(drive) : null;

    public bool HasDiskTitle => CurrentDisk is not null;

    /// <summary>"SSD · KINGSTON SA400 · 224 GB".</summary>
    public string DiskTitle => CurrentDisk is { } disk
        ? string.Join(" · ", new[] { disk.IsSsd switch { true => "SSD", false => "HDD", null => null }, disk.Model.Trim(), ByteSize.Format(disk.Size) }
            .Where(s => !string.IsNullOrEmpty(s)))
        : "";

    public string DriveUsedText => DiskDrives.Length > 0 ? ByteSize.Format(DiskDrives.Sum(d => d.Summary.UsedBytes)) : "—";

    public string DriveFreeText => DiskDrives.Length > 0
        ? $"{string.Format(Strings.DriveFree, ByteSize.Format(DiskDrives.Sum(d => d.Summary.FreeBytes)))} · {ByteSize.Format(DiskDrives.Sum(d => d.Summary.TotalBytes))}"
        : "";

    /// <summary>Each partition with its use, when the disk has more than one.</summary>
    public IReadOnlyList<LegendLine> DiskPartitions => DiskDrives.Length > 1
        ? DiskDrives.Select(d => new LegendLine(d.Letter, d.Label, d.UsedText)).ToArray()
        : [];

    public bool HasPartitions => DiskPartitions.Count > 0;

    /// <summary>What fills the main partition once it's been scanned; until then, how full the disk is.</summary>
    public string DriveArt
    {
        get
        {
            if (MainSummary is { } summary)
            {
                long total = Math.Max(summary.Drive.TotalBytes, 1);
                var parts = summary.Categories
                    .Select(row => (CategoryChars[row.Category!.Value], (double)row.Bytes / total))
                    .Append(('-', (double)summary.Unaccounted.Bytes / total));
                return Ascii.DriveArt(parts, DriveArtProgress, (MainDrive?.Letter ?? "C:") + "\\");
            }

            long size = Math.Max(DiskDrives.Sum(d => d.Summary.TotalBytes), 1);
            string letters = DiskDrives.Length > 0 ? string.Join(" ", DiskDrives.Select(d => d.Letter + "\\")) : "C:\\";
            return Ascii.DriveArt([('#', (double)DiskDrives.Sum(d => d.Summary.UsedBytes) / size)], DriveArtProgress, letters);
        }
    }

    public IReadOnlyList<LegendLine> DriveLegend
    {
        get
        {
            if (MainSummary is not { } summary)
            {
                return DiskDrives.Length == 0 ? [] :
                [
                    new("#", Strings.InUse, DriveUsedText),
                    new(".", Strings.MemFree, ByteSize.Format(DiskDrives.Sum(d => d.Summary.FreeBytes))),
                ];
            }

            return summary.Categories
                .Select(row => new LegendLine(CategoryChars[row.Category!.Value].ToString(), row.Name, row.SizeText))
                .Append(new LegendLine("-", summary.Unaccounted.Name, summary.Unaccounted.SizeText))
                .Append(new LegendLine(".", Strings.MemFree, ByteSize.Format(summary.Drive.FreeBytes)))
                .ToArray();
        }
    }

    /// <summary>The five biggest places across all categories, with a 16-character bar.</summary>
    public IReadOnlyList<BarLine> TopFolders
    {
        get
        {
            var places = MainSummary?.Categories.SelectMany(row => row.Areas).OrderByDescending(a => a.Bytes).Take(5).ToArray() ?? [];
            long largest = Math.Max(places.FirstOrDefault()?.Bytes ?? 1, 1);
            return places.Select(place => new BarLine(place.Title, Ascii.Bar((double)place.Bytes / largest, 16), place.SizeText)).ToArray();
        }
    }

    public bool HasTopFolders => TopFolders.Count > 0;

    // Memory
    public string MemoryAscii
    {
        get
        {
            var memory = Live.Memory;
            double total = Math.Max(memory.Total, 1);
            return "[" + Ascii.StackedBar(
                [('#', memory.InUse / total), ('%', memory.Compressed / total), (':', memory.Cached / total)], 32) + "]";
        }
    }

    public IReadOnlyList<LegendLine> MemoryLegend =>
    [
        new("#", Strings.InUse, Live.InUseText),
        new("%", Strings.Compressed, Live.CompressedText),
        new(":", Strings.Cached, Live.CachedText),
        new(".", Strings.MemFree, Live.FreeText),
    ];

    public IReadOnlyList<LegendLine> TopMemory => shell.Memory.Apps
        .OrderByDescending(app => app.Memory).Take(4)
        .Select(app => new LegendLine("", app.Name, app.MemoryText)).ToArray();

    // Processor
    public string CpuSpark => Ascii.Spark(Live.CpuHistory.Skip(LiveViewModel.CpuHistoryLength - 30), 25);

    public IReadOnlyList<LegendLine> TopCpu => shell.Memory.Apps
        .OrderByDescending(app => app.Cpu).Take(4)
        .Select(app => new LegendLine("", app.Name, app.CpuText)).ToArray();

    // Graphics
    public string GpuSpark => Ascii.Spark(Live.GpuHistory, 30);

    public bool IsGpuAvailable => Live.Gpu is not null;

    public string ShaderNote => shell.Cleanup.ShaderBytes > 0
        ? string.Format(Strings.ShaderNote, ByteSize.Format(shell.Cleanup.ShaderBytes))
        : Strings.ShaderCleaned;

    // Power
    public long HibernationBytes => shell.SystemSummary?.Tree?.Root.GetChildren()
        .Where(n => string.Equals(n.Name, "hiberfil.sys", StringComparison.OrdinalIgnoreCase))
        .Select(n => n.SizeOnDisk)
        .FirstOrDefault() ?? 0;

    public bool HasHibernation => HibernationBytes > 0;

    public string HibernationText => ByteSize.Format(HibernationBytes);

    /// <summary>The hibernation file next to the size of RAM it can hold.</summary>
    public string HibernationBar => Ascii.Bar(Live.Memory.Total == 0 ? 0 : (double)HibernationBytes / Live.Memory.Total, 12);

    public string PowerDiagram =>
        "  +----------------+\n" +
        "  | > ON   > SLEEP |--+\n" +
        "  | > HIBERNATE    |  | RAM -> DISK\n" +
        "  +----------------+  v\n" +
        $"       hiberfil.sys  [{HibernationBar}]";

    // Trash
    public string CleanupTotalText => ByteSize.Format(shell.Cleanup.TotalBytes);

    public IReadOnlyList<CleanupItemViewModel> TrashItems => shell.Cleanup.Items;

    public void Refresh() => OnPropertyChanged(string.Empty);

    /// <summary>Only the live panels, so the part chips aren't rebuilt every second.</summary>
    public void RefreshLive()
    {
        foreach (string name in (string[])[nameof(MachineText), nameof(MemoryAscii), nameof(MemoryLegend), nameof(TopMemory),
            nameof(CpuSpark), nameof(TopCpu), nameof(GpuSpark), nameof(IsGpuAvailable), nameof(HibernationBar), nameof(PowerDiagram)])
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>Moves the drive drawing's fill animation on (1.6 s, easing out).</summary>
    public void AdvanceAnimation(TimeSpan sinceSelected)
    {
        double t = Math.Clamp(sinceSelected.TotalSeconds / 1.6, 0, 1);
        DriveArtProgress = 1 - Math.Pow(1 - t, 3);
    }

    partial void OnSelectedDiskChanged(int value)
    {
        DriveArtProgress = 0;
        foreach (string name in (string[])[nameof(HasDiskTitle), nameof(DiskTitle), nameof(DriveUsedText), nameof(DriveFreeText),
            nameof(DiskPartitions), nameof(HasPartitions), nameof(DriveArt), nameof(DriveLegend), nameof(TopFolders), nameof(HasTopFolders)])
        {
            OnPropertyChanged(name);
        }
    }

    partial void OnPartChanged(PcPart value)
    {
        ShowHint = false;
        DriveArtProgress = 0;
    }

    [RelayCommand]
    private void SelectPart(PcPart part) => Part = part;

    public bool CanAsk => shell.HasAssistant;

    [RelayCommand]
    private void AskAboutPart()
    {
        TopicKind topic = Part switch
        {
            PcPart.Memory => TopicKind.Memory,
            PcPart.Processor => TopicKind.Processor,
            PcPart.Graphics => TopicKind.Graphics,
            PcPart.Power => TopicKind.Power,
            _ => TopicKind.Storage,
        };
        shell.AskAbout(new AssistantTopic(topic), string.Format(CultureInfo.CurrentCulture, Strings.AskAboutSpec, PartName));
    }

    [RelayCommand]
    private void DismissHint() => ShowHint = false;

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Clamp(Zoom * 1.25, MinZoom, MaxZoom);

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Clamp(Zoom / 1.25, MinZoom, MaxZoom);

    [RelayCommand]
    private void ZoomReset() => Zoom = 1;

    [RelayCommand]
    private void OpenStorage()
    {
        if (MainDrive is { } drive)
        {
            shell.ShowDrive(drive);
        }
        else
        {
            shell.ShowStorageCategory(null);
        }
    }

    [RelayCommand]
    private void OpenHidden() => shell.ShowStorageCategory(StorageCategory.HiddenSystem);

    [RelayCommand]
    private void OpenMemory() => shell.GoMemoryCommand.Execute(null);

    [RelayCommand]
    private void OpenCleanup() => shell.GoCleanupCommand.Execute(null);

    [RelayCommand]
    private void OpenSpecs() => shell.GoSpecsCommand.Execute(null);

    private static string Label(PcPart part) =>
        (Strings.ResourceManager.GetString($"PartLabel_{part}", Strings.Culture) ?? part.ToString()).ToUpper(CultureInfo.CurrentCulture);
}

/// <summary>A disk inside the PC, as the 3D view draws it.</summary>
/// <param name="Name">Its label: the partitions on it, "C:" or "D: E:".</param>
/// <param name="IsSsd">Null when Windows can't tell.</param>
public sealed record PcDisk(string Name, bool? IsSsd);

public sealed record PartChipViewModel(PcPart Part, string Index, string Label, bool IsSelected);

/// <param name="Mark">A legend character ("#") or empty.</param>
public sealed record LegendLine(string Mark, string Name, string Value);

public sealed record BarLine(string Name, string Bar, string Value);
