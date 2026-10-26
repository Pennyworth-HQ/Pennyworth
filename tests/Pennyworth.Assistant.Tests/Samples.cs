using System.Globalization;
using Pennyworth.Cleanup;
using Pennyworth.Core.Storage;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Assistant.Tests;

internal static class Samples
{
    public const long Gb = 1024L * 1024 * 1024;

    public static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    public static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static PcSpecs Specs(BatterySpecs? battery = null, bool? systemSsd = false, int? slots = 2) => new(
        new SystemSpecs("DESKTOP-ANA", "ana", "PICHAU", "PCH-B550M"),
        new OsSpecs("Windows 11 Pro", "25H2", "26200.9711", "X64", null, TimeSpan.FromHours(200)),
        new CpuSpecs("AMD Ryzen 5 5600 6-Core Processor", "AuthenticAMD", 6, 12, 1, 3493, 0, 0, 0, "X64", "AM4"),
        new MemorySpecs(16 * Gb, slots, [new MemoryModule("DIMM 0", 16 * Gb, "DDR4", 3200, 3200, null, null, "DIMM")]),
        [new GpuSpecs("NVIDIA GeForce GTX 1650", 4 * Gb, null, null), new GpuSpecs("Microsoft Basic Display Adapter", null, null, null)],
        [new DisplaySpecs("Monitor", "GTX", 1920, 1080, 75, true)],
        [
            new DiskSpecs(0, "ST1500DL003", 1397 * Gb, "SATA", systemSsd, ["C:"]),
            new DiskSpecs(1, "KINGSTON SA400", 223 * Gb, "SATA", true, []),
        ],
        new BoardSpecs(null, null, null, null, null),
        [],
        battery);

    public static AppGroup App(string key, long memory, double cpu = 0, bool canClose = true, string? title = null) => new(
        key,
        title is null ? null : new ProcessExplanation(key, [key], new LocalizedText(title, title + " (pt)"), new LocalizedText("", ""), null, !canClose, canClose),
        key == "chrome" ? "Google Chrome" : null,
        null,
        null,
        memory,
        cpu,
        []);

    public static MonitorSample Sample(double ramPercent, double cpu = 20, params AppGroup[] apps)
    {
        long total = 16 * Gb;
        long used = (long)(total * ramPercent / 100);
        return new MonitorSample(new MemoryStatus(total, total - used, 0, 0), cpu, 22, apps);
    }

    public static CleanupRule Rule(string id, SafetyLevel safety, CleanupKind kind = CleanupKind.FolderContents) =>
        new(id, safety, kind, [], false, new LocalizedText(id + " files", id + " arquivos"), new LocalizedText("desc", "desc"), new LocalizedText("conseq", "conseq"));

    public static PcSnapshot Snapshot(
        double ramPercent = 50,
        double cpu = 20,
        double diskUsedPercent = 50,
        PcSpecs? specs = null,
        IReadOnlyList<CleanupEstimate>? cleanup = null,
        params AppGroup[] apps)
    {
        long total = 953 * Gb;
        long free = (long)(total * (100 - diskUsedPercent) / 100);
        return new PcSnapshot(
            specs ?? Specs(),
            [.. Enumerable.Range(0, 5).Select(_ => Sample(ramPercent, cpu, apps))],
            [new DriveFact(@"C:\", "", total, free, true), new DriveFact(@"D:\", "Data", 1000 * Gb, 100 * Gb, false)],
            new Dictionary<StorageCategory, long> { [StorageCategory.Games] = 412 * Gb, [StorageCategory.Apps] = 168 * Gb, [StorageCategory.Other] = 0 },
            cleanup ?? [new CleanupEstimate(Rule("temp", SafetyLevel.Safe), 18 * Gb, 67_808, true), new CleanupEstimate(Rule("recycle-bin", SafetyLevel.UsuallySafe, CleanupKind.RecycleBin), 2 * Gb, 8, true)],
            false);
    }

    public static Fact Get(this IReadOnlyList<Fact> facts, string id) => facts.Single(f => f.Id == id);
}
