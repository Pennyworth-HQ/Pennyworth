namespace Pennyworth.Platform;

public sealed record DriveSummary(string RootPath, string Label, long TotalBytes, long FreeBytes)
{
    public long UsedBytes => TotalBytes - FreeBytes;
}

public interface IDriveService
{
    IReadOnlyList<DriveSummary> GetDrives();

    string SystemDriveRoot { get; }
}
