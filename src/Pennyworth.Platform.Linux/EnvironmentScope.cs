namespace Pennyworth.Platform.Linux;

/// <summary>
/// Resolves the <c>%NAME%</c> variables the knowledge base writes its paths with. Windows names map
/// to their closest Linux place, XDG and <c>HOME</c> names resolve directly, and everything else
/// (<c>SystemRoot</c>, <c>ProgramFiles</c>, an unknown name) is unresolved, which drops the rule
/// that uses it. Names are matched ignoring case, because the data was written for Windows.
/// </summary>
public sealed class EnvironmentScope(XdgPaths paths, Func<string, string?>? environment = null)
{
    private const string FallbackTemp = "/tmp";

    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    /// <summary>The folder for <paramref name="name"/>, or null when it has no meaning on Linux.</summary>
    public string? Resolve(string name) => name.ToUpperInvariant() switch
    {
        "HOME" or "USERPROFILE" => paths.Home,
        "TMPDIR" or "TEMP" or "TMP" => Temp(),
        "XDG_CACHE_HOME" or "LOCALAPPDATA" => paths.CacheHome,
        "XDG_CONFIG_HOME" or "APPDATA" => paths.ConfigHome,
        "XDG_DATA_HOME" => paths.DataHome,
        "XDG_STATE_HOME" => paths.StateHome,
        "SYSTEMDRIVE" => "/",
        _ => null,
    };

    private string Temp() =>
        _environment("TMPDIR") is { Length: > 0 } dir && Path.IsPathRooted(dir)
            ? Path.TrimEndingDirectorySeparator(dir)
            : FallbackTemp;
}
