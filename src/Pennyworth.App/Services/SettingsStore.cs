using System.Text.Json;
using System.Text.Json.Serialization;
using Pennyworth.App.Localization;
using Pennyworth.Assistant;
using Pennyworth.Monitoring.Alerts;

namespace Pennyworth.App.Services;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed record AppSettings
{
    public LanguagePreference Language { get; init; } = LanguagePreference.System;

    public ThemePreference Theme { get; init; } = ThemePreference.System;

    /// <summary>The welcome screen is shown until the first "Analyze my PC".</summary>
    public bool WelcomeSeen { get; init; }

    /// <summary>Never null: a settings file from before the assistant has no "assistant" key, and the JSON source generator passes null for missing init properties.</summary>
    public AssistantSettings Assistant { get => field ?? new(); init; }

    /// <summary>Never null, for the same reason as <see cref="Assistant"/>.</summary>
    public AlertSettings Alerts { get => field ?? new(); init; }

    /// <summary>By what the properties return, so a missing "assistant" key equals the default.</summary>
    public bool Equals(AppSettings? other) =>
        other is not null
        && Language == other.Language
        && Theme == other.Theme
        && WelcomeSeen == other.WelcomeSeen
        && Assistant.Equals(other.Assistant)
        && Alerts.Equals(other.Alerts);

    public override int GetHashCode() => HashCode.Combine(Language, Theme, WelcomeSeen);
}

/// <summary>How the assistant answers. API keys are never here; they live in the Windows Credential Manager.</summary>
public sealed record AssistantSettings
{
    public IReadOnlyList<AssistantProfile> Profiles { get => field ?? []; init; }

    public string? DefaultProfileId { get; init; }

    /// <summary>Cloud profiles whose send preview the person turned off.</summary>
    public IReadOnlyList<string> HidePreviewFor { get => field ?? []; init; }

    [JsonIgnore]
    public AssistantProfile? Default => Profiles.FirstOrDefault(p => p.Id == DefaultProfileId) ?? Profiles.FirstOrDefault();

    /// <summary>By content, so settings read back from disk equal the ones saved.</summary>
    public bool Equals(AssistantSettings? other) =>
        other is not null
        && DefaultProfileId == other.DefaultProfileId
        && Profiles.SequenceEqual(other.Profiles)
        && HidePreviewFor.SequenceEqual(other.HidePreviewFor);

    public override int GetHashCode() => HashCode.Combine(DefaultProfileId, Profiles.Count, HidePreviewFor.Count);
}

/// <summary>Watching the PC from the tray and the notifications it sends.</summary>
public sealed record AlertSettings
{
    /// <summary>Off pauses every notification; the rules stay as they are.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Closing the window hides it in the tray instead of quitting.</summary>
    public bool RunInBackground { get; init; } = true;

    /// <summary>The "still running in the tray" notification is shown once, the first time the window hides.</summary>
    public bool BackgroundHintShown { get; init; }

    /// <summary>Missing in older files means the defaults; an empty list means the person removed them all.</summary>
    public IReadOnlyList<AlertRule> Rules { get => field ?? AlertRule.Defaults; init; }

    public bool Equals(AlertSettings? other) =>
        other is not null
        && Enabled == other.Enabled
        && RunInBackground == other.RunInBackground
        && BackgroundHintShown == other.BackgroundHintShown
        && Rules.SequenceEqual(other.Rules);

    public override int GetHashCode() => HashCode.Combine(Enabled, RunInBackground, Rules.Count);
}

public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>Settings in <c>%LocalAppData%\Pennyworth\settings.json</c>. A broken file means defaults.</summary>
public sealed class FileSettingsStore(string path) : ISettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pennyworth", "settings.json");

    public AppSettings Load()
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save them must never break the app.
        }
    }
}

[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
