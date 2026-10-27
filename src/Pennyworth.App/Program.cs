using Avalonia;
using Pennyworth.App.Services.Windows;

namespace Pennyworth.App;

internal static class Program
{
    // Don't use Avalonia types before AppMain runs: nothing is initialized yet.
    [STAThread]
    public static int Main(string[] args)
    {
        // The short-lived elevated copy started by "Unlock full scan": measure, write, exit. No window.
        if (OperatingSystem.IsWindows() && ElevatedHiddenSpaceService.TryRunHelper(args, out int exitCode))
        {
            return exitCode;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
