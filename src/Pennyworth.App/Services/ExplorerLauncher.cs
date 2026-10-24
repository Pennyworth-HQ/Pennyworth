using System.Diagnostics;

namespace Pennyworth.App.Services;

public interface IExplorerLauncher
{
    void ShowInExplorer(string path);
}

public sealed class ExplorerLauncher : IExplorerLauncher
{
    public void ShowInExplorer(string path)
    {
        bool isDriveRoot = Path.GetPathRoot(path) == path;
        string arguments = isDriveRoot ? $"\"{path}\"" : $"/select,\"{path}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
    }
}
