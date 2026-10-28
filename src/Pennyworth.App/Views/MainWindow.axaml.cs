using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Views;

public sealed partial class MainWindow : Window
{
    /// <summary>Below this width the sidebar is hidden so the content keeps its room.</summary>
    private const double NarrowWidth = 900;

    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DragDrop.AddDragOverHandler(this, OnDragOver);
        DragDrop.AddDropHandler(this, OnDrop);
        if (Application.Current is { } app)
        {
            // While the choice is System, a change in Windows' theme is followed live.
            app.ActualThemeVariantChanged += (_, _) => _viewModel?.SyncSystemTheme(app.ActualThemeVariant == ThemeVariant.Dark);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null && Application.Current is { } app)
        {
            _viewModel.SyncSystemTheme(app.ActualThemeVariant == ThemeVariant.Dark);
            _viewModel.PropertyChanged += OnViewModelChanged;
            ApplyTheme();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
        {
            Classes.Set("narrow", ClientSize.Width < NarrowWidth);
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsDarkTheme) or nameof(MainWindowViewModel.Theme) or "" or null)
        {
            ApplyTheme();
        }
    }

    // A pet file dropped anywhere on the window is added, like "Add a pet…".
    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = _viewModel?.Pet.CanAddPets == true && e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_viewModel?.Pet is { CanAddPets: true } pet
            && e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null) is { } path)
        {
            _ = pet.ImportAsync(path);
        }
    }

    // The theme is a view concern: the view model only says which one the user wants.
    private void ApplyTheme()
    {
        if (Application.Current is not { } app || _viewModel is null)
        {
            return;
        }

        if (_viewModel.Theme == ThemePreference.System)
        {
            app.RequestedThemeVariant = ThemeVariant.Default;
            _viewModel.SyncSystemTheme(app.ActualThemeVariant == ThemeVariant.Dark);
        }
        else if (_viewModel.IsDarkTheme is { } dark)
        {
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }
}
