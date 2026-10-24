using Pennyworth.App.ViewModels;
using Pennyworth.Core.Storage;
using static Pennyworth.App.Tests.SampleTrees;

namespace Pennyworth.App.Tests;

public class FolderBrowserViewModelTests
{
    [Fact]
    public void Lists_children_largest_first_with_share_of_parent()
    {
        var browser = new FolderBrowserViewModel(Build().Root, new FakeExplorer(), () => { });

        Assert.Equal(["Games", "Windows", "Users", "stray.bin"], browser.Items.Select(i => i.Name));
        Assert.Equal(50.0 / 85, browser.Items[0].Fraction, 3);
    }

    [Fact]
    public void Opens_folders_and_ignores_files()
    {
        var browser = new FolderBrowserViewModel(Build().Root, new FakeExplorer(), () => { });

        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "stray.bin"));
        Assert.Equal(@"C:\", browser.Current.FullPath);

        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "Users"));
        Assert.Equal(@"C:\Users", browser.Current.FullPath);
        Assert.Equal([@"C:\", "Users"], browser.Breadcrumbs.Select(b => b.Name));
    }

    [Fact]
    public void Goes_up_and_jumps_through_breadcrumbs()
    {
        var root = Build().Root;
        var bob = root.GetChildren().Single(n => n.Name == "Users").GetChildren().Single();
        var browser = new FolderBrowserViewModel(bob, new FakeExplorer(), () => { });

        browser.UpCommand.Execute(null);
        Assert.Equal(@"C:\Users", browser.Current.FullPath);

        browser.NavigateToCommand.Execute(browser.Breadcrumbs[0]);
        Assert.Equal(@"C:\", browser.Current.FullPath);
        Assert.False(browser.UpCommand.CanExecute(null));
    }

    [Fact]
    public void Titles_the_page_with_the_current_folder()
    {
        var browser = new FolderBrowserViewModel(Build().Root, new FakeExplorer(), () => { });
        Assert.Equal(@"C:\", browser.Title);

        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "Games"));

        Assert.Equal("Games", browser.Title);
    }

    [Fact]
    public void Tells_locked_folders_apart_from_empty_ones()
    {
        var b = new StorageTreeBuilder(@"D:\", StorageCategory.Other);
        int locked = b.AddDirectory(StorageTreeBuilder.RootIndex, "Locked", StorageCategory.Other);
        b.MarkLocked(locked);
        b.AddDirectory(StorageTreeBuilder.RootIndex, "Empty", StorageCategory.Other);
        var browser = new FolderBrowserViewModel(b.Build().Root, new FakeExplorer(), () => { });

        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "Locked"));
        Assert.True(browser.IsLockedFolder);
        Assert.False(browser.IsEmpty);

        browser.UpCommand.Execute(null);
        browser.OpenCommand.Execute(browser.Items.Single(i => i.Name == "Empty"));
        Assert.False(browser.IsLockedFolder);
        Assert.True(browser.IsEmpty);
    }

    [Fact]
    public void Shows_items_in_explorer_and_goes_back()
    {
        var explorer = new FakeExplorer();
        bool wentBack = false;
        var browser = new FolderBrowserViewModel(Build().Root, explorer, () => wentBack = true);

        browser.ShowInExplorerCommand.Execute(browser.Items[0]);
        browser.BackCommand.Execute(null);

        Assert.Equal([@"C:\Games"], explorer.Shown);
        Assert.True(wentBack);
    }
}
