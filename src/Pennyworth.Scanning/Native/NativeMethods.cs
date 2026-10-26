using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pennyworth.Scanning.Native;

[SupportedOSPlatform("windows")]
internal static unsafe partial class NativeMethods
{
    public const int FindExInfoBasic = 1;
    public const int FindExSearchNameMatch = 0;
    public const int FindFirstExLargeFetch = 2;
    public const int ErrorFileNotFound = 2;
    public const int ErrorNoMoreFiles = 18;
    public static readonly nint InvalidHandleValue = -1;

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindFirstFileEx(
        string fileName,
        int infoLevel,
        Win32FindData* findData,
        int searchOp,
        nint searchFilter,
        int additionalFlags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextFile(nint findHandle, Win32FindData* findData);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(nint findHandle);

    public const uint InvalidFileSize = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetCompressedFileSize(string fileName, out uint fileSizeHigh);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpace(
        string rootPathName,
        out uint sectorsPerCluster,
        out uint bytesPerSector,
        out uint numberOfFreeClusters,
        out uint totalNumberOfClusters);
}

/// <summary>WIN32_FIND_DATAW. FILETIMEs are kept as uint pairs to preserve the 4-byte layout.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct Win32FindData
{
    public uint FileAttributes;
    public uint CreationTimeLow;
    public uint CreationTimeHigh;
    public uint LastAccessTimeLow;
    public uint LastAccessTimeHigh;
    public uint LastWriteTimeLow;
    public uint LastWriteTimeHigh;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint Reserved0;
    public uint Reserved1;
    public fixed char FileName[260];
    public fixed char AlternateFileName[14];
}
