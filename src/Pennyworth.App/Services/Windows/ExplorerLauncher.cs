using System.Diagnostics;
using System.Runtime.Versioning;
using Pennyworth.Platform;

namespace Pennyworth.App.Services.Windows;

[SupportedOSPlatform("windows")]
public sealed class ExplorerLauncher : IExplorerLauncher
{
    public void ShowInExplorer(string path)
    {
        bool isDriveRoot = Path.GetPathRoot(path) == path;
        string arguments = isDriveRoot ? $"\"{path}\"" : $"/select,\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }
}
