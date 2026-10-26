using Pennyworth.Tests;
using Pennyworth.Platform.Linux;

namespace Pennyworth.Platform.Linux.Tests;

public class MountDriveServiceTests
{
    private const string Table = """
        43 1 0:39 /@/.snapshots/499/snapshot / rw,relatime shared:1 - btrfs /dev/nvme0n1p2 rw,subvol=/@
        39 43 0:7 / /dev rw,nosuid shared:2 - devtmpfs devtmpfs rw
        53 43 0:29 / /run rw,nosuid,nodev shared:14 - tmpfs tmpfs rw
        49 45 0:32 / /sys/firmware/efi/efivars rw - efivarfs efivarfs rw
        28 45 0:23 / /sys/fs/selinux rw,nosuid shared:12 - selinuxfs selinuxfs rw
        55 43 0:38 /@/.snapshots /.snapshots rw,relatime shared:45 - btrfs /dev/nvme0n1p2 rw
        67 43 0:51 /@/var /var rw,relatime shared:59 - btrfs /dev/nvme0n1p2 rw
        70 43 259:3 / /boot/efi rw,relatime shared:61 - vfat /dev/nvme0n1p3 rw
        97 43 8:16 / /mnt/a rw,relatime shared:65 - xfs /dev/sdb rw
        99 43 8:32 / /mnt/b rw,relatime shared:67 - xfs /dev/sdc rw
        54 43 259:1 / /home rw,relatime - ext4 /dev/nvme0n1p1 rw
        29 53 0:88 / /run/user/1000 rw shared:860 - tmpfs tmpfs rw
        875 29 0:100 / /run/user/1000/doc rw - fuse.portal portal rw
        500 43 7:0 / /snap/core/1 ro,nodev,relatime - squashfs /dev/loop0 ro
        501 43 8:48 / /run/media/ana/My\040Stick rw,nosuid,nodev,relatime - vfat /dev/sdd1 rw
        """;

    private static readonly Func<string, (long, long)?> Fixed = _ => (1000, 400);

    private static MountDriveService Service(Func<string, (long, long)?>? space = null, string labels = "/nonexistent") =>
        new(() => Table, space ?? Fixed, labels);

    [Fact]
    public void Unescapes_octal_sequences_in_mount_points()
    {
        var entry = MountTable.Parse("1 0 8:1 / /mnt/a\\040b\\011c\\012d\\134e rw - ext4 /dev/sda1 rw").Single();

        Assert.Equal("/mnt/a b\tc\nd\\e", entry.MountPoint);
    }

    [Fact]
    public void Leaves_malformed_escapes_alone_and_decodes_utf8_bytes()
    {
        Assert.Equal("/x\\9", MountTable.Unescape("/x\\9"));
        Assert.Equal("/ç", MountTable.Unescape("/\\303\\247"));
    }

    [Theory]
    [InlineData("1 0 8:1 / /m rw - ext4 /dev/sda1 rw")]
    [InlineData("1 0 8:1 / /m rw shared:1 - ext4 /dev/sda1 rw")]
    [InlineData("1 0 8:1 / /m rw shared:1 master:2 propagate_from:3 - ext4 /dev/sda1 rw")]
    public void Finds_the_fixed_tail_after_any_number_of_optional_fields(string line)
    {
        var entry = MountTable.Parse(line).Single();

        Assert.Equal("/m", entry.MountPoint);
        Assert.Equal("ext4", entry.FileSystemType);
        Assert.Equal("/dev/sda1", entry.Source);
        Assert.Equal("8:1", entry.MajorMinor);
        Assert.Equal("rw", entry.MountOptions);
    }

    [Fact]
    public void Skips_malformed_lines()
    {
        Assert.Empty(MountTable.Parse("garbage\n\n1 0 8:1 / /m rw ext4 /dev/sda1 rw"));
    }

    [Fact]
    public void Lists_all_mount_points_including_pseudo_ones()
    {
        var points = MountTable.MountPoints(Table);

        Assert.Contains("/run/user/1000", points);
        Assert.Contains("/run/media/ana/My Stick", points);
        Assert.Equal(15, points.Count);
    }

    [Fact]
    public void Drops_pseudo_file_systems_and_squashfs_loops()
    {
        var roots = Service().GetDrives().Select(d => d.RootPath).ToList();

        Assert.DoesNotContain("/dev", roots);
        Assert.DoesNotContain("/run", roots);
        Assert.DoesNotContain("/sys/firmware/efi/efivars", roots);
        Assert.DoesNotContain("/run/user/1000", roots);
        Assert.DoesNotContain("/run/user/1000/doc", roots);
        Assert.DoesNotContain("/snap/core/1", roots);
    }

    [Fact]
    public void Collapses_subvolume_mounts_with_different_device_numbers_into_the_shortest_path()
    {
        var roots = Service().GetDrives().Select(d => d.RootPath).ToList();

        Assert.Contains("/", roots);
        Assert.DoesNotContain("/var", roots);
        Assert.DoesNotContain("/.snapshots", roots);
    }

    [Fact]
    public void Keeps_distinct_partitions_that_report_the_same_size()
    {
        var roots = Service().GetDrives().Select(d => d.RootPath).ToList();

        Assert.Contains("/mnt/a", roots);
        Assert.Contains("/mnt/b", roots);
    }

    [Fact]
    public void Orders_the_root_first_then_by_path_and_keeps_removable_media()
    {
        var roots = Service().GetDrives().Select(d => d.RootPath).ToList();

        Assert.Equal(["/", "/boot/efi", "/home", "/mnt/a", "/mnt/b", "/run/media/ana/My Stick"], roots);
    }

    [Fact]
    public void Skips_a_mount_whose_space_cannot_be_read()
    {
        var drives = Service(path => path == "/home" ? null : (1000, 400)).GetDrives();

        Assert.DoesNotContain(drives, d => d.RootPath == "/home");
        Assert.Contains(drives, d => d.RootPath == "/");
    }

    [Fact]
    public void Reports_total_and_available_space()
    {
        var root = Service(_ => (1000, 400)).GetDrives().First();

        Assert.Equal(1000, root.TotalBytes);
        Assert.Equal(400, root.FreeBytes);
        Assert.Equal("/", Service().SystemDriveRoot);
    }

    [Fact]
    public void Falls_back_to_the_mount_point_without_a_label()
    {
        Assert.All(Service().GetDrives(), d => Assert.Equal(d.RootPath, d.Label));
    }

    [LinuxFact]
    public void Resolves_labels_through_symlinks_and_decodes_udev_escapes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pw-labels-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string alias = Path.Combine(Path.GetTempPath(), "pw-alias-" + Guid.NewGuid().ToString("N"));
        try
        {
            string device = "/dev/pw-test-sdd1";
            File.CreateSymbolicLink(alias, device);
            File.CreateSymbolicLink(Path.Combine(dir, "My\\x20Stick"), alias);
            string table = $"1 0 8:48 / /run/media/ana/s rw - vfat {device} rw\n2 0 8:49 / /other rw - ext4 /dev/pw-test-sde1 rw";

            var drives = new MountDriveService(() => table, _ => (10, 5), dir).GetDrives();

            Assert.Equal("My Stick", drives.Single(d => d.RootPath == "/run/media/ana/s").Label);
            Assert.Equal("/other", drives.Single(d => d.RootPath == "/other").Label);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            File.Delete(alias);
        }
    }

    [LinuxFact]
    public void Lists_real_drives_with_the_root_first()
    {
        var drives = new MountDriveService().GetDrives();

        Assert.NotEmpty(drives);
        Assert.Equal("/", drives[0].RootPath);
        Assert.All(drives, d => Assert.True(d.TotalBytes >= d.FreeBytes && d.TotalBytes > 0));
    }
}
