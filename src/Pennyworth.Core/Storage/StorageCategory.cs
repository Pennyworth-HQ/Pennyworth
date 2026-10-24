namespace Pennyworth.Core.Storage;

/// <summary>Every scanned byte belongs to exactly one category.</summary>
public enum StorageCategory : byte
{
    Other = 0,
    Games = 1,
    Apps = 2,
    YourFiles = 3,
    Windows = 4,
    HiddenSystem = 5,
}

public static class StorageCategories
{
    /// <summary>Display order used by the summary bar.</summary>
    public static readonly IReadOnlyList<StorageCategory> All =
    [
        StorageCategory.Games,
        StorageCategory.Apps,
        StorageCategory.YourFiles,
        StorageCategory.Windows,
        StorageCategory.HiddenSystem,
        StorageCategory.Other,
    ];

    public const int Count = 6;
}
