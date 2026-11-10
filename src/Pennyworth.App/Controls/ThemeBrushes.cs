using Avalonia.Controls;
using Avalonia.Media;

namespace Pennyworth.App.Controls;

/// <summary>Theme lookups for controls that draw themselves, so they follow light/dark.</summary>
internal static class ThemeBrushes
{
    public static IBrush Brush(this Control control, string key, double opacity = 1)
    {
        IBrush brush = control.TryFindResource(key, control.ActualThemeVariant, out object? value) && value is IBrush found ? found : Brushes.Gray;
        return opacity >= 1 || brush is not ISolidColorBrush solid ? brush : new SolidColorBrush(solid.Color, opacity);
    }

    public static FontFamily MonoFont(this Control control) =>
        control.TryFindResource("FontFamily.Mono", control.ActualThemeVariant, out object? value) && value is FontFamily family ? family : FontFamily.Default;
}
