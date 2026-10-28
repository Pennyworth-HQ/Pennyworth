using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Pennyworth.App.Services;

public interface IFilePicker
{
    /// <summary>The local path of the file the person chose, or null when they cancelled.</summary>
    Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns);
}

/// <summary>The window's open dialog; the window is looked up when needed because it is created later.</summary>
public sealed class AvaloniaFilePicker(Func<TopLevel?> topLevel) : IFilePicker
{
    public async Task<string?> PickFileAsync(string title, string filterName, IReadOnlyList<string> patterns)
    {
        if (topLevel()?.StorageProvider is not { CanOpen: true } storage)
        {
            return null;
        }

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}
