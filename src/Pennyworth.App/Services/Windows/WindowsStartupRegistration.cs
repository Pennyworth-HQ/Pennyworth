using System.Runtime.Versioning;
using Microsoft.Win32;
using Pennyworth.Platform;

namespace Pennyworth.App.Services.Windows;

/// <summary>
/// Starts Pennyworth hidden in the tray at sign-in, through the current user's <c>Run</c> key:
/// no admin rights, and it shows up (and can be turned off) in Task Manager's Startup apps.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsStartupRegistration : IStartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Pennyworth";

    public bool IsEnabled
    {
        get
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && command.Contains(Executable(), StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            key.SetValue(ValueName, $"\"{Executable()}\" {SingleInstance.BackgroundArgument}");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static string Executable() => Environment.ProcessPath ?? throw new InvalidOperationException("No executable path.");
}
