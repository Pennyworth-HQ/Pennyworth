using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pennyworth.Cleanup;

public sealed record CleanupHistoryEntry(DateTimeOffset When, IReadOnlyList<CleanupHistoryItem> Items)
{
    public long BytesFreed => Items.Sum(item => item.BytesFreed);
}

public sealed record CleanupHistoryItem(string RuleId, long BytesFreed, long FilesDeleted, long Skipped);

/// <summary>Every cleanup run, one JSON line each, in <c>%LocalAppData%\Pennyworth\cleanup-history.jsonl</c>.</summary>
public sealed class CleanupHistory(string path)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pennyworth", "cleanup-history.jsonl");

    public void Append(IEnumerable<CleanupResult> results, DateTimeOffset when)
    {
        var entry = new CleanupHistoryEntry(
            when,
            results.Select(r => new CleanupHistoryItem(r.Rule.Id, r.BytesFreed, r.FilesDeleted, r.Skipped)).ToArray());
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(entry, HistoryJsonContext.Default.CleanupHistoryEntry) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The log is informational; a cleanup that worked must not fail because of it.
        }
    }

    public IReadOnlyList<CleanupHistoryEntry> Read()
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var entries = new List<CleanupHistoryEntry>();
        foreach (string line in File.ReadLines(path))
        {
            try
            {
                if (JsonSerializer.Deserialize(line, HistoryJsonContext.Default.CleanupHistoryEntry) is { } entry)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // Skip a damaged line, keep the rest.
            }
        }

        return entries;
    }
}

[JsonSerializable(typeof(CleanupHistoryEntry))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;
