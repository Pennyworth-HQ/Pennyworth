using Pennyworth.App.Services;
using Pennyworth.Core.Storage;

namespace Pennyworth.App.Tests;

internal static class SampleTrees
{
    public const long Gb = 1024L * 1024 * 1024;

    // C:\  Windows\ (20 GB)  Games\ (50 GB)  Users\Bob\ (10 GB)  stray.bin (5 GB, Other)
    public static StorageTree Build()
    {
        var b = new StorageTreeBuilder(@"C:\", StorageCategory.Other);
        int windows = b.AddDirectory(StorageTreeBuilder.RootIndex, "Windows", StorageCategory.Windows);
        b.AddFile(windows, "system.dll", 20 * Gb, 20 * Gb, StorageCategory.Windows);
        int games = b.AddDirectory(StorageTreeBuilder.RootIndex, "Games", StorageCategory.Games);
        b.AddFile(games, "game.pak", 50 * Gb, 50 * Gb, StorageCategory.Games);
        int users = b.AddDirectory(StorageTreeBuilder.RootIndex, "Users", StorageCategory.Other);
        int bob = b.AddDirectory(users, "Bob", StorageCategory.YourFiles);
        b.AddFile(bob, "video.mp4", 10 * Gb, 10 * Gb, StorageCategory.YourFiles);
        b.AddFile(StorageTreeBuilder.RootIndex, "stray.bin", 5 * Gb, 5 * Gb, StorageCategory.Other);
        return b.Build();
    }
}

internal sealed class FakeDriveService(params DriveSummary[] drives) : IDriveService
{
    public string SystemDriveRoot { get; init; } = @"C:\";

    public IReadOnlyList<DriveSummary> GetDrives() => drives;
}

internal sealed class FakeScanner(Func<string, StorageTree> result) : IStorageScanner
{
    public List<string> ScannedRoots { get; } = [];

    public TaskCompletionSource? Gate { get; set; }

    public async Task<StorageTree> ScanAsync(string rootPath, IPathClassifier classifier, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        ScannedRoots.Add(rootPath);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        return result(rootPath);
    }
}

internal sealed class NullClassifier : IPathClassifier
{
    public StorageCategory Classify(string fullPath, StorageCategory inherited) => inherited;
}

internal sealed class FakeLocations(Dictionary<string, (string Title, string Description)>? known = null) : ILocationInfo
{
    public (string Title, string Description)? Describe(string fullPath) =>
        known is not null && known.TryGetValue(fullPath, out var value) ? value : null;
}

internal sealed class FakeExplorer : IExplorerLauncher
{
    public List<string> Shown { get; } = [];

    public void ShowInExplorer(string path) => Shown.Add(path);
}
