using System.Runtime.Versioning;
using Pennyworth.Platform;

namespace Pennyworth.App.Services.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsDriveService : IDriveService
{
    public string SystemDriveRoot => Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    public IReadOnlyList<DriveSummary> GetDrives()
    {
        var drives = new List<DriveSummary>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            try
            {
                if (drive.IsReady)
                {
                    drives.Add(new DriveSummary(drive.RootDirectory.FullName, drive.VolumeLabel, drive.TotalSize, drive.TotalFreeSpace));
                }
            }
            catch (IOException)
            {
                // Drive removed or not readable right now; skip it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return drives;
    }
}
