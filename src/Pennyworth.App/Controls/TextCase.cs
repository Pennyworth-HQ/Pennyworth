using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace Pennyworth.App.Controls;

/// <summary>
/// Upper-cases a fixed string for the design's monospace labels and buttons:
/// <c>{controls:Upper {x:Static res:Strings.Categories}}</c>. Avalonia has no CSS-style text-transform.
/// </summary>
public sealed class UpperExtension(string text) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        text.ToUpper(CultureInfo.CurrentUICulture);
}

public static class TextCase
{
    /// <summary>Upper-cases bound text, for labels whose text changes.</summary>
    public static readonly IValueConverter Upper =
        new FuncValueConverter<string?, string?>(text => text?.ToUpper(CultureInfo.CurrentUICulture));
}
