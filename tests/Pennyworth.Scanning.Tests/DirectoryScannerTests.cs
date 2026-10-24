using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Pennyworth.Core.Storage;

namespace Pennyworth.Scanning.Tests;

public sealed class DirectoryScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pw-scan-").FullName;
    private readonly List<string> _deniedDirectories = [];
    private readonly List<string> _junctions = [];

    public void Dispose()
    {
        _deniedDirectories.ForEach(RestoreAccess);
        _junctions.ForEach(junction => Directory.Delete(junction));
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Counts_every_file_and_rolls_up_sizes()
    {
        WriteFile("a.txt", 1000);
        WriteFile(@"docs\b.txt", 5000);
        WriteFile(@"docs\deep\c.txt", 300);

        var tree = await ScanAsync();

        Assert.Equal(3, tree.FileCount);
        Assert.Equal(6300, tree.Root.LogicalSize);
        Assert.True(tree.Root.SizeOnDisk >= tree.Root.LogicalSize);
        var docs = tree.Root.GetChildren().Single(n => n.Name == "docs");
        Assert.Equal(5300, docs.LogicalSize);
        Assert.Equal(Path.Combine(_root, "docs"), docs.FullPath);
    }

    [Fact]
    public async Task Applies_the_classifier_to_folders_and_inherits_it_below()
    {
        WriteFile(@"games\x\big.bin", 2000);
        WriteFile(@"other\small.bin", 10);

        var tree = await ScanAsync(new NameClassifier("games", StorageCategory.Games));

        var area = Assert.Single(tree.GetCategoryAreas(StorageCategory.Games));
        Assert.Equal("games", area.Node.Name);
        Assert.Equal(2000, area.Node.LogicalSize);
        Assert.Equal(tree.GetCategoryBytes(StorageCategory.Games), area.Bytes);
        Assert.True(tree.GetCategoryBytes(StorageCategory.Other) > 0);
    }

    [Fact]
    public async Task Classifies_files_in_the_scan_root()
    {
        WriteFile("hiberfil.sys", 4096);
        WriteFile(@"sub\hiberfil.sys", 4096);

        var tree = await ScanAsync(new NameClassifier("hiberfil.sys", StorageCategory.HiddenSystem));

        Assert.Equal(4096, tree.GetCategoryBytes(StorageCategory.HiddenSystem));
    }

    [Fact]
    public async Task Counts_sparse_files_by_their_stored_size()
    {
        string path = Path.Combine(_root, "disk.vhdx");
        using (File.Create(path))
        {
        }

        RunCmd($"fsutil sparse setflag \"{path}\"");
        using (var stream = new FileStream(path, FileMode.Open))
        {
            stream.SetLength(10L * 1024 * 1024 * 1024); // 10 GB logical, nothing written
        }

        var tree = await ScanAsync();

        var disk = Assert.Single(tree.Root.GetChildren());
        Assert.Equal(10L * 1024 * 1024 * 1024, disk.LogicalSize);
        Assert.True(disk.SizeOnDisk < 1024 * 1024, $"Expected almost nothing stored, got {disk.SizeOnDisk}.");
    }

    [Fact]
    public async Task Does_not_follow_junctions()
    {
        WriteFile(@"real\data.bin", 8000);
        CreateJunction(Path.Combine(_root, "link"), Path.Combine(_root, "real"));

        var tree = await ScanAsync();

        Assert.Equal(1, tree.FileCount);
        Assert.DoesNotContain(tree.Root.GetChildren(), n => n.Name == "link");
    }

    [Fact]
    public async Task Marks_unreadable_folders_as_locked_and_keeps_going()
    {
        WriteFile(@"secret\hidden.bin", 100);
        WriteFile("visible.bin", 100);
        DenyListing(Path.Combine(_root, "secret"));

        var tree = await ScanAsync();

        var secret = tree.Root.GetChildren().Single(n => n.Name == "secret");
        Assert.True(secret.IsLocked);
        Assert.Equal(1, tree.FileCount);
    }

    [Fact]
    public async Task Reports_progress_with_category_totals()
    {
        WriteFile("a.bin", 100);
        var reports = new List<ScanProgress>();

        await new DirectoryScanner(workerCount: 2).ScanAsync(_root, new NameClassifier("none", StorageCategory.Other), new SyncProgress(reports), CancellationToken.None);

        Assert.NotEmpty(reports);
        ScanProgress last = reports[^1];
        Assert.Equal(1, last.FileCount);
        Assert.Equal(StorageCategories.Count, last.CategoryBytes.Count);
    }

    [Fact]
    public async Task Stops_when_cancelled()
    {
        WriteFile("a.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DirectoryScanner().ScanAsync(_root, new NameClassifier("x", StorageCategory.Other), null, cts.Token));
    }

    private Task<StorageTree> ScanAsync(IPathClassifier? classifier = null) =>
        new DirectoryScanner(workerCount: 4).ScanAsync(
            _root,
            classifier ?? new NameClassifier("none", StorageCategory.Other),
            null,
            TestContext.Current.CancellationToken);

    private void WriteFile(string relativePath, int length)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    private void CreateJunction(string link, string target)
    {
        _junctions.Add(link);
        RunCmd($"mklink /J \"{link}\" \"{target}\"");
    }

    private static void RunCmd(string command)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c {command}")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private void DenyListing(string directory)
    {
        _deniedDirectories.Add(directory);
        var info = new DirectoryInfo(directory);
        DirectorySecurity security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory,
            AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    private static void RestoreAccess(string directory)
    {
        var info = new DirectoryInfo(directory);
        DirectorySecurity security = info.GetAccessControl();
        security.RemoveAccessRuleAll(new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.ListDirectory,
            AccessControlType.Deny));
        info.SetAccessControl(security);
    }

    /// <summary>Classifies any path whose last segment equals <paramref name="name"/>.</summary>
    private sealed class NameClassifier(string name, StorageCategory category) : IPathClassifier
    {
        public StorageCategory Classify(string fullPath, StorageCategory inherited) =>
            string.Equals(Path.GetFileName(fullPath), name, StringComparison.OrdinalIgnoreCase) ? category : inherited;
    }

    private sealed class SyncProgress(List<ScanProgress> reports) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }
}
