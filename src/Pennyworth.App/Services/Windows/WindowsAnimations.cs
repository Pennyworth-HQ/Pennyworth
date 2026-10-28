using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pennyworth.App.Services.Windows;

/// <summary>"Animation effects" in Windows' accessibility settings.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsAnimations
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    /// <summary>True when the person turned animations off; unknown counts as on.</summary>
    public static bool AreOff() => SystemParametersInfoW(SpiGetClientAreaAnimation, 0, out int enabled, 0) && enabled == 0;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SystemParametersInfoW(uint action, uint param, out int value, uint winIni);
}
