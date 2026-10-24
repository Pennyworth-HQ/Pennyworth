using System.Runtime.InteropServices;

namespace Pennyworth.Cleanup;

/// <summary>The Recycle Bin of every drive, through Windows' own functions.</summary>
public static partial class RecycleBin
{
    private const uint NoConfirmation = 0x1;
    private const uint NoProgressUi = 0x2;
    private const uint NoSound = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo
    {
        public uint Size;
        public long Bytes;
        public long Items;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBin(string? rootPath, ref QueryInfo info);

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBin(nint window, string? rootPath, uint flags);

    /// <summary>Size and number of items in all Recycle Bins, or (0, 0) when Windows can't tell.</summary>
    public static (long Bytes, long Items) Query()
    {
        var info = new QueryInfo { Size = (uint)Marshal.SizeOf<QueryInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.Bytes, info.Items) : (0, 0);
    }

    /// <summary>Empties every Recycle Bin without Windows' own confirmation (the app already asked).</summary>
    public static bool Empty()
    {
        int result = SHEmptyRecycleBin(0, null, NoConfirmation | NoProgressUi | NoSound);
        const int AlreadyEmpty = unchecked((int)0x8000FFFF); // E_UNEXPECTED when there is nothing to empty.
        return result == 0 || result == AlreadyEmpty;
    }
}
