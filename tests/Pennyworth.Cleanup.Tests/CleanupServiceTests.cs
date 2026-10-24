using System.Diagnostics;
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

        var result = await Service().CleanAsync(Rule("%ROOT%\\cache"), new SyncProgress(progress), TestContext.Current.CancellationToken);

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
            result = await Service().CleanAsync(Rule("%ROOT%\\cache"), null, TestContext.Current.CancellationToken);
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

        await Service().CleanAsync(Rule("%ROOT%\\cache"), null, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(Path.Combine(_root, @"precious\photo.jpg")));
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().CleanAsync(rule, null, TestContext.Current.CancellationToken));
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

    private CleanupService Service() => new([], name => name == "ROOT" ? _root : Environment.GetEnvironmentVariable(name));

    private static CleanupRule Rule(string path) => new(
        "test", SafetyLevel.Safe, CleanupKind.FolderContents, [path], RequiresAdmin: false,
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
