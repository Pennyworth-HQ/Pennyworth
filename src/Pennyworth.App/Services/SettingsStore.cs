using System.Text.Json;
using System.Text.Json.Serialization;
using Pennyworth.App.Localization;

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
