using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pennyworth.Assistant;

public enum UsageState
{
    Ok,

    /// <summary>80% of the monthly cap or more: show a note.</summary>
    Near,

    /// <summary>The cap is used up: cloud questions stop until next month or a higher cap.</summary>
    Reached,
}

/// <param name="Month">"2026-10".</param>
public sealed record UsageRecord(string ProfileId, string Month, long InputTokens, long OutputTokens)
{
    public long Total => InputTokens + OutputTokens;
}

internal sealed record UsageFile(List<UsageRecord> Records);

[JsonSerializable(typeof(UsageFile))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class UsageJsonContext : JsonSerializerContext;

/// <summary>Tokens used per profile and calendar month, kept in one small JSON file on this PC.</summary>
public sealed class UsageMeter(string path)
{
    public const double NearFraction = 0.8;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pennyworth", "assistant", "usage.json");

    public void Add(string profileId, long inputTokens, long outputTokens, DateTimeOffset when)
    {
        var file = Read();
        string month = MonthOf(when);
        int index = file.Records.FindIndex(r => r.ProfileId == profileId && r.Month == month);
        var current = index >= 0 ? file.Records[index] : new UsageRecord(profileId, month, 0, 0);
        var updated = current with { InputTokens = current.InputTokens + inputTokens, OutputTokens = current.OutputTokens + outputTokens };
        if (index >= 0)
        {
            file.Records[index] = updated;
        }
        else
        {
            file.Records.Add(updated);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(file, UsageJsonContext.Default.UsageFile));
    }

    public UsageRecord Month(string profileId, DateTimeOffset when)
    {
        string month = MonthOf(when);
        return Read().Records.FirstOrDefault(r => r.ProfileId == profileId && r.Month == month) ?? new UsageRecord(profileId, month, 0, 0);
    }

    public UsageState State(AssistantProfile profile, DateTimeOffset now)
    {
        if (profile.MonthlyTokenCap is not long cap || cap <= 0)
        {
            return UsageState.Ok;
        }

        long used = Month(profile.Id, now).Total;
        return used >= cap ? UsageState.Reached : used >= cap * NearFraction ? UsageState.Near : UsageState.Ok;
    }

    private static string MonthOf(DateTimeOffset when) => when.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private UsageFile Read()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), UsageJsonContext.Default.UsageFile) ?? new UsageFile([])
                : new UsageFile([]);
        }
        catch (JsonException)
        {
            return new UsageFile([]); // A damaged file starts the count again rather than blocking questions.
        }
    }
}
