using System.Runtime.Versioning;

namespace Pennyworth.App.Services;

/// <summary>
/// One Pennyworth per sign-in. With the app hidden in the tray, opening it again from the Start menu
/// would otherwise start a second copy with a second tray icon; instead it wakes the first one,
/// which shows its window, and exits.
/// </summary>
internal static class SingleInstance
{
    /// <summary>Starts hidden in the tray: the start at sign-in.</summary>
    public const string BackgroundArgument = "--background";

    private const string ShowEventName = "Pennyworth.SingleInstance.Show";

    // Kept alive for the whole run: the name exists as long as this handle is open.
    private static EventWaitHandle? s_show;

    /// <summary>Runs on a background thread when another copy asked this one to show itself.</summary>
    public static Action? ShowRequested { get; set; }

    /// <summary>
    /// True when this is the first copy. Otherwise the first copy is asked to show its window,
    /// unless this one was started hidden (signing in while it already runs).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool TryClaim(string[] args)
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out bool createdNew);
        if (!createdNew)
        {
            if (!args.Contains(BackgroundArgument))
            {
                show.Set();
            }

            show.Dispose();
            return false;
        }

        s_show = show;
        var listener = new Thread(() =>
        {
            while (true)
            {
                s_show.WaitOne();
                ShowRequested?.Invoke();
            }
        })
        {
            IsBackground = true,
            Name = "Pennyworth single instance",
        };
        listener.Start();
        return true;
    }
}
