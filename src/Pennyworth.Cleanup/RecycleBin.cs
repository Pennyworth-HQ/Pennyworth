using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace Pennyworth.Cleanup;

/// <summary>
/// The current user's Recycle Bin on every drive. Each deleted item is a pair in
/// <c>X:\$Recycle.Bin\&lt;user SID&gt;</c>: <c>$R…</c> holds the item and <c>$I…</c> records its
/// original path, size and deletion time.
/// </summary>
public static partial class RecycleBin
{
    [LibraryImport("shell32.dll")]
    private static partial int SHUpdateRecycleBinIcon();

    /// <summary>Every item in the user's Recycle Bins, largest first.</summary>
    public static IReadOnlyList<CleanupFile> List()
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null)
        {
            return [];
        }

        var items = new List<CleanupFile>();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            string bin = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
            try
            {
                if (!drive.IsReady || !Directory.Exists(bin))
                {
                    continue;
                }

                foreach (string info in Directory.EnumerateFiles(bin, "$I*"))
                {
                    string item = Path.Combine(bin, "$R" + Path.GetFileName(info)[2..]);
                    if ((File.Exists(item) || Directory.Exists(item)) && ParseInfo(File.ReadAllBytes(info)) is { } parsed)
                    {
                        items.Add(new CleanupFile(item, parsed.Size, parsed.DeletedUtc, parsed.OriginalPath, info));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another user's bin, or a drive that went away: skip it.
            }
        }

        return [.. items.OrderByDescending(i => i.Size)];
    }

    /// <summary>
    /// Reads a <c>$I</c> record: version 1 (Vista–8) keeps a fixed 260-character path,
    /// version 2 (Windows 10+) a length-prefixed one.
    /// </summary>
    public static (string OriginalPath, long Size, DateTime DeletedUtc)? ParseInfo(ReadOnlySpan<byte> data)
    {
        if (data.Length < 24)
        {
            return null;
        }

        long version = BitConverter.ToInt64(data[..8]);
        long size = BitConverter.ToInt64(data[8..16]);
        long fileTime = BitConverter.ToInt64(data[16..24]);
        string path;
        if (version == 2 && data.Length >= 28)
        {
            int chars = BitConverter.ToInt32(data[24..28]);
            if (chars <= 0 || 28 + (chars * 2) > data.Length)
            {
                return null;
            }

            path = Encoding.Unicode.GetString(data.Slice(28, chars * 2));
        }
        else if (version == 1)
        {
            path = Encoding.Unicode.GetString(data[24..Math.Min(data.Length, 24 + 520)]);
        }
        else
        {
            return null;
        }

        DateTime deleted = fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : DateTime.MinValue;
        return (path.TrimEnd('\0'), Math.Max(size, 0), deleted);
    }

    /// <summary>Removes one listed item and its record; false when it is no longer in the bin.</summary>
    internal static bool Delete(CleanupFile item)
    {
        if (item.CompanionPath is not { } info || !Path.GetFileName(item.Path).StartsWith("$R", StringComparison.Ordinal)
            || !item.Path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (Directory.Exists(item.Path))
            {
                // Recursive delete removes links themselves; it never follows them.
                Directory.Delete(item.Path, recursive: true);
            }
            else if (File.Exists(item.Path))
            {
                File.SetAttributes(item.Path, FileAttributes.Normal);
                File.Delete(item.Path);
            }
            else
            {
                return false; // Restored or emptied elsewhere since the preview.
            }

            File.Delete(info);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Lets Explorer redraw the Recycle Bin icon (full or empty).</summary>
    internal static void RefreshIcon() => SHUpdateRecycleBinIcon();
}
