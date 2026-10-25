using System.Globalization;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Cleanup;
using Pennyworth.Core.Formatting;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>
/// Everything one cleanup item would delete, before anything is deleted: its folders and every
/// file (Recycle Bin: every item with where it came from), largest first, searchable by name.
/// </summary>
public sealed partial class CleanupPreviewViewModel : ObservableObject
{
    private readonly CleanupViewModel _cleanup;
    private readonly IExplorerLauncher? _explorer;
    private readonly CleanupFileRow[] _all;

    public CleanupPreviewViewModel(CleanupItemViewModel item, CleanupViewModel cleanup, IExplorerLauncher? explorer)
    {
        Item = item;
        _cleanup = cleanup;
        _explorer = explorer;
        string[] roots = [.. (item.Estimate.Folders ?? []).Select(folder => folder.Path)];
        _all = [.. (item.Estimate.Files ?? []).OrderByDescending(file => file.Size).Select(file => new CleanupFileRow(file, roots))];
        Folders = [.. (item.Estimate.Folders ?? []).OrderByDescending(folder => folder.Bytes).Select(folder => new CleanupFolderRow(folder))];
        Files = _all;
    }

    public CleanupItemViewModel Item { get; }

    public IReadOnlyList<CleanupFolderRow> Folders { get; }

    public bool HasFolders => Folders.Count > 0;

    /// <summary>The files whose name matches <see cref="Search"/>, largest first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchText), nameof(HasNoMatch))]
    public partial IReadOnlyList<CleanupFileRow> Files { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchText))]
    public partial string Search { get; set; } = "";

    /// <summary>"18.2 GB in 1,204 files. Only these will be deleted; anything newer stays."</summary>
    public string Summary => string.Format(Item.IsRecycleBin ? Strings.PreviewSubBin : Strings.PreviewSub, Item.SizeText, Item.Items);

    public string ListLabel => Item.IsRecycleBin ? Strings.PreviewItemsBin : Strings.PreviewFiles;

    public string MatchText => Search.Length == 0 ? Item.CountText : string.Format(Strings.PreviewMatches, Files.Count);

    public bool HasNoMatch => Files.Count == 0;

    public string NoMatchText => _all.Length == 0 ? Strings.PreviewNothing : Strings.PreviewNoMatch;

    partial void OnSearchChanged(string value)
    {
        string term = value.Trim();
        Files = term.Length == 0 ? _all : [.. _all.Where(row => row.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase))];
    }

    [RelayCommand]
    private void Back() => _cleanup.ClosePreview();

    /// <summary>The same tick as on the list: whether this item is part of the cleanup.</summary>
    [RelayCommand]
    private void ToggleInclude() => _cleanup.ToggleCommand.Execute(Item);

    [RelayCommand]
    private void ShowInExplorer(string path) => _explorer?.ShowInExplorer(path);

    internal void Refresh()
    {
        foreach (CleanupFileRow row in _all)
        {
            row.Refresh();
        }

        foreach (CleanupFolderRow folder in Folders)
        {
            folder.Refresh();
        }

        OnPropertyChanged(string.Empty);
    }
}

/// <summary>A folder the item cleans inside, with what is in it now.</summary>
public sealed partial class CleanupFolderRow(CleanupFolder folder) : ObservableObject
{
    public string Path => folder.Path;

    public string Detail => string.Format(Strings.PreviewFolderLine, ByteSize.Format(folder.Bytes), folder.Files);

    internal void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>One file that will be deleted; for the Recycle Bin, one deleted item and where it was.</summary>
public sealed partial class CleanupFileRow(CleanupFile file, IReadOnlyList<string> roots) : ObservableObject
{
    public string Path => file.Path;

    public string Name => System.IO.Path.GetFileName(file.OriginalPath ?? file.Path);

    /// <summary>
    /// The folder it is in, starting from the cleaned folder ("Temp\jnb0mdtr"); Recycle Bin: the
    /// full folder it was deleted from.
    /// </summary>
    public string Folder => file.OriginalPath is { } original
        ? System.IO.Path.GetDirectoryName(original) ?? ""
        : FromRoot(System.IO.Path.GetDirectoryName(file.Path) ?? "");

    public string? Deleted => file.OriginalPath is null || file.ChangedUtc == DateTime.MinValue
        ? null
        : string.Format(Strings.PreviewDeletedOn, file.ChangedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture));

    /// <summary>"Temp\jnb0mdtr" (Recycle Bin: "C:\Users\Bob · deleted 10/1/2026 9:00").</summary>
    public string Location => Deleted is null ? Folder : $"{Folder} · {Deleted}";

    /// <summary>Recycle Bin items have no place in Explorer worth showing.</summary>
    public bool CanShow => file.OriginalPath is null;

    public string SizeText => ByteSize.Format(file.Size);

    internal void Refresh() => OnPropertyChanged(string.Empty);

    private string FromRoot(string folder)
    {
        foreach (string root in roots)
        {
            string trimmed = System.IO.Path.TrimEndingDirectorySeparator(root);
            if ((folder.Equals(trimmed, StringComparison.OrdinalIgnoreCase)
                 || folder.StartsWith(trimmed + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                && System.IO.Path.GetDirectoryName(trimmed) is { } parent)
            {
                return System.IO.Path.GetRelativePath(parent, folder);
            }
        }

        return folder;
    }
}
