using Avalonia;

namespace Pennyworth.App;

internal static class Program
{
    // Don't use Avalonia types before AppMain runs: nothing is initialized yet.
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
