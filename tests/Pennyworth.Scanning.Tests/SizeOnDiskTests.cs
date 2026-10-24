namespace Pennyworth.Scanning.Tests;

public class SizeOnDiskTests
{
    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 4096L)]
    [InlineData(4096L, 4096L)]
    [InlineData(4097L, 8192L)]
    public void Rounds_up_to_whole_clusters(long logical, long expected)
    {
        Assert.Equal(expected, SizeOnDisk.Estimate(FileAttributes.Normal, logical, 4096));
    }

    [Theory]
    [InlineData(FileAttributes.Offline)]
    [InlineData((FileAttributes)0x00400000)] // RecallOnDataAccess: OneDrive "online-only"
    public void Online_only_cloud_files_use_no_space(FileAttributes attributes)
    {
        Assert.Equal(0, SizeOnDisk.Estimate(attributes, 1_000_000, 4096));
    }

    [Fact]
    public void Sparse_and_compressed_files_use_their_stored_size()
    {
        Assert.True(SizeOnDisk.NeedsStoredSize(FileAttributes.SparseFile));
        Assert.True(SizeOnDisk.NeedsStoredSize(FileAttributes.Compressed));
        Assert.False(SizeOnDisk.NeedsStoredSize(FileAttributes.Normal));
        Assert.False(SizeOnDisk.NeedsStoredSize(FileAttributes.SparseFile | FileAttributes.Offline));
        Assert.Equal(8192, SizeOnDisk.Estimate(FileAttributes.SparseFile, 1_000_000_000_000, 4096, storedSize: 5000));
    }

    [Theory]
    [InlineData(FileAttributes.Directory, 0u, true)]
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, 0xA0000003u, false)] // junction / mount point
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, 0xA000000Cu, false)] // symbolic link
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, 0x9000001Au, true)]  // cloud files
    [InlineData(FileAttributes.Directory | FileAttributes.ReparsePoint, 0x9000701Au, true)]  // cloud files, variant 7
    public void Enters_only_real_and_cloud_folders(FileAttributes attributes, uint tag, bool expected)
    {
        Assert.Equal(expected, SizeOnDisk.ShouldEnterDirectory(attributes, tag));
    }
}
