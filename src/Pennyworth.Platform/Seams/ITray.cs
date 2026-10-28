namespace Pennyworth.Platform;

/// <summary>Shows a notification from the operating system, outside the window.</summary>
public interface INotifier
{
    /// <param name="onClick">Runs on the UI thread when the person clicks the notification.</param>
    void Notify(string title, string message, Action? onClick = null);
}

/// <summary>One entry of the tray icon's menu.</summary>
public sealed record TrayMenuItem(string Text, Action Invoke, bool IsChecked = false);

/// <summary>
/// The icon next to the clock that keeps Pennyworth reachable while its window is hidden. It
/// also delivers notifications, which on Windows belong to a tray icon.
/// </summary>
public interface ITray : INotifier, IDisposable
{
    /// <summary>Adds the icon, or updates its tooltip and menu. <paramref name="open"/> runs on a click on the icon.</summary>
    void Show(string tooltip, IReadOnlyList<TrayMenuItem> menu, Action open);
}

/// <summary>Starting Pennyworth, hidden in the tray, when the person signs in.</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
