using System.Text;
using Pennyworth.Monitoring.Hardware;

namespace Pennyworth.Monitoring.Tests;

public class SmbiosTests
{
    [Fact]
    public void Reads_system_board_bios_and_memory()
    {
        byte[] table = Table(
            Structure(0, [0x01, 0x02, 0x00, 0x00, 0x03], "American Megatrends", "F12", "03/15/2024"),
            Structure(1, [0x01, 0x02], "Micro-Star International", "MS-7C02"),
            Structure(2, [0x01, 0x02], "MSI", "B450 TOMAHAWK"),
            Structure(16, Bytes(0x0F, 0x0D, (0x0D, [4, 0]))),
            Module(sizeMb: 8192, slot: "DIMM_A1", type: 0x1A, speed: 3200, configured: 3000, maker: "Kingston", part: "KF3200C16D4/8GX"),
            Module(sizeMb: 0, slot: "DIMM_A2", type: 0x02, speed: 0, configured: 0, maker: "Unknown", part: "Unknown"),
            Module(sizeMb: 0x7FFF, extendedMb: 32768, slot: "DIMM_B1", type: 0x22, speed: 0xFFFF, extendedSpeed: 6000, configured: 4800,
                maker: "To Be Filled By O.E.M.", part: "CMK32GX5M1B6000"));

        SmbiosInfo info = Smbios.Parse(table);

        Assert.Equal("Micro-Star International", info.SystemManufacturer);
        Assert.Equal("MS-7C02", info.SystemProduct);
        Assert.Equal("B450 TOMAHAWK", info.BoardProduct);
        Assert.Equal("F12", info.BiosVersion);
        Assert.Equal("03/15/2024", info.BiosDate);
        Assert.Equal(4, info.MemorySlots);
        Assert.Equal(2, info.Modules.Count); // the empty slot is skipped

        var first = info.Modules[0];
        Assert.Equal(("DIMM_A1", 8L << 30, "DDR4", 3200, 3000), (first.Slot, first.Size, first.Type, first.SpeedMts, first.ConfiguredMts));
        Assert.Equal("Kingston", first.Manufacturer);
        Assert.Equal("DIMM", first.FormFactor);

        var second = info.Modules[1];
        Assert.Equal(32L << 30, second.Size);
        Assert.Equal("DDR5", second.Type);
        Assert.Equal(6000, second.SpeedMts);
        Assert.Null(second.Manufacturer); // placeholder text dropped
    }

    [Fact]
    public void Survives_empty_or_broken_tables()
    {
        Assert.Empty(Smbios.Parse([]).Modules);
        Assert.Empty(Smbios.Parse([0, 3, 0, 0, 0, 0, 0, 0, 17, 200, 0]).Modules);
    }

    /// <summary>A type 17 memory device, SMBIOS 3.3 layout (0x5C bytes).</summary>
    private static byte[] Module(int sizeMb, string slot, byte type, int speed, int configured, string maker, string part,
        int extendedMb = 0, int extendedSpeed = 0)
    {
        var f = new byte[0x5C - 4];
        void Put16(int at, int v) { f[at - 4] = (byte)v; f[at - 3] = (byte)(v >> 8); }
        void Put32(int at, int v) => BitConverter.GetBytes(v).CopyTo(f, at - 4);
        Put16(0x0C, sizeMb);
        f[0x0E - 4] = 0x09;   // DIMM
        f[0x10 - 4] = 1;      // device locator → string 1
        f[0x12 - 4] = type;
        Put16(0x15, speed);
        f[0x17 - 4] = 2;      // manufacturer → string 2
        f[0x1A - 4] = 3;      // part number → string 3
        Put32(0x1C, extendedMb);
        Put16(0x20, configured);
        Put32(0x54, extendedSpeed);
        return Structure(17, f, slot, maker, part);
    }

    private static byte[] Bytes(int length, int unusedType, params (int At, byte[] Value)[] fields)
    {
        var f = new byte[length - 4];
        foreach (var (at, value) in fields)
        {
            value.CopyTo(f, at - 4);
        }

        return f;
    }

    private static byte[] Structure(byte type, byte[] formatted, params string[] strings)
    {
        var bytes = new List<byte> { type, (byte)(formatted.Length + 4), 0, 0 };
        bytes.AddRange(formatted);
        foreach (string s in strings)
        {
            bytes.AddRange(Encoding.ASCII.GetBytes(s));
            bytes.Add(0);
        }

        bytes.Add(0);
        if (strings.Length == 0)
        {
            bytes.Add(0);
        }

        return [.. bytes];
    }

    private static byte[] Table(params byte[][] structures) =>
        [.. new byte[8], .. structures.SelectMany(s => s), 127, 4, 0, 0, 0, 0];
}

/// <summary>Runs against the real machine: only checks that the answers are sane.</summary>
public class WindowsSpecsReaderTests
{
    [Fact]
    public void Reads_this_machine()
    {
        PcSpecs specs = new WindowsSpecsReader().Read();

        Assert.Equal(Environment.MachineName, specs.System.ComputerName);
        Assert.StartsWith("Windows", specs.Os.Name);
        Assert.Matches(@"^\d+(\.\d+)?$", specs.Os.Build);
        Assert.NotEqual("?", specs.Cpu.Name);
        Assert.Equal(Environment.ProcessorCount, specs.Cpu.Threads);
        Assert.InRange(specs.Cpu.Cores, 1, specs.Cpu.Threads);
        Assert.True(specs.Memory.Total > 512L * 1024 * 1024);
        Assert.Contains(specs.Disks, disk => disk.Size > 0 && disk.Letters.Contains(Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\')));
        Assert.NotEmpty(specs.Displays.Concat<object>(specs.Gpus)); // a test runner may have no desktop, but has an adapter
    }
}
