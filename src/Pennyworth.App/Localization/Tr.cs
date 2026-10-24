using Avalonia;
using Avalonia.Markup.Xaml;

namespace Pennyworth.App.Localization;

/// <summary>
/// A localized string that follows the language switch: <c>{l:Tr StorageTitle}</c>, or
/// <c>{l:Tr Categories, Upper=True}</c> for the design's upper-case monospace labels.
/// </summary>
public sealed class Tr(string key) : MarkupExtension
{
    public bool Upper { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        Localizer.Instance.Observe(key, Upper).ToBinding();
}
