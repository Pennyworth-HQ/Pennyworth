using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pennyworth.Scanning.Native;

internal readonly record struct DirectoryEntry(string Name, FileAttributes Attributes, long Size, uint ReparseTag)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;
}

[SupportedOSPlatform("windows")]
internal static unsafe class NativeDirectory
{
    /// <summary>
    /// Lists one directory without following anything. Returns false when Windows refuses
    /// access (or the folder vanished), so callers can mark it as locked.
    /// </summary>
    public static bool TryList(string directoryPath, List<DirectoryEntry> entries)
    {
        string search = ToExtendedPath(Path.Join(directoryPath, "*"));
        Win32FindData data;
        nint handle = NativeMethods.FindFirstFileEx(
            search,
            NativeMethods.FindExInfoBasic,
            &data,
            NativeMethods.FindExSearchNameMatch,
            0,
            NativeMethods.FindFirstExLargeFetch);

        if (handle == NativeMethods.InvalidHandleValue)
        {
            return Marshal.GetLastPInvokeError() == NativeMethods.ErrorFileNotFound;
        }

        try
        {
            do
            {
                string name = new(data.FileName);
                if (name is "." or "..")
                {
                    continue;
                }

                var attributes = (FileAttributes)data.FileAttributes;
                long size = ((long)data.FileSizeHigh << 32) | data.FileSizeLow;
                uint reparseTag = (attributes & FileAttributes.ReparsePoint) != 0 ? data.Reserved0 : 0;
                entries.Add(new DirectoryEntry(name, attributes, size, reparseTag));
            }
            while (NativeMethods.FindNextFile(handle, &data));

            return Marshal.GetLastPInvokeError() == NativeMethods.ErrorNoMoreFiles;
        }
        finally
        {
            NativeMethods.FindClose(handle);
        }
    }

    /// <summary>
    /// Bytes actually stored for a sparse or compressed file, or null when Windows can't tell.
    /// </summary>
    public static long? GetStoredSize(string filePath)
    {
        uint low = NativeMethods.GetCompressedFileSize(ToExtendedPath(filePath), out uint high);
        if (low == NativeMethods.InvalidFileSize && Marshal.GetLastPInvokeError() != 0)
        {
            return null;
        }

        return ((long)high << 32) | low;
    }

    public static long GetClusterSize(string rootPath)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(rootPath)) ?? rootPath;
        return NativeMethods.GetDiskFreeSpace(root, out uint sectorsPerCluster, out uint bytesPerSector, out _, out _)
            ? (long)sectorsPerCluster * bytesPerSector
            : 4096;
    }

    private static string ToExtendedPath(string path) =>
        path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path
        : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;
}
