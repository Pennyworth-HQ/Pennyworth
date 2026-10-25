using System.Globalization;
using Avalonia.Data.Converters;

namespace Pennyworth.App.Controls;

/// <summary>
/// Upper-cases bound text in the current language, for the design's monospace labels.
/// Fixed labels use <c>{l:Tr Key, Upper=True}</c> instead. Avalonia has no CSS-style text-transform.
/// </summary>
public static class TextCase
{
    public static readonly IValueConverter Upper =
        new FuncValueConverter<string?, string?>(text => text?.ToUpper(CultureInfo.CurrentCulture));
}
