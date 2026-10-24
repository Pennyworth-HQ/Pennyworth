namespace Pennyworth.Scanning;

/// <summary>Estimates allocated size from what a directory listing provides.</summary>
internal static class SizeOnDisk
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes OnlineOnly = FileAttributes.Offline | RecallOnDataAccess;

    private const uint CloudReparseTag = 0x9000001A;
    private const uint CloudReparseTagMask = 0x0000F000;

    /// <summary>
    /// Sparse and compressed files (for example WSL and Docker virtual disks) store far
    /// less than their logical size, so their real stored size must be asked for.
    /// </summary>
    public static bool NeedsStoredSize(FileAttributes attributes) =>
        (attributes & OnlineOnly) == 0
        && (attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0;

    /// <summary>
    /// Rounds up to whole clusters. Cloud files that are only online (OneDrive and
    /// similar) use no local space. Hard links are only exact in the full scan.
    /// </summary>
    /// <param name="storedSize">From <c>GetCompressedFileSize</c>, when <see cref="NeedsStoredSize"/> is true.</param>
    public static long Estimate(FileAttributes attributes, long logicalSize, long clusterSize, long? storedSize = null)
    {
        if ((attributes & OnlineOnly) != 0)
        {
            return 0;
        }

        long bytes = storedSize ?? logicalSize;
        return (bytes + clusterSize - 1) / clusterSize * clusterSize;
    }

    /// <summary>
    /// Directories that are reparse points (junctions, symbolic links, mount points) are
    /// skipped so nothing is counted twice, except cloud-sync folders, which hold real files.
    /// </summary>
    public static bool ShouldEnterDirectory(FileAttributes attributes, uint reparseTag) =>
        (attributes & FileAttributes.ReparsePoint) == 0
        || (reparseTag & ~CloudReparseTagMask) == CloudReparseTag;
}
