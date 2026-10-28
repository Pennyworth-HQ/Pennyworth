using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Pennyworth.Platform;

namespace Pennyworth.App.Services.Windows;

/// <summary>
/// The tray icon and its notifications, straight from <c>Shell_NotifyIcon</c>: Avalonia's tray
/// icon cannot show notifications, and the WinRT toast API would tie the whole app to a Windows SDK
/// target. Windows 10 and 11 turn these "balloons" into ordinary notifications in the Action Center.
/// Lives on the UI thread: its hidden window receives its messages through Avalonia's message loop.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class WindowsTray : ITray
{
    private const string ClassName = "PennyworthTray";
    private const uint IconId = 1;
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1

    // Only one tray icon per process; the window procedure finds it here.
    private static WindowsTray? s_current;

    private readonly nint _window;
    private readonly nint _smallIcon;
    private readonly nint _largeIcon;
    private readonly uint _taskbarCreated;
    private string _tooltip = "";
    private IReadOnlyList<TrayMenuItem> _menu = [];
    private Action? _open;
    private Action? _notificationClick;
    private bool _added;

    public WindowsTray()
    {
        if (s_current is not null)
        {
            throw new InvalidOperationException("Only one tray icon is supported.");
        }

        nint instance = Native.GetModuleHandleW(null);
        fixed (char* className = ClassName)
        {
            var windowClass = new Native.WndClassEx
            {
                Size = (uint)sizeof(Native.WndClassEx),
                WndProc = &WndProc,
                Instance = instance,
                ClassName = className,
            };
            Native.RegisterClassExW(&windowClass); // Fails harmlessly when already registered.

            // A top-level window that is never shown: a message-only window cannot take the
            // foreground, and without that the tray menu does not close when clicking elsewhere.
            _window = Native.CreateWindowExW(0, className, className, 0, 0, 0, 0, 0, 0, 0, instance, 0);
        }

        if (_window == 0)
        {
            throw new InvalidOperationException($"Could not create the tray window (error {Marshal.GetLastPInvokeError()}).");
        }

        s_current = this;
        _taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");

        nint large = 0, small = 0;
        if (Environment.ProcessPath is { } exe)
        {
            Native.ExtractIconExW(exe, 0, &large, &small, 1);
        }

        _largeIcon = large;
        _smallIcon = small != 0 ? small : Native.LoadIconW(0, Native.IdiApplication);
    }

    public void Show(string tooltip, IReadOnlyList<TrayMenuItem> menu, Action open)
    {
        _tooltip = tooltip;
        _menu = menu;
        _open = open;
        if (!_added)
        {
            Add();
        }
        else
        {
            Native.NotifyIconData data = NewData(Native.NifTip | Native.NifShowTip);
            Copy(_tooltip, data.Tip, 128);
            Native.Shell_NotifyIconW(Native.NimModify, &data);
        }
    }

    public void Notify(string title, string message, Action? onClick = null)
    {
        if (!_added)
        {
            return;
        }

        _notificationClick = onClick;
        Native.NotifyIconData data = NewData(Native.NifInfo);
        Copy(title, data.InfoTitle, 64);
        Copy(message, data.Info, 256);
        data.InfoFlags = _largeIcon != 0 ? Native.NiifUser | Native.NiifLargeIcon : Native.NiifInfo;
        data.BalloonIcon = _largeIcon;
        if (!Native.Shell_NotifyIconW(Native.NimModify, &data))
        {
            Debug.WriteLine("Shell_NotifyIcon refused the notification.");
        }
    }

    public void Dispose()
    {
        if (s_current != this)
        {
            return;
        }

        if (_added)
        {
            Native.NotifyIconData data = NewData(0);
            Native.Shell_NotifyIconW(Native.NimDelete, &data);
            _added = false;
        }

        Native.DestroyWindow(_window);
        if (_largeIcon != 0)
        {
            Native.DestroyIcon(_largeIcon);
        }

        s_current = null;
    }

    private void Add()
    {
        Native.NotifyIconData data = NewData(Native.NifMessage | Native.NifIcon | Native.NifTip | Native.NifShowTip);
        data.CallbackMessage = CallbackMessage;
        data.Icon = _smallIcon;
        Copy(_tooltip, data.Tip, 128);
        _added = Native.Shell_NotifyIconW(Native.NimAdd, &data);
        if (_added)
        {
            data.Version = Native.NotifyIconVersion4;
            Native.Shell_NotifyIconW(Native.NimSetVersion, &data);
        }
    }

    private Native.NotifyIconData NewData(uint flags) => new()
    {
        Size = (uint)sizeof(Native.NotifyIconData),
        Window = _window,
        Id = IconId,
        Flags = flags,
    };

    private void ShowMenu()
    {
        if (_menu.Count == 0)
        {
            return;
        }

        nint menu = Native.CreatePopupMenu();
        try
        {
            for (int i = 0; i < _menu.Count; i++)
            {
                uint flags = Native.MfString | (_menu[i].IsChecked ? Native.MfChecked : 0);
                Native.AppendMenuW(menu, flags, (nuint)(i + 1), _menu[i].Text);
            }

            Native.GetCursorPos(out Native.Point cursor);
            Native.SetForegroundWindow(_window);
            int chosen = Native.TrackPopupMenuEx(menu, Native.TpmReturnCmd | Native.TpmRightButton, cursor.X, cursor.Y, _window, 0);
            Native.PostMessageW(_window, 0, 0, 0); // WM_NULL: lets the menu close properly next time.
            if (chosen > 0 && chosen <= _menu.Count)
            {
                _menu[chosen - 1].Invoke();
            }
        }
        finally
        {
            Native.DestroyMenu(menu);
        }
    }

    private nint HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (message == CallbackMessage)
        {
            switch ((uint)(lParam & 0xFFFF))
            {
                case Native.NinSelect:
                case Native.NinKeySelect:
                    _open?.Invoke();
                    break;
                case Native.WmContextMenu:
                    ShowMenu();
                    break;
                case Native.NinBalloonUserClick:
                    (_notificationClick ?? _open)?.Invoke();
                    _notificationClick = null;
                    break;
            }

            return 0;
        }

        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            // Explorer restarted and forgot every tray icon.
            _added = false;
            Add();
            return 0;
        }

        return Native.DefWindowProcW(_window, message, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WndProc(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (s_current is { } tray && tray._window == window)
            {
                return tray.HandleMessage(message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            // Nothing may escape into native code: it would end the process.
            Debug.WriteLine(ex);
            return 0;
        }

        return Native.DefWindowProcW(window, message, wParam, lParam);
    }

    private static void Copy(string text, char* destination, int capacity)
    {
        int length = Math.Min(text.Length, capacity - 1);
        text.AsSpan(0, length).CopyTo(new Span<char>(destination, capacity));
        destination[length] = '\0';
    }

    private static partial class Native
    {
        public const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
        public const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
        public const uint NiifInfo = 0x1, NiifUser = 0x4, NiifLargeIcon = 0x20;
        public const uint NotifyIconVersion4 = 4;
        public const uint NinSelect = 0x400, NinKeySelect = 0x401, NinBalloonUserClick = 0x405;
        public const uint WmContextMenu = 0x7B;
        public const uint MfString = 0x0, MfChecked = 0x8;
        public const uint TpmRightButton = 0x2, TpmReturnCmd = 0x100;
        public const nint IdiApplication = 32512;

        [StructLayout(LayoutKind.Sequential)]
        public struct WndClassEx
        {
            public uint Size;
            public uint Style;
            public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint> WndProc;
            public int ClassExtra;
            public int WindowExtra;
            public nint Instance;
            public nint Icon;
            public nint Cursor;
            public nint Background;
            public char* MenuName;
            public char* ClassName;
            public nint SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NotifyIconData
        {
            public uint Size;
            public nint Window;
            public uint Id;
            public uint Flags;
            public uint CallbackMessage;
            public nint Icon;
            public fixed char Tip[128];
            public uint State;
            public uint StateMask;
            public fixed char Info[256];
            public uint Version; // Also the old timeout.
            public fixed char InfoTitle[64];
            public uint InfoFlags;
            public Guid Item;
            public nint BalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint GetModuleHandleW(string? name);

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial ushort RegisterClassExW(WndClassEx* windowClass);

        [LibraryImport("user32.dll", SetLastError = true)]
        public static partial nint CreateWindowExW(uint exStyle, char* className, char* windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyWindow(nint window);

        [LibraryImport("user32.dll")]
        public static partial nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial uint RegisterWindowMessageW(string name);

        [LibraryImport("shell32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool Shell_NotifyIconW(uint message, NotifyIconData* data);

        [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
        public static partial uint ExtractIconExW(string file, int index, nint* large, nint* small, uint count);

        [LibraryImport("user32.dll")]
        public static partial nint LoadIconW(nint instance, nint name);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyIcon(nint icon);

        [LibraryImport("user32.dll")]
        public static partial nint CreatePopupMenu();

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool AppendMenuW(nint menu, uint flags, nuint id, string text);

        [LibraryImport("user32.dll")]
        public static partial int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool DestroyMenu(nint menu);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool GetCursorPos(out Point point);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetForegroundWindow(nint window);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
    }
}
