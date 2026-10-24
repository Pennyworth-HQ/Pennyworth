using Pennyworth.App.Services;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>Drill-down through folders, largest first.</summary>
public sealed partial class FolderBrowserViewModel : ObservableObject
{
    private readonly IExplorerLauncher _explorer;
    private readonly Action _back;

    public FolderBrowserViewModel(StorageNode start, IExplorerLauncher explorer, Action back)
    {
        _explorer = explorer;
        _back = back;
        Current = start;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Items), nameof(Breadcrumbs), nameof(Title), nameof(SizeText), nameof(IsEmpty), nameof(IsLockedFolder), nameof(HasItems))]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    public partial StorageNode Current { get; private set; }

    public IReadOnlyList<FolderItemViewModel> Items =>
        Current.GetChildren().Select(child => new FolderItemViewModel(child, Current.SizeOnDisk)).ToArray();

    public IReadOnlyList<BreadcrumbViewModel> Breadcrumbs
    {
        get
        {
            var crumbs = new List<BreadcrumbViewModel>();
            for (StorageNode? node = Current; node is { } current; node = current.Parent)
            {
                crumbs.Insert(0, new BreadcrumbViewModel(current));
            }

            return crumbs;
        }
    }

    public string Title => Current.Name;

    public string SizeText => ByteSize.Format(Current.SizeOnDisk);

    /// <summary>Nothing inside, and not because Windows refused to list it.</summary>
    public bool IsEmpty => !Current.HasChildren && !Current.IsLocked;

    public bool IsLockedFolder => Current.IsLocked;

    public bool HasItems => Current.HasChildren;

    [RelayCommand]
    private void Open(FolderItemViewModel item)
    {
        if (item.Node.IsDirectory)
        {
            Current = item.Node;
        }
    }

    [RelayCommand]
    private void NavigateTo(BreadcrumbViewModel crumb) => Current = crumb.Node;

    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void Up() => Current = Current.Parent!.Value;

    [RelayCommand]
    private void ShowInExplorer(FolderItemViewModel item) => _explorer.ShowInExplorer(item.Node.FullPath);

    [RelayCommand]
    private void Back() => _back();

    private bool CanGoUp() => Current.Parent is not null;
}

public sealed record FolderItemViewModel(StorageNode Node, long ParentSize)
{
    public string Name => Node.Name;

    public bool IsDirectory => Node.IsDirectory;

    public bool IsLocked => Node.IsLocked;

    public string SizeText => ByteSize.Format(Node.SizeOnDisk);

    /// <summary>Share of the parent folder, 0 to 1, for the size bar.</summary>
    public double Fraction => ParentSize == 0 ? 0 : (double)Node.SizeOnDisk / ParentSize;
}

public sealed record BreadcrumbViewModel(StorageNode Node)
{
    public string Name => Node.Name;
}
