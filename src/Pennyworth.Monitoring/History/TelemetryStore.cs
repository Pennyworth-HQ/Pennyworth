using System.Globalization;
using System.Text;

namespace Pennyworth.Monitoring.History;

/// <summary>
/// The usage history on disk: one tab-separated line per minute, one file per UTC day, and
/// nothing older than <see cref="KeepDays"/>. It stays on this PC; nothing reads it but the app.
/// </summary>
public sealed class TelemetryStore(string folder)
{
    public const int KeepDays = 7;

    private const string Extension = ".tsv";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pennyworth", "telemetry");

    public string Folder { get; } = folder;

    public void Append(TelemetryPoint point)
    {
        Directory.CreateDirectory(Folder);
        File.AppendAllText(FileOf(point.At), Format(point) + "\n", Encoding.UTF8);
    }

    /// <summary>Every minute recorded at or after <paramref name="since"/>, oldest first.</summary>
    public List<TelemetryPoint> Load(DateTimeOffset since)
    {
        var points = new List<TelemetryPoint>();
        if (!Directory.Exists(Folder))
        {
            return points;
        }

        DateOnly firstDay = DateOnly.FromDateTime(since.UtcDateTime);
        foreach (string file in Directory.EnumerateFiles(Folder, "*" + Extension).Order(StringComparer.Ordinal))
        {
            if (!DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", Invariant, DateTimeStyles.None, out DateOnly day) || day < firstDay)
            {
                continue;
            }

            foreach (string line in ReadLines(file))
            {
                if (Parse(line) is { } point && point.At >= since)
                {
                    points.Add(point);
                }
            }
        }

        points.Sort((a, b) => a.At.CompareTo(b.At));
        return points;
    }

    /// <summary>Deletes the days that fell out of the history.</summary>
    public void Prune(DateTimeOffset now)
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        DateOnly oldest = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-KeepDays);
        foreach (string file in Directory.EnumerateFiles(Folder, "*" + Extension))
        {
            if (DateOnly.TryParseExact(Path.GetFileNameWithoutExtension(file), "yyyy-MM-dd", Invariant, DateTimeStyles.None, out DateOnly day) && day < oldest)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Tried again at the next start.
                }
            }
        }
    }

    /// <summary>"2026-10-27T14:32:00Z  12.5  98  61  63  4  100      chrome  Code" (empty = unavailable).</summary>
    public static string Format(TelemetryPoint point)
    {
        static string Number(double value) => value.ToString("0.#", Invariant);
        static string Pair(Reading? reading) => reading is { } r ? $"{Number(r.Average)}\t{Number(r.Peak)}" : "\t";
        static string Text(string? value) => value is null ? "" : value.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');

        return string.Join('\t',
            point.At.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", Invariant),
            Pair(point.Cpu),
            Pair(point.Memory),
            Pair(point.Disk),
            Pair(point.Gpu),
            Text(point.TopCpuApp),
            Text(point.TopMemoryApp));
    }

    /// <summary>Null for a line that is cut or damaged; the rest of the file still loads.</summary>
    public static TelemetryPoint? Parse(string line)
    {
        string[] parts = line.Split('\t');
        if (parts.Length < 11
            || !DateTimeOffset.TryParse(parts[0], Invariant, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset at)
            || ReadingAt(parts, 1) is not { } cpu
            || ReadingAt(parts, 3) is not { } memory)
        {
            return null;
        }

        static string? Text(string value) => value.Length == 0 ? null : value;
        return new TelemetryPoint(at, cpu, memory, ReadingAt(parts, 5), ReadingAt(parts, 7), Text(parts[9]), Text(parts[10]));
    }

    private static Reading? ReadingAt(string[] parts, int index) =>
        double.TryParse(parts[index], NumberStyles.Float, Invariant, out double average)
        && double.TryParse(parts[index + 1], NumberStyles.Float, Invariant, out double peak)
            ? new Reading(average, peak)
            : null;

    private string FileOf(DateTimeOffset at) =>
        Path.Combine(Folder, at.UtcDateTime.ToString("yyyy-MM-dd", Invariant) + Extension);

    private static IEnumerable<string> ReadLines(string file)
    {
        try
        {
            return File.ReadAllLines(file, Encoding.UTF8);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
