using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace Pennyworth.App.Views;

public sealed partial class MainWindow : Window
{
    /// <summary>Below this width the sidebar is hidden so the content keeps its room.</summary>
    private const double NarrowWidth = 900;

    public MainWindow() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
        {
            Classes.Set("narrow", ClientSize.Width < NarrowWidth);
        }
    }

    // Theme is a view concern only: it follows Windows until the user picks one here.
    private void OnToggleTheme(object? sender, RoutedEventArgs e)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = app.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        }
    }
}
