using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.Core.Storage;
using static Pennyworth.App.Tests.SampleTrees;

namespace Pennyworth.App.Tests;

public class StorageSummaryViewModelTests
{
    private static readonly DriveSummary Drive = new(@"C:\", "", TotalBytes: 200 * Gb, FreeBytes: 100 * Gb);

    private static StorageSummaryViewModel Create(Action<StorageNode>? open = null, ILocationInfo? locations = null) =>
        new(Drive, locations ?? new FakeLocations(), open ?? (_ => { }));

    [Fact]
    public void Splits_used_space_into_categories_and_not_measured()
    {
        var summary = Create();

        summary.ApplyResult(Build());

        Assert.Equal(50 * Gb, summary.Categories.Single(c => c.Category == StorageCategory.Games).Bytes);
        Assert.Equal(0.5, summary.Categories.Single(c => c.Category == StorageCategory.Games).Fraction, 3);
        Assert.Equal(15 * Gb, summary.Unaccounted.Bytes); // 100 GB used - 85 GB scanned
        Assert.False(summary.HasOvercount);
        Assert.Equal(1.0, summary.Segments.Sum(s => s.Fraction), 3);
        Assert.Contains(summary.Segments, s => s.ColorKey == "Unaccounted");
    }

    [Fact]
    public void Flags_overcount_when_scan_exceeds_used_space()
    {
        var summary = new StorageSummaryViewModel(new DriveSummary(@"C:\", "", 200 * Gb, 150 * Gb), new FakeLocations(), _ => { });

        summary.ApplyResult(Build()); // 85 GB scanned, only 50 GB used

        Assert.True(summary.HasOvercount);
        Assert.Equal(0, summary.Unaccounted.Bytes);
        Assert.Equal(1.0, summary.Segments.Sum(s => s.Fraction), 3);
    }

    [Fact]
    public void Updates_live_from_progress_until_complete()
    {
        var summary = Create();
        long[] bytes = new long[StorageCategories.Count];
        bytes[(int)StorageCategory.Apps] = 7 * Gb;

        summary.ApplyProgress(new ScanProgress(42, 7 * Gb, bytes));

        Assert.Equal(7 * Gb, summary.Categories.Single(c => c.Category == StorageCategory.Apps).Bytes);
        Assert.Equal(42, summary.FileCount);
        Assert.False(summary.IsComplete);

        summary.ApplyResult(Build());
        summary.ApplyProgress(new ScanProgress(1, 1, new long[StorageCategories.Count]));

        Assert.Equal(4, summary.FileCount);
        Assert.Equal(0, summary.Categories.Single(c => c.Category == StorageCategory.Apps).Bytes);
    }

    [Fact]
    public void Selects_the_biggest_category_and_lists_its_areas_with_friendly_titles()
    {
        var locations = new FakeLocations(new() { [@"C:\Games"] = ("My games", "Where games live") });
        var summary = Create(locations: locations);

        summary.ApplyResult(Build());

        Assert.Equal(StorageCategory.Games, summary.SelectedCategory?.Category);
        var area = Assert.Single(summary.SelectedCategory!.Areas);
        Assert.Equal("My games", area.Title);
        Assert.Equal("Where games live", area.Description);
        Assert.Equal(@"C:\Games", area.Path);
    }

    [Fact]
    public void Falls_back_to_folder_name_for_unknown_areas()
    {
        var summary = Create();

        summary.ApplyResult(Build());

        var yourFiles = summary.Categories.Single(c => c.Category == StorageCategory.YourFiles);
        Assert.Equal("Bob", Assert.Single(yourFiles.Areas).Title);
    }

    [Fact]
    public void Browse_all_is_only_available_after_the_scan()
    {
        StorageNode? opened = null;
        var summary = Create(open: node => opened = node);

        Assert.False(summary.BrowseAllCommand.CanExecute(null));
        summary.ApplyResult(Build());
        summary.BrowseAllCommand.Execute(null);

        Assert.Equal(@"C:\", opened?.FullPath);
    }

    [Fact]
    public void Opening_an_area_opens_its_folder()
    {
        StorageNode? opened = null;
        var summary = Create(open: node => opened = node);
        summary.ApplyResult(Build());

        summary.OpenAreaCommand.Execute(summary.SelectedCategory!.Areas[0]);

        Assert.Equal(@"C:\Games", opened?.FullPath);
    }
}
