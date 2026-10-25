using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace Pennyworth.App.Services;

public interface IClipboardService
{
    Task SetTextAsync(string text);
}

/// <summary>The window's clipboard; the window is looked up when needed because it is created later.</summary>
public sealed class AvaloniaClipboardService(Func<TopLevel?> topLevel) : IClipboardService
{
    public Task SetTextAsync(string text) =>
        topLevel()?.Clipboard is { } clipboard ? clipboard.SetTextAsync(text) : Task.CompletedTask;
}
