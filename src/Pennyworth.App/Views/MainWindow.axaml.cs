using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Views;

public sealed partial class MainWindow : Window
{
    /// <summary>Below this width the sidebar is hidden so the content keeps its room.</summary>
    private const double NarrowWidth = 900;

    private MainWindowViewModel? _viewModel;

    public MainWindow() => InitializeComponent();

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
        if (e.PropertyName is nameof(MainWindowViewModel.IsDarkTheme) or "" or null)
        {
            ApplyTheme();
        }
    }

    // The theme is a view concern: the view model only says which one the user wants.
    private void ApplyTheme()
    {
        if (Application.Current is { } app && _viewModel?.IsDarkTheme is { } dark)
        {
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }
}
