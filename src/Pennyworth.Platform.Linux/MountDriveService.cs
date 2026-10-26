namespace Pennyworth.Platform.Linux;

/// <summary>
/// Lists the machine's drives from <c>/proc/self/mountinfo</c>.
/// <para>
/// A mount counts as a drive when its source is a block device node (<c>/dev/...</c>). That is the rule that
/// separates, on a real machine, the six disks from the pseudo mounts: proc, sysfs, cgroup2, efivarfs, selinuxfs,
/// devtmpfs, tmpfs and overlay all use a non-path source (<c>proc</c>, <c>tmpfs</c>, <c>none</c>...), and FUSE mounts
/// use <c>portal</c>/<c>gvfsd-fuse</c>. Loop devices carrying squashfs (snap/AppImage images, always read-only) do have a
/// <c>/dev/loopN</c> source, so squashfs is excluded by file system type.
/// </para>
/// <para>
/// Mounts are deduplicated by mount source, never by major:minor: every btrfs subvolume mount gets its own
/// major:minor but they all share one source (<c>/dev/nvme0n1p2</c>) and one pool of space, so <c>/</c>,
/// <c>/var</c>, <c>/opt</c>... are one drive. The mount with the shortest path represents the source.
/// Distinct partitions are distinct sources, so equal sizes never merge them.
/// </para>
/// </summary>
internal sealed class MountDriveService : IDriveService
{
    private readonly Func<string> _readMountinfo;
    private readonly Func<string, (long Total, long Free)?> _space;
    private readonly string _labelDirectory;

    public MountDriveService(
        Func<string>? readMountinfo = null,
        Func<string, (long Total, long Free)?>? space = null,
        string labelDirectory = "/dev/disk/by-label")
    {
        _readMountinfo = readMountinfo ?? MountTable.ReadSystem;
        _space = space ?? ReadSpace;
        _labelDirectory = labelDirectory;
    }

    public string SystemDriveRoot => "/";

    public IReadOnlyList<DriveSummary> GetDrives()
    {
        var candidates = MountTable.Parse(_readMountinfo())
            .Where(IsBlockDeviceMount)
            .GroupBy(e => e.Source, StringComparer.Ordinal)
            .Select(g => g.OrderBy(e => e.MountPoint.Length).ThenBy(e => e.MountPoint, StringComparer.Ordinal).First())
            .ToList();
        if (candidates.Count == 0)
            return [];

        var labels = ReadLabels();
        var drives = new List<DriveSummary>();
        foreach (var entry in candidates)
        {
            if (_space(entry.MountPoint) is not { } space)
                continue;
            string label = labels.TryGetValue(CanonicalPath(entry.Source), out string? found) && found.Length > 0
                ? found
                : entry.MountPoint;
            drives.Add(new DriveSummary(entry.MountPoint, label, space.Total, space.Free));
        }

        return drives
            .OrderBy(d => d.RootPath == "/" ? 0 : 1)
            .ThenBy(d => d.RootPath, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsBlockDeviceMount(MountEntry entry) =>
        entry.Source.StartsWith("/dev/", StringComparison.Ordinal)
        && !string.Equals(entry.FileSystemType, "squashfs", StringComparison.Ordinal);

    private static (long Total, long Free)? ReadSpace(string mountPoint)
    {
        try
        {
            var info = new DriveInfo(mountPoint);
            return (info.TotalSize, info.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Maps the real device path behind each by-label symlink to its decoded label.</summary>
    private Dictionary<string, string> ReadLabels()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (string link in Directory.EnumerateFileSystemEntries(_labelDirectory))
            {
                string label = MountTable.UnescapeUdev(Path.GetFileName(link));
                labels.TryAdd(CanonicalPath(link), label);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return labels;
    }

    private static string CanonicalPath(string path)
    {
        try
        {
            return File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Path.GetFullPath(path);
        }
    }
}
