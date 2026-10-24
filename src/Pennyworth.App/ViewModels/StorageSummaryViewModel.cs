using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <param name="ColorKey">Looked up as the resource <c>Brush.Category.{ColorKey}</c>.</param>
public sealed record BarSegment(string ColorKey, double Fraction);

/// <summary>The plain-language summary of one drive. Updates live while the scan runs.</summary>
public sealed partial class StorageSummaryViewModel : ObservableObject
{
    private const int MaxAreasPerCategory = 10;

    private readonly ILocationInfo _locations;
    private readonly Action<StorageNode> _openFolder;
    private StorageTree? _tree;

    public StorageSummaryViewModel(DriveSummary drive, ILocationInfo locations, Action<StorageNode> openFolder)
    {
        Drive = drive;
        _locations = locations;
        _openFolder = openFolder;
        Categories = StorageCategories.All.Select(category => new CategoryRowViewModel(category)).ToArray();
        Unaccounted = new CategoryRowViewModel(null);
        Update(new long[StorageCategories.Count], 0);
    }

    public DriveSummary Drive { get; }

    public IReadOnlyList<CategoryRowViewModel> Categories { get; }

    public CategoryRowViewModel Unaccounted { get; }

    public string UsedText => string.Format(Strings.DriveUsed, ByteSize.Format(Drive.UsedBytes), ByteSize.Format(Drive.TotalBytes));

    [ObservableProperty]
    public partial IReadOnlyList<BarSegment> Segments { get; private set; } = [];

    [ObservableProperty]
    public partial long FileCount { get; private set; }

    /// <summary>The scan counted more than Windows reports as used (hard links).</summary>
    [ObservableProperty]
    public partial bool HasOvercount { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BrowseAllCommand))]
    public partial bool IsComplete { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlacesTitle))]
    public partial CategoryRowViewModel? SelectedCategory { get; set; }

    /// <summary>Heading of the biggest-places panel, naming the selected category.</summary>
    public string PlacesTitle => SelectedCategory is { } selected
        ? string.Format(Strings.PlacesIn, selected.Name)
        : Strings.BiggestPlaces;

    public void ApplyProgress(ScanProgress progress)
    {
        if (!IsComplete)
        {
            Update(progress.CategoryBytes, progress.FileCount);
        }
    }

    public void ApplyResult(StorageTree tree)
    {
        _tree = tree;
        long[] bytes = Enumerable.Range(0, StorageCategories.Count)
            .Select(i => tree.GetCategoryBytes((StorageCategory)i))
            .ToArray();
        Update(bytes, tree.FileCount);
        foreach (CategoryRowViewModel row in Categories)
        {
            row.Areas = tree.GetCategoryAreas(row.Category!.Value)
                .Take(MaxAreasPerCategory)
                .Select(area => CreateArea(area, row.Bytes))
                .ToArray();
        }

        IsComplete = true;
        SelectedCategory ??= Categories.Where(row => row.Bytes > 0).MaxBy(row => row.Bytes);
    }

    [RelayCommand]
    private void OpenArea(CategoryAreaViewModel area) => _openFolder(area.Node);

    [RelayCommand(CanExecute = nameof(IsComplete))]
    private void BrowseAll() => _openFolder(_tree!.Root);

    private CategoryAreaViewModel CreateArea(CategoryArea area, long categoryBytes)
    {
        var description = _locations.Describe(area.Node.FullPath);
        double share = categoryBytes == 0 ? 0 : (double)area.Bytes / categoryBytes;
        return new CategoryAreaViewModel(area.Node, area.Bytes, description?.Title ?? area.Node.Name, description?.Description, share);
    }

    private void Update(IReadOnlyList<long> categoryBytes, long fileCount)
    {
        long scanned = categoryBytes.Sum();
        long unaccounted = Drive.UsedBytes - scanned;
        HasOvercount = unaccounted < 0;
        long whole = Math.Max(Math.Max(Drive.UsedBytes, scanned), 1);

        foreach (CategoryRowViewModel row in Categories)
        {
            row.SetBytes(categoryBytes[(int)row.Category!.Value], whole);
        }

        Unaccounted.SetBytes(Math.Max(unaccounted, 0), whole);
        FileCount = fileCount;
        Segments = Categories
            .Append(Unaccounted)
            .Where(row => row.Bytes > 0)
            .Select(row => new BarSegment(row.ColorKey, row.Fraction))
            .ToArray();
    }
}

public sealed partial class CategoryRowViewModel : ObservableObject
{
    /// <param name="category">Null for the "not measured yet" row.</param>
    public CategoryRowViewModel(StorageCategory? category)
    {
        Category = category;
        Name = category is { } c ? CategoryText.Name(c) : Strings.Category_Unaccounted;
        Hint = category is { } h ? CategoryText.Hint(h) : Strings.CategoryHint_Unaccounted;
        ColorKey = category is { } k ? CategoryText.ColorKey(k) : CategoryText.UnaccountedKey;
        Swatch = [new BarSegment(ColorKey, 1)];
    }

    public StorageCategory? Category { get; }

    public string Name { get; }

    public string Hint { get; }

    public string ColorKey { get; }

    /// <summary>A single full segment, used to draw the category's colour dot.</summary>
    public IReadOnlyList<BarSegment> Swatch { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    public partial long Bytes { get; private set; }

    /// <summary>Share of the drive's used space, 0 to 1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    public partial double Fraction { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<CategoryAreaViewModel> Areas { get; set; } = [];

    public string SizeText => ByteSize.Format(Bytes);

    public string PercentText => Fraction.ToString("P0");

    internal void SetBytes(long bytes, long whole)
    {
        Bytes = bytes;
        Fraction = (double)bytes / whole;
    }
}

/// <param name="Share">Share of its category's bytes, 0 to 1, for the small size bar.</param>
public sealed record CategoryAreaViewModel(StorageNode Node, long Bytes, string Title, string? Description, double Share = 0)
{
    public string Path => Node.FullPath;

    public string SizeText => ByteSize.Format(Bytes);
}
