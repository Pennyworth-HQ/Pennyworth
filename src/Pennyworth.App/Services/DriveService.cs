namespace Pennyworth.App.Services;

public sealed record DriveSummary(string RootPath, string Label, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
}

public interface IDriveService
{
    IReadOnlyList<DriveSummary> GetDrives();

    string SystemDriveRoot { get; }
}

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
