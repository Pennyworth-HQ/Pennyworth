namespace Pennyworth.Monitoring.Hardware;

/// <summary>Everything the Specs screen shows, read once. Unknown values are null.</summary>
public sealed record PcSpecs(
    SystemSpecs System,
    OsSpecs Os,
    CpuSpecs Cpu,
    MemorySpecs Memory,
    IReadOnlyList<GpuSpecs> Gpus,
    IReadOnlyList<DisplaySpecs> Displays,
    IReadOnlyList<DiskSpecs> Disks,
    BoardSpecs Board,
    IReadOnlyList<NetworkSpecs> Networks,
    BatterySpecs? Battery);

public sealed record SystemSpecs(string ComputerName, string UserName, string? Manufacturer, string? Model);

/// <param name="Name">"Windows 11 Pro".</param>
/// <param name="Version">"24H2".</param>
/// <param name="Build">"26100.2033".</param>
public sealed record OsSpecs(string Name, string? Version, string Build, string Architecture, DateTimeOffset? InstalledOn, TimeSpan Uptime);

/// <param name="L1">Total level-1 cache of all cores, in bytes (0 when unknown); same for L2 and L3.</param>
public sealed record CpuSpecs(
    string Name, string? Vendor, int Cores, int Threads, int Sockets, int? BaseMhz,
    long L1, long L2, long L3, string Architecture, string? Socket);

/// <param name="Slots">Memory slots on the motherboard, when the firmware says.</param>
public sealed record MemorySpecs(long Total, int? Slots, IReadOnlyList<MemoryModule> Modules);

/// <param name="Type">"DDR4", "DDR5"...</param>
/// <param name="SpeedMts">Rated speed in MT/s.</param>
/// <param name="ConfiguredMts">Speed it actually runs at, in MT/s.</param>
public sealed record MemoryModule(
    string Slot, long Size, string? Type, int? SpeedMts, int? ConfiguredMts, string? Manufacturer, string? PartNumber, string? FormFactor);

public sealed record GpuSpecs(string Name, long? Memory, string? DriverVersion, string? DriverDate);

public sealed record DisplaySpecs(string Name, string Adapter, int Width, int Height, int RefreshHz, bool IsPrimary);

/// <param name="Bus">"NVMe", "SATA", "USB"...</param>
/// <param name="IsSsd">Null when Windows can't tell.</param>
/// <param name="Letters">Drive letters on this disk ("C:", "D:").</param>
public sealed record DiskSpecs(int Number, string Model, long Size, string Bus, bool? IsSsd, IReadOnlyList<string> Letters);

public sealed record BoardSpecs(string? Manufacturer, string? Product, string? BiosVendor, string? BiosVersion, string? BiosDate);

/// <param name="Kind">"Wi-Fi", "Ethernet"...</param>
public sealed record NetworkSpecs(string Name, string Description, string Kind, long SpeedBps, string? Mac, string? IPv4);

public sealed record BatterySpecs(int? Percent, bool IsCharging, bool OnAcPower);

public interface ISpecsReader
{
    /// <summary>Reads the hardware. Takes a moment (disks, firmware tables); call off the UI thread.</summary>
    PcSpecs Read();
}
