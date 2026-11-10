using Pennyworth.Core.Storage;

namespace Pennyworth.Core.Tests;

public class StorageTreeTests
{
    // C:\
    //   Windows\            (Windows)
    //     a.dll 100
    //   Users\              (Other)
    //     Bob\              (YourFiles)
    //       photo.jpg 300
    //       AppData\        (Apps)
    //         cache.bin 50
    //   hiberfil.sys 1000   (HiddenSystem, root file)
    private static StorageTree BuildSample()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        int windows = b.AddDirectory(StorageTreeBuilder.RootIndex, "Windows", StorageCategory.Windows);
        b.AddFile(windows, "a.dll", 100, 90, StorageCategory.Windows);
        int users = b.AddDirectory(StorageTreeBuilder.RootIndex, "Users", StorageCategory.Other);
        int bob = b.AddDirectory(users, "Bob", StorageCategory.YourFiles);
        b.AddFile(bob, "photo.jpg", 300, 290, StorageCategory.YourFiles);
        int appData = b.AddDirectory(bob, "AppData", StorageCategory.Apps);
        b.AddFile(appData, "cache.bin", 50, 40, StorageCategory.Apps);
        b.AddFile(StorageTreeBuilder.RootIndex, "hiberfil.sys", 1000, 1000, StorageCategory.HiddenSystem);
        return b.Build();
    }

    [Fact]
    public void Rolls_sizes_up_to_every_directory()
    {
        var tree = BuildSample();

        Assert.Equal(1450, tree.Root.SizeOnDisk);
        Assert.Equal(1420, tree.Root.LogicalSize);
        Assert.Equal(1450, tree.TotalBytes);
        Assert.Equal(4, tree.FileCount);
        var users = tree.Root.GetChildren().Single(n => n.Name == "Users");
        Assert.Equal(350, users.SizeOnDisk);
    }

    [Fact]
    public void Children_are_sorted_largest_first()
    {
        var names = BuildSample().Root.GetChildren().Select(n => n.Name);

        Assert.Equal(["hiberfil.sys", "Users", "Windows"], names);
    }

    [Fact]
    public void Builds_full_paths_from_the_root()
    {
        var bob = BuildSample().Root.GetChildren().Single(n => n.Name == "Users").GetChildren().Single();

        Assert.Equal(Path.Combine(@"C:\", "Users", "Bob"), bob.FullPath);
        Assert.Equal(@"C:\", bob.Parent!.Value.Parent!.Value.FullPath);
    }

    [Fact]
    public void Tracks_bytes_per_category()
    {
        var tree = BuildSample();

        Assert.Equal(100, tree.GetCategoryBytes(StorageCategory.Windows));
        Assert.Equal(300, tree.GetCategoryBytes(StorageCategory.YourFiles));
        Assert.Equal(50, tree.GetCategoryBytes(StorageCategory.Apps));
        Assert.Equal(1000, tree.GetCategoryBytes(StorageCategory.HiddenSystem));
        Assert.Equal(0, tree.GetCategoryBytes(StorageCategory.Games));
    }

    [Fact]
    public void Category_areas_exclude_nested_areas_of_other_categories()
    {
        var tree = BuildSample();

        var yourFiles = Assert.Single(tree.GetCategoryAreas(StorageCategory.YourFiles));
        Assert.Equal(Path.Combine(@"C:\", "Users", "Bob"), yourFiles.Node.FullPath);
        Assert.Equal(300, yourFiles.Bytes);

        var apps = Assert.Single(tree.GetCategoryAreas(StorageCategory.Apps));
        Assert.Equal(Path.Combine(@"C:\", "Users", "Bob", "AppData"), apps.Node.FullPath);

        var hidden = Assert.Single(tree.GetCategoryAreas(StorageCategory.HiddenSystem));
        Assert.Equal(Path.Combine(@"C:\", "hiberfil.sys"), hidden.Node.FullPath);
        Assert.False(hidden.Node.IsDirectory);

        Assert.Empty(tree.GetCategoryAreas(StorageCategory.Games));
    }

    [Fact]
    public void Marks_locked_directories()
    {
        var b = new StorageTreeBuilder(@"D:\", StorageCategory.Other);
        int secret = b.AddDirectory(StorageTreeBuilder.RootIndex, "Secret", StorageCategory.Other);
        b.MarkLocked(secret);

        var node = Assert.Single(b.Build().Root.GetChildren());

        Assert.True(node.IsLocked);
        Assert.False(node.HasChildren);
    }

    [Fact]
    public void Rejects_children_added_to_a_file()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        b.AddFile(StorageTreeBuilder.RootIndex, "a.txt", 1, 1, StorageCategory.Other);

        Assert.Throws<ArgumentOutOfRangeException>(() => b.AddFile(1, "b.txt", 1, 1, StorageCategory.Other));
    }

    [Fact]
    public void Snapshot_reflects_files_added_so_far()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        b.AddFile(StorageTreeBuilder.RootIndex, "a.bin", 10, 10, StorageCategory.Games);

        var snapshot = b.SnapshotCategoryBytes();

        Assert.Equal(10, snapshot[(int)StorageCategory.Games]);
        Assert.Equal(StorageCategories.Count, snapshot.Length);
    }
}
