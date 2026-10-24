using Pennyworth.App.Resources;
using Pennyworth.Core.Storage;

namespace Pennyworth.App.ViewModels;

internal static class CategoryText
{
    /// <summary>Resource key suffix for the bar colour, e.g. <c>Brush.Category.Games</c>.</summary>
    public const string UnaccountedKey = "Unaccounted";

    public static string Name(StorageCategory category) => category switch
    {
        StorageCategory.Games => Strings.Category_Games,
        StorageCategory.Apps => Strings.Category_Apps,
        StorageCategory.YourFiles => Strings.Category_YourFiles,
        StorageCategory.Windows => Strings.Category_Windows,
        StorageCategory.HiddenSystem => Strings.Category_HiddenSystem,
        _ => Strings.Category_Other,
    };

    public static string ColorKey(StorageCategory category) => category.ToString();
}
