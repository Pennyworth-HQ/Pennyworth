using Avalonia;
using Pennyworth.Scanning;

namespace Pennyworth.App;

internal static class Program
{
    // Don't use Avalonia types before AppMain runs: nothing is initialized yet.
    [STAThread]
    public static int Main(string[] args)
    {
        // The short-lived elevated copy started by "Unlock full scan": measure, write, exit. No window.
        if (args is [HiddenSpace.Argument, string drive, string reportPath])
        {
            if (!HiddenSpace.IsDriveLetter(drive))
            {
                return 2;
            }

            HiddenSpace.WriteReport(HiddenSpace.Measure(drive), reportPath);
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect();
}
