using System.Runtime.InteropServices;

namespace Pennyworth.Monitoring.Hardware;

/// <summary>Windows calls for the Specs screen. None of them need admin rights.</summary>
internal static unsafe partial class HardwareNative
{
    private const uint Rsmb = 0x52534D42; // 'RSMB': the raw SMBIOS table.
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int RelationProcessorCore = 0;
    private const int RelationCache = 2;
    private const int RelationProcessorPackage = 3;
    private const int EnumCurrentSettings = -1;
    private const uint DisplayAttachedToDesktop = 0x1;
    private const uint DisplayPrimary = 0x4;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetSystemFirmwareTable(uint provider, uint tableId, byte* buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLogicalProcessorInformation(byte* buffer, ref uint length);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(nint device, uint code, void* input, int inputSize, void* output, int outputSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplayDevices(string? device, uint index, byte* displayDevice, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumDisplaySettings(string device, int mode, byte* devMode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    /// <summary>The raw SMBIOS table, or empty when the firmware has none.</summary>
    public static byte[] ReadSmbios()
    {
        uint size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size == 0)
        {
            return [];
        }

        byte[] buffer = new byte[size];
        fixed (byte* start = buffer)
        {
            return GetSystemFirmwareTable(Rsmb, 0, start, size) == size ? buffer : [];
        }
    }

    /// <summary>Cores, threads, sockets and total cache per level (SYSTEM_LOGICAL_PROCESSOR_INFORMATION, 32 bytes on 64-bit).</summary>
    public static (int Cores, int Threads, int Sockets, long L1, long L2, long L3) ReadProcessorLayout()
    {
        uint length = 0;
        GetLogicalProcessorInformation(null, ref length);
        if (length == 0)
        {
            return (Environment.ProcessorCount, Environment.ProcessorCount, 1, 0, 0, 0);
        }

        byte[] buffer = new byte[length];
        int cores = 0, threads = 0, sockets = 0;
        long l1 = 0, l2 = 0, l3 = 0;
        fixed (byte* start = buffer)
        {
            if (!GetLogicalProcessorInformation(start, ref length))
            {
                return (Environment.ProcessorCount, Environment.ProcessorCount, 1, 0, 0, 0);
            }

            const int entrySize = 32;
            for (int offset = 0; offset + entrySize <= length; offset += entrySize)
            {
                byte* entry = start + offset;
                ulong mask = *(ulong*)entry;
                int relationship = *(int*)(entry + 8);
                switch (relationship)
                {
                    case RelationProcessorCore:
                        cores++;
                        threads += System.Numerics.BitOperations.PopCount(mask);
                        break;
                    case RelationProcessorPackage:
                        sockets++;
                        break;
                    case RelationCache:
                        byte level = *(entry + 16);
                        uint size = *(uint*)(entry + 20);
                        if (level == 1)
                        {
                            l1 += size;
                        }
                        else if (level == 2)
                        {
                            l2 += size;
                        }
                        else if (level == 3)
                        {
                            l3 += size;
                        }

                        break;
                }
            }
        }

        return (cores, threads, Math.Max(sockets, 1), l1, l2, l3);
    }

    /// <summary>Physical disks \\.\PhysicalDrive0..31: model, size, bus and SSD/HDD.</summary>
    public static List<(int Number, string Model, long Size, int Bus, bool? IsSsd)> ReadDisks()
    {
        var disks = new List<(int, string, long, int, bool?)>();
        for (int number = 0; number < 32; number++)
        {
            // Zero access rights: enough for these queries, and no admin needed.
            nint disk = CreateFile($@"\\.\PhysicalDrive{number}", 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
            if (disk == -1)
            {
                continue;
            }

            try
            {
                var (model, bus) = QueryDevice(disk);
                disks.Add((number, model, QuerySize(disk), bus, QuerySeekPenalty(disk) is { } seek ? !seek : null));
            }
            finally
            {
                CloseHandle(disk);
            }
        }

        return disks;
    }

    /// <summary>Which physical disk a drive letter ("C:") lives on, or null.</summary>
    public static int? DiskNumberOf(string letter)
    {
        nint volume = CreateFile($@"\\.\{letter}", 0, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (volume == -1)
        {
            return null;
        }

        try
        {
            int* number = stackalloc int[3]; // STORAGE_DEVICE_NUMBER: type, number, partition.
            return DeviceIoControl(volume, IoctlStorageGetDeviceNumber, null, 0, number, 12, out _, 0) ? number[1] : null;
        }
        finally
        {
            CloseHandle(volume);
        }
    }

    private static (string Model, int Bus) QueryDevice(nint disk)
    {
        int* query = stackalloc int[3] { StorageDeviceProperty, 0, 0 };
        byte* output = stackalloc byte[1024];
        if (!DeviceIoControl(disk, IoctlStorageQueryProperty, query, 12, output, 1024, out int returned, 0) || returned < 32)
        {
            return ("?", 0);
        }

        // STORAGE_DEVICE_DESCRIPTOR: offsets of vendor and product strings, then the bus type.
        string vendor = AnsiAt(output, *(uint*)(output + 12), returned);
        string product = AnsiAt(output, *(uint*)(output + 16), returned);
        string model = product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) || vendor.Length == 0
            ? product
            : $"{vendor} {product}";
        return (model.Length == 0 ? "?" : model, *(int*)(output + 28));
    }

    private static bool? QuerySeekPenalty(nint disk)
    {
        int* query = stackalloc int[3] { StorageDeviceSeekPenaltyProperty, 0, 0 };
        byte* output = stackalloc byte[12];
        return DeviceIoControl(disk, IoctlStorageQueryProperty, query, 12, output, 12, out int returned, 0) && returned >= 9
            ? output[8] != 0
            : null;
    }

    private static long QuerySize(nint disk)
    {
        byte* output = stackalloc byte[256];
        return DeviceIoControl(disk, IoctlDiskGetDriveGeometryEx, null, 0, output, 256, out int returned, 0) && returned >= 32
            ? *(long*)(output + 24) // DISK_GEOMETRY (24 bytes), then DiskSize.
            : 0;
    }

    private static string AnsiAt(byte* buffer, uint offset, int length)
    {
        if (offset == 0 || offset >= length)
        {
            return "";
        }

        return Marshal.PtrToStringAnsi((nint)(buffer + offset))?.Trim() ?? "";
    }

    /// <summary>Screens on the desktop: monitor name, adapter, resolution and refresh rate.</summary>
    public static List<(string Name, string Adapter, int Width, int Height, int Hz, bool Primary)> ReadDisplays()
    {
        const int displayDeviceSize = 840; // DISPLAY_DEVICEW
        const int devModeSize = 220;       // DEVMODEW
        var displays = new List<(string, string, int, int, int, bool)>();
        byte* adapter = stackalloc byte[displayDeviceSize];
        byte* monitor = stackalloc byte[displayDeviceSize];
        byte* mode = stackalloc byte[devModeSize];
        for (uint i = 0; ; i++)
        {
            new Span<byte>(adapter, displayDeviceSize).Clear();
            *(uint*)adapter = displayDeviceSize;
            if (!EnumDisplayDevices(null, i, adapter, 0))
            {
                break;
            }

            uint state = *(uint*)(adapter + 4 + 64 + 256);
            if ((state & DisplayAttachedToDesktop) == 0)
            {
                continue;
            }

            string deviceName = new((char*)(adapter + 4));
            string adapterName = new((char*)(adapter + 4 + 64));
            new Span<byte>(mode, devModeSize).Clear();
            *(ushort*)(mode + 68) = devModeSize;
            if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, mode))
            {
                continue;
            }

            new Span<byte>(monitor, displayDeviceSize).Clear();
            *(uint*)monitor = displayDeviceSize;
            string monitorName = EnumDisplayDevices(deviceName, 0, monitor, 0) ? new string((char*)(monitor + 4 + 64)) : adapterName;
            displays.Add((monitorName, adapterName, *(int*)(mode + 172), *(int*)(mode + 176), *(int*)(mode + 184), (state & DisplayPrimary) != 0));
        }

        return displays;
    }

    /// <summary>
    /// Names of display adapters that are actually present (attached to a screen or not).
    /// The driver registry also keeps entries for cards that were removed long ago.
    /// </summary>
    public static HashSet<string> ReadPresentAdapters()
    {
        const int displayDeviceSize = 840;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte* adapter = stackalloc byte[displayDeviceSize];
        for (uint i = 0; ; i++)
        {
            new Span<byte>(adapter, displayDeviceSize).Clear();
            *(uint*)adapter = displayDeviceSize;
            if (!EnumDisplayDevices(null, i, adapter, 0))
            {
                return names;
            }

            names.Add(new string((char*)(adapter + 4 + 64)));
        }
    }

    /// <summary>Null on desktops without a battery.</summary>
    public static BatterySpecs? ReadBattery()
    {
        if (!GetSystemPowerStatus(out var status) || (status.BatteryFlag & 128) != 0 || status.BatteryFlag == 255)
        {
            return null;
        }

        return new BatterySpecs(
            status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null,
            (status.BatteryFlag & 8) != 0,
            status.ACLineStatus == 1);
    }
}
