using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Pennyworth.Monitoring.Hardware;

/// <summary>
/// Reads the hardware from the firmware tables, Windows' own registry entries and a few
/// device queries. No admin rights, no WMI, nothing leaves the PC.
/// </summary>
public sealed class WindowsSpecsReader : ISpecsReader
{
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string DisplayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public PcSpecs Read()
    {
        SmbiosInfo smbios = Safe(() => Smbios.Parse(HardwareNative.ReadSmbios()), Smbios.Parse([]));
        string? Bios(string name) => Safe(() => Registry.LocalMachine.OpenSubKey(BiosKey)?.GetValue(name) as string, null)?.Trim();

        var system = new SystemSpecs(
            Environment.MachineName,
            Environment.UserName,
            smbios.SystemManufacturer ?? Bios("SystemManufacturer"),
            smbios.SystemProduct ?? Bios("SystemProductName"));

        var board = new BoardSpecs(
            smbios.BoardManufacturer ?? Bios("BaseBoardManufacturer"),
            smbios.BoardProduct ?? Bios("BaseBoardProduct"),
            smbios.BiosVendor ?? Bios("BIOSVendor"),
            smbios.BiosVersion ?? Bios("BIOSVersion"),
            smbios.BiosDate ?? Bios("BIOSReleaseDate"));

        var memoryStatus = Safe(() => Native.ProcessReader.ReadMemory(0, null), default);
        var memory = new MemorySpecs(memoryStatus.Total, smbios.MemorySlots, smbios.Modules);

        return new PcSpecs(
            system,
            ReadOs(),
            ReadCpu(smbios.ProcessorSocket),
            memory,
            Safe(ReadGpus, []),
            Safe(() => HardwareNative.ReadDisplays().Select(d => new DisplaySpecs(d.Name, d.Adapter, d.Width, d.Height, d.Hz, d.Primary)).ToArray(), []),
            Safe(ReadDisks, []),
            board,
            Safe(ReadNetworks, []),
            Safe(HardwareNative.ReadBattery, null));
    }

    internal static OsSpecs ReadOs()
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(CurrentVersionKey);
        string name = key?.GetValue("ProductName") as string ?? "Windows";
        string build = key?.GetValue("CurrentBuildNumber") as string ?? Environment.OSVersion.Version.Build.ToString();
        if (int.TryParse(build, out int buildNumber) && buildNumber >= 22000)
        {
            // Windows 11 still says "Windows 10" in ProductName.
            name = name.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        }

        if (key?.GetValue("UBR") is int revision)
        {
            build = $"{build}.{revision}";
        }

        DateTimeOffset? installed = key?.GetValue("InstallDate") is int seconds && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds((uint)seconds).ToLocalTime()
            : null;
        return new OsSpecs(
            name,
            key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string,
            build,
            RuntimeInformation.OSArchitecture.ToString(),
            installed,
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    private static CpuSpecs ReadCpu(string? socket)
    {
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(ProcessorKey);
        string name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "?";
        var layout = HardwareNative.ReadProcessorLayout();
        return new CpuSpecs(
            System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " "),
            (key?.GetValue("VendorIdentifier") as string)?.Trim(),
            layout.Cores,
            layout.Threads,
            layout.Sockets,
            key?.GetValue("~MHz") is int mhz ? mhz : null,
            layout.L1,
            layout.L2,
            layout.L3,
            RuntimeInformation.ProcessArchitecture.ToString(),
            socket);
    }

    /// <summary>Display adapters from their driver entries (name, video memory, driver).</summary>
    private static IReadOnlyList<GpuSpecs> ReadGpus()
    {
        using RegistryKey? display = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
        HashSet<string> present = HardwareNative.ReadPresentAdapters();
        var gpus = new List<GpuSpecs>();
        foreach (string sub in display?.GetSubKeyNames() ?? [])
        {
            if (!sub.All(char.IsAsciiDigit))
            {
                continue;
            }

            try
            {
                using RegistryKey? key = display!.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string name
                    || name.Contains("Remote", StringComparison.OrdinalIgnoreCase)
                    || (present.Count > 0 && !present.Contains(name)))
                {
                    continue; // Remote sessions, and cards that are no longer installed.
                }

                long? memory = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long qword => qword,
                    _ => key.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int dword => (uint)dword,
                        byte[] bytes when bytes.Length >= 4 => BitConverter.ToUInt32(bytes, 0),
                        _ => null,
                    },
                };
                if (gpus.All(g => g.Name != name))
                {
                    gpus.Add(new GpuSpecs(name, memory > 0 ? memory : null, key.GetValue("DriverVersion") as string, key.GetValue("DriverDate") as string));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // Some adapter keys are private to the system; skip them.
            }
        }

        return gpus;
    }

    private static IReadOnlyList<DiskSpecs> ReadDisks()
    {
        var letters = DriveInfo.GetDrives()
            .Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable)
            .Select(d => (Letter: d.Name.TrimEnd('\\'), Disk: Safe(() => HardwareNative.DiskNumberOf(d.Name.TrimEnd('\\')), null)))
            .ToArray();
        return HardwareNative.ReadDisks()
            .Select(d => new DiskSpecs(
                d.Number,
                d.Model,
                d.Size,
                BusName(d.Bus),
                d.IsSsd,
                letters.Where(l => l.Disk == d.Number).Select(l => l.Letter).ToArray()))
            .ToArray();
    }

    private static string BusName(int bus) => bus switch
    {
        1 => "SCSI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        14 or 15 => "Virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        _ => "—",
    };

    /// <summary>
    /// Connected adapters with an IPv4 address. Windows also lists filter drivers, WAN miniports
    /// and loopback adapters as "up"; those aren't network cards and are left out.
    /// </summary>
    private static IReadOnlyList<NetworkSpecs> ReadNetworks() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            && !IsPseudoAdapter(n.Description))
        .Select(n => (Adapter: n, IPv4: n.GetIPProperties().UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString()))
        .Where(n => n.IPv4 is not null)
        .Select(n => new NetworkSpecs(
            n.Adapter.Name,
            n.Adapter.Description,
            n.Adapter.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => "Ethernet",
                _ => "VPN",
            },
            n.Adapter.Speed,
            Mac(n.Adapter.GetPhysicalAddress()),
            n.IPv4))
        .ToArray();

    private static bool IsPseudoAdapter(string description) =>
        description.Contains("Miniport", StringComparison.OrdinalIgnoreCase)
        || description.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
        || description.Contains("Filter", StringComparison.OrdinalIgnoreCase)
        || description.Contains("Scheduler", StringComparison.OrdinalIgnoreCase)
        || description.Contains("Network Monitor", StringComparison.OrdinalIgnoreCase);

    private static string? Mac(PhysicalAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? null : string.Join(':', bytes.Select(b => b.ToString("X2")));
    }

    /// <summary>One part failing (a driver, a locked key) must not hide the rest.</summary>
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or NetworkInformationException or InvalidOperationException or ExternalException or ArgumentException)
        {
            return fallback;
        }
    }
}
