namespace Pennyworth.Platform.Linux;

/// <summary>
/// Where Pennyworth keeps its files on Linux, following the XDG base directory specification:
/// settings under the config home, everything the user would miss under the data home.
/// A variable that is unset, empty or not an absolute path is ignored, as the specification says.
/// </summary>
public sealed class XdgPaths
{
    private const string AppFolder = "Pennyworth";

    /// <param name="environment">Defaults to the process environment.</param>
    /// <exception cref="InvalidOperationException">No home directory can be found, so no default location exists.</exception>
    public XdgPaths(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;

        Home = Absolute(environment("HOME"))
            ?? Absolute(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            ?? throw new InvalidOperationException("The home directory is unknown: HOME is not set to an absolute path.");
        ConfigHome = Absolute(environment("XDG_CONFIG_HOME")) ?? Path.Combine(Home, ".config");
        DataHome = Absolute(environment("XDG_DATA_HOME")) ?? Path.Combine(Home, ".local", "share");
        CacheHome = Absolute(environment("XDG_CACHE_HOME")) ?? Path.Combine(Home, ".cache");
        StateHome = Absolute(environment("XDG_STATE_HOME")) ?? Path.Combine(Home, ".local", "state");
    }

    public string Home { get; }

    public string ConfigHome { get; }

    public string DataHome { get; }

    public string CacheHome { get; }

    public string StateHome { get; }

    /// <summary><c>$XDG_CONFIG_HOME/Pennyworth/settings.json</c>.</summary>
    public string SettingsPath => Path.Combine(ConfigHome, AppFolder, "settings.json");

    /// <summary><c>$XDG_DATA_HOME/Pennyworth</c>: conversations, usage, history and models.</summary>
    public string DataFolder => Path.Combine(DataHome, AppFolder);

    public string CleanupHistoryPath => Path.Combine(DataFolder, "cleanup-history.jsonl");

    public string ConversationFolder => Path.Combine(DataFolder, "conversations");

    public string UsagePath => Path.Combine(DataFolder, "usage.json");

    public string ModelRoot => Path.Combine(DataFolder, "models");

    private static string? Absolute(string? path) =>
        !string.IsNullOrEmpty(path) && Path.IsPathRooted(path) ? Path.TrimEndingDirectorySeparator(path) : null;
}
