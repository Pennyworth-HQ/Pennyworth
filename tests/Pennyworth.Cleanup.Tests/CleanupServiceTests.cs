using System.Diagnostics;
using System.Text;
using Pennyworth.KnowledgeBase;

namespace Pennyworth.Cleanup.Tests;

public sealed class CleanupServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pw-clean-").FullName;
    private readonly List<string> _junctions = [];

    public void Dispose()
    {
        _junctions.ForEach(junction => Directory.Delete(junction));
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Measures_without_deleting()
    {
        Write(@"cache\a.bin", 1000);
        Write(@"cache\sub\b.bin", 500);
        var rule = Rule("%ROOT%\\cache");

        var estimate = await Service().MeasureAsync(rule, TestContext.Current.CancellationToken);

        Assert.Equal(1500, estimate.Bytes);
        Assert.Equal(2, estimate.Items);
        Assert.True(estimate.IsAvailable);
        Assert.True(File.Exists(Path.Combine(_root, @"cache\a.bin")));
    }

    [Fact]
    public async Task Deletes_the_contents_but_keeps_the_folder()
    {
        Write(@"cache\a.bin", 1000);
        Write(@"cache\sub\deep\b.bin", 500);
        var progress = new List<double>();

        var result = await MeasureAndClean(Rule("%ROOT%\\cache"), new SyncProgress(progress));

        Assert.Equal(1500, result.BytesFreed);
        Assert.Equal(2, result.FilesDeleted);
        Assert.Equal(0, result.Skipped);
        Assert.True(Directory.Exists(Path.Combine(_root, "cache")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "cache")));
        Assert.Equal(1, progress[^1]);
    }

    [Fact]
    public async Task Skips_files_in_use()
    {
        Write(@"cache\busy.bin", 100);
        Write(@"cache\free.bin", 100);

        CleanupResult result;
        using (File.Open(Path.Combine(_root, @"cache\busy.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await MeasureAndClean(Rule("%ROOT%\\cache"), null);
        }

        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(Path.Combine(_root, @"cache\busy.bin")));
    }

    [Fact]
    public async Task Never_follows_junctions_out_of_the_folder()
    {
        Write(@"precious\photo.jpg", 100);
        Write(@"cache\a.bin", 100);
        CreateJunction(Path.Combine(_root, @"cache\link"), Path.Combine(_root, "precious"));

        await MeasureAndClean(Rule("%ROOT%\\cache"), null);

        Assert.True(File.Exists(Path.Combine(_root, @"precious\photo.jpg")));
    }

    [Fact]
    public async Task Lists_every_file_and_folder_it_would_delete()
    {
        Write(@"cache\a.bin", 1000);
        Write(@"cache\sub\b.bin", 500);
        Write(@"other\c.bin", 100);

        var estimate = await Service().MeasureAsync(Rule("%ROOT%\\cache", "%ROOT%\\other"), TestContext.Current.CancellationToken);

        Assert.Equal(
            [Path.Combine(_root, @"cache\a.bin"), Path.Combine(_root, @"cache\sub\b.bin"), Path.Combine(_root, @"other\c.bin")],
            estimate.Files!.Select(f => f.Path).Order());
        Assert.Equal([(1500L, 2L), (100L, 1L)], estimate.Folders!.Select(f => (f.Bytes, f.Files)));
    }

    [Fact]
    public async Task Deletes_only_what_was_listed()
    {
        Write(@"cache\listed.bin", 100);
        var estimate = await Service().MeasureAsync(Rule("%ROOT%\\cache"), TestContext.Current.CancellationToken);
        Write(@"cache\new-after-preview.bin", 100);
        Write(@"cache\later\also-new.bin", 100);

        var result = await Service().CleanAsync(estimate, null, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.FilesDeleted);
        Assert.False(File.Exists(Path.Combine(_root, @"cache\listed.bin")));
        Assert.True(File.Exists(Path.Combine(_root, @"cache\new-after-preview.bin")));
        Assert.True(File.Exists(Path.Combine(_root, @"cache\later\also-new.bin")));
    }

    [Fact]
    public async Task Leaves_files_that_changed_since_the_preview()
    {
        Write(@"cache\changed.bin", 100);
        var estimate = await Service().MeasureAsync(Rule("%ROOT%\\cache"), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(Path.Combine(_root, @"cache\changed.bin"), DateTime.UtcNow.AddMinutes(5));

        var result = await Service().CleanAsync(estimate, null, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.FilesDeleted);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(Path.Combine(_root, @"cache\changed.bin")));
    }

    [Fact]
    public async Task Removes_only_folders_it_emptied()
    {
        Write(@"cache\deep\inner\a.bin", 100);
        Directory.CreateDirectory(Path.Combine(_root, @"cache\was-empty"));

        await MeasureAndClean(Rule("%ROOT%\\cache"), null);

        Assert.False(Directory.Exists(Path.Combine(_root, @"cache\deep")));
        Assert.True(Directory.Exists(Path.Combine(_root, @"cache\was-empty")));
    }

    [Fact]
    public async Task Refuses_listed_files_outside_the_rule_folders()
    {
        Write(@"cache\a.bin", 10);
        Write(@"precious\photo.jpg", 10);
        var estimate = await Service().MeasureAsync(Rule("%ROOT%\\cache"), TestContext.Current.CancellationToken);
        var photo = new FileInfo(Path.Combine(_root, @"precious\photo.jpg"));
        var tampered = estimate with { Files = [.. estimate.Files!, new CleanupFile(photo.FullName, photo.Length, photo.LastWriteTimeUtc)] };

        var result = await Service().CleanAsync(tampered, null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(photo.FullName));
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void Reads_recycle_bin_records()
    {
        var deleted = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal((@"C:\Users\Bob\notes.txt", 4096L, deleted), RecycleBin.ParseInfo(InfoV2(4096, deleted, @"C:\Users\Bob\notes.txt")));
        Assert.Equal((@"D:\old.doc", 10L, deleted), RecycleBin.ParseInfo(InfoV1(10, deleted, @"D:\old.doc")));
        Assert.Null(RecycleBin.ParseInfo(new byte[10]));
        Assert.Null(RecycleBin.ParseInfo([.. BitConverter.GetBytes(7L), .. new byte[40]]));
    }

    [Fact]
    public async Task Expands_wildcard_folders()
    {
        Write(@"profiles\Default\Cache\a.bin", 10);
        Write(@"profiles\Profile 1\Cache\b.bin", 20);
        Write(@"profiles\Profile 1\Bookmarks", 30);

        var estimate = await Service().MeasureAsync(Rule("%ROOT%\\profiles\\*\\Cache"), TestContext.Current.CancellationToken);

        Assert.Equal(30, estimate.Bytes);
    }

    [Fact]
    public async Task Ignores_patterns_with_unknown_variables()
    {
        var estimate = await Service().MeasureAsync(Rule("%NOT_SET%\\cache"), TestContext.Current.CancellationToken);

        Assert.Equal(0, estimate.Bytes);
    }

    [Fact]
    public async Task Refuses_admin_rules()
    {
        var rule = Rule("%ROOT%\\cache") with { RequiresAdmin = true };
        Write(@"cache\a.bin", 10);

        var estimate = await Service().MeasureAsync(rule, TestContext.Current.CancellationToken);

        Assert.False(estimate.IsAvailable);
        Assert.Equal(10, estimate.Bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().CleanAsync(estimate, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Resolves_absolute_folders_that_have_no_drive_letter()
    {
        string folder = Path.Combine(_root, "cache");
        Directory.CreateDirectory(folder);

        IReadOnlyList<string> resolved = CleanupTargets.Resolve([folder, folder + Path.DirectorySeparatorChar], Environment.GetEnvironmentVariable);

        Assert.Equal([Path.GetFullPath(folder)], resolved);
    }

    [Fact]
    public void Expands_a_wildcard_under_an_absolute_folder_that_has_no_drive_letter()
    {
        Directory.CreateDirectory(Path.Combine(_root, "profiles", "Default", "Cache"));
        Directory.CreateDirectory(Path.Combine(_root, "profiles", "Profile 1", "Cache"));

        IReadOnlyList<string> resolved = CleanupTargets.Resolve(
            [Path.Combine(_root, "profiles", "*", "Cache")],
            Environment.GetEnvironmentVariable);

        Assert.Equal(2, resolved.Count);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"C:\Program Files\App\cache")]
    [InlineData(@"C:\Program Files (x86)\Steam\shadercache")]
    [InlineData(@"C:\Users\Bob")]
    public void Guard_refuses_protected_places(string folder)
    {
        Assert.False(SafetyGuard.MayCleanInside(folder, Env));
    }

    [Fact]
    public void Guard_allows_caches_in_the_user_profile()
    {
        Assert.True(SafetyGuard.MayCleanInside(@"C:\Users\Bob\AppData\Local\Temp", Env));
    }

    [Fact]
    public async Task History_keeps_every_run()
    {
        var history = new CleanupHistory(Path.Combine(_root, @"logs\history.jsonl"));
        var rule = Rule("%ROOT%\\cache");
        var when = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

        history.Append([new CleanupResult(rule, 100, 2, 1)], when);
        history.Append([new CleanupResult(rule, 50, 1, 0)], when.AddHours(1));
        await File.AppendAllTextAsync(Path.Combine(_root, @"logs\history.jsonl"), "garbage\n", TestContext.Current.CancellationToken);

        var entries = history.Read();

        Assert.Equal([100L, 50L], entries.Select(e => e.BytesFreed));
        Assert.Equal("test", entries[0].Items[0].RuleId);
        Assert.Equal(when, entries[0].When);
    }

    private static string? Env(string name) => name switch
    {
        "SystemRoot" => @"C:\Windows",
        "ProgramFiles" => @"C:\Program Files",
        "ProgramFiles(x86)" => @"C:\Program Files (x86)",
        "USERPROFILE" => @"C:\Users\Bob",
        _ => null,
    };

    private async Task<CleanupResult> MeasureAndClean(CleanupRule rule, IProgress<double>? progress)
    {
        var estimate = await Service().MeasureAsync(rule, TestContext.Current.CancellationToken);
        return await Service().CleanAsync(estimate, progress, TestContext.Current.CancellationToken);
    }

    private static byte[] InfoV2(long size, DateTime deleted, string path) =>
    [
        .. BitConverter.GetBytes(2L), .. BitConverter.GetBytes(size), .. BitConverter.GetBytes(deleted.ToFileTimeUtc()),
        .. BitConverter.GetBytes(path.Length + 1), .. Encoding.Unicode.GetBytes(path + "\0"),
    ];

    private static byte[] InfoV1(long size, DateTime deleted, string path)
    {
        byte[] name = new byte[520];
        Encoding.Unicode.GetBytes(path).CopyTo(name, 0);
        return [.. BitConverter.GetBytes(1L), .. BitConverter.GetBytes(size), .. BitConverter.GetBytes(deleted.ToFileTimeUtc()), .. name];
    }

    private CleanupService Service() => new([], name => name == "ROOT" ? _root : Environment.GetEnvironmentVariable(name));

    private static CleanupRule Rule(params string[] paths) => new(
        "test", SafetyLevel.Safe, CleanupKind.FolderContents, paths, RequiresAdmin: false,
        new("Test", "Teste"), new("", ""), new("", ""));

    private void Write(string relativePath, int length)
    {
        string path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    private void CreateJunction(string link, string target)
    {
        _junctions.Add(link);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private sealed class SyncProgress(List<double> values) : IProgress<double>
    {
        public void Report(double value)
        {
            lock (values)
            {
                values.Add(value);
            }
        }
    }
}
