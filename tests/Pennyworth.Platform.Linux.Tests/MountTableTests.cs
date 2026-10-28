using Pennyworth.Platform.Linux;
using Pennyworth.Tests;

namespace Pennyworth.Platform.Linux.Tests;

public class MountTableTests
{
    [LinuxFact]
    public void Unescapes_octal_sequences_in_mount_points()
    {
        var entry = MountTable.Parse("1 0 8:1 / /mnt/a\\040b\\011c\\012d\\134e rw - ext4 /dev/sda1 rw").Single();

        Assert.Equal("/mnt/a b\tc\nd\\e", entry.MountPoint);
    }

    [LinuxFact]
    public void Leaves_malformed_escapes_alone_and_decodes_utf8_bytes()
    {
        Assert.Equal("/x\\9", MountTable.Unescape("/x\\9"));
        Assert.Equal("/ç", MountTable.Unescape("/\\303\\247"));
    }

    [LinuxFact]
    public void Decodes_udev_hex_escapes()
    {
        Assert.Equal("My Disk", MountTable.UnescapeUdev("My\\x20Disk"));
    }

    [LinuxTheory]
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

    [LinuxTheory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1 2 3:4 / /x rw - ext4")]
    public void Lines_that_are_not_mountinfo_are_skipped(string text) => Assert.Empty(MountTable.Parse(text));

    [LinuxFact]
    public void Btrfs_subvolume_mounts_keep_their_own_device_number_but_share_the_source()
    {
        var mounts = MountTable.Parse("""
            43 1 0:39 /@ / rw - btrfs /dev/nvme0n1p2 rw
            55 43 0:38 /@/.snapshots /.snapshots rw - btrfs /dev/nvme0n1p2 rw
            """);

        Assert.NotEqual(mounts[0].MajorMinor, mounts[1].MajorMinor);
        Assert.Equal(mounts[0].Source, mounts[1].Source);
    }
}
