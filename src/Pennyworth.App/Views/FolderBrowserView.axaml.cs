using Avalonia.Controls;
using Avalonia.Input;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Views;

public sealed partial class FolderBrowserView : UserControl
{
    public FolderBrowserView()
    {
        InitializeComponent();
        FolderList.KeyDown += OnFolderListKeyDown;
    }

    private void OnFolderListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space
            && DataContext is FolderBrowserViewModel browser
            && FolderList.SelectedItem is FolderItemViewModel item
            && item.CanOpen)
        {
            browser.OpenCommand.Execute(item);
            e.Handled = true;
        }
    }
}
