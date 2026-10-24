using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Pennyworth.Scanning;

/// <summary>Hidden system space only an administrator can measure. Null = couldn't be measured.</summary>
/// <param name="RestorePoints">Volume shadow copy storage (restore points).</param>
/// <param name="NtfsMetadata">The NTFS master file table ($MFT).</param>
public sealed record HiddenSpaceReport(long? RestorePoints, long? NtfsMetadata)
{
    public long Total => (RestorePoints ?? 0) + (NtfsMetadata ?? 0);
}

/// <summary>
/// Runs in the short-lived elevated copy of the app (<c>--measure-hidden C: result.json</c>).
/// Reads only sizes; it never changes anything.
/// </summary>
public static partial class HiddenSpace
{
    public const string Argument = "--measure-hidden";

    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint FsctlGetNtfsVolumeData = 0x00090064;
    private const int MftValidDataLengthOffset = 56;

    /// <summary>"C:" style drive letters only, so nothing else ever reaches the command line.</summary>
    public static bool IsDriveLetter(string drive) => DriveLetter().IsMatch(drive);

    public static HiddenSpaceReport Measure(string drive)
    {
        if (!IsDriveLetter(drive))
        {
            throw new ArgumentException($"'{drive}' is not a drive letter like C:.", nameof(drive));
        }

        return new HiddenSpaceReport(MeasureRestorePoints(drive), MeasureNtfsMetadata(drive));
    }

    public static void WriteReport(HiddenSpaceReport report, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(report, HiddenSpaceJsonContext.Default.HiddenSpaceReport));

    public static HiddenSpaceReport? ReadReport(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path), HiddenSpaceJsonContext.Default.HiddenSpaceReport);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Needs admin rights: opens the volume itself.</summary>
    internal static unsafe long? MeasureNtfsMetadata(string drive)
    {
        nint volume = CreateFile($@"\\.\{drive}", GenericRead, FileShareReadWrite, 0, OpenExisting, 0, 0);
        if (volume == -1)
        {
            return null;
        }

        try
        {
            byte* data = stackalloc byte[128];
            return DeviceIoControl(volume, FsctlGetNtfsVolumeData, null, 0, data, 128, out _, 0)
                ? *(long*)(data + MftValidDataLengthOffset)
                : null;
        }
        finally
        {
            CloseHandle(volume);
        }
    }

    /// <summary>Shadow storage used on the drive, through Windows' own management interface (CIM).</summary>
    private static long? MeasureRestorePoints(string drive)
    {
        string script =
            "$ErrorActionPreference='Stop';" +
            $"$v=Get-CimInstance Win32_Volume -Filter \"DriveLetter='{drive}'\";" +
            "$t=[long]0;" +
            "Get-CimInstance Win32_ShadowStorage | ForEach-Object { if ($_.Volume.DeviceID -eq $v.DeviceID) { $t += [long]$_.UsedSpace } };" +
            "[Console]::Out.Write($t)";
        try
        {
            using var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(30_000) || process.ExitCode != 0)
            {
                return null;
            }

            return long.TryParse(output.Trim(), out long bytes) ? bytes : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [GeneratedRegex("^[A-Za-z]:$")]
    private static partial Regex DriveLetter();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(nint device, uint code, void* input, int inputSize, void* output, int outputSize, out int returned, nint overlapped);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

[JsonSerializable(typeof(HiddenSpaceReport))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class HiddenSpaceJsonContext : JsonSerializerContext;
