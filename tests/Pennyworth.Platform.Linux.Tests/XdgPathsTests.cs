using Pennyworth.Platform.Linux;

namespace Pennyworth.Platform.Linux.Tests;

public class XdgPathsTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    [Fact]
    public void Falls_back_to_the_specification_defaults_under_home()
    {
        var paths = new XdgPaths(Env(("HOME", "/home/ana")));

        Assert.Equal(Path.Combine("/home/ana", ".config"), paths.ConfigHome);
        Assert.Equal(Path.Combine("/home/ana", ".local", "share"), paths.DataHome);
        Assert.Equal(Path.Combine("/home/ana", ".cache"), paths.CacheHome);
        Assert.Equal(Path.Combine("/home/ana", ".local", "state"), paths.StateHome);
    }

    [Fact]
    public void Settings_follow_the_config_home_and_everything_else_the_data_home()
    {
        var paths = new XdgPaths(Env(("HOME", "/home/ana"), ("XDG_CONFIG_HOME", "/c"), ("XDG_DATA_HOME", "/d")));

        Assert.Equal(Path.Combine("/c", "Pennyworth", "settings.json"), paths.SettingsPath);
        Assert.Equal(Path.Combine("/d", "Pennyworth", "cleanup-history.jsonl"), paths.CleanupHistoryPath);
        Assert.Equal(Path.Combine("/d", "Pennyworth", "conversations"), paths.ConversationFolder);
        Assert.Equal(Path.Combine("/d", "Pennyworth", "usage.json"), paths.UsagePath);
        Assert.Equal(Path.Combine("/d", "Pennyworth", "models"), paths.ModelRoot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/dir")]
    [InlineData(".config")]
    public void Ignores_a_variable_that_is_empty_or_not_absolute(string value)
    {
        var paths = new XdgPaths(Env(("HOME", "/home/ana"), ("XDG_CONFIG_HOME", value), ("XDG_DATA_HOME", value)));

        Assert.Equal(Path.Combine("/home/ana", ".config"), paths.ConfigHome);
        Assert.Equal(Path.Combine("/home/ana", ".local", "share"), paths.DataHome);
    }

    [Fact]
    public void Drops_a_trailing_separator()
    {
        var paths = new XdgPaths(Env(("HOME", "/home/ana/"), ("XDG_CACHE_HOME", "/cache/")));

        Assert.Equal("/home/ana", paths.Home);
        Assert.Equal("/cache", paths.CacheHome);
    }
}

public class EnvironmentScopeTests
{
    private static EnvironmentScope Scope(params (string Name, string Value)[] values)
    {
        string? Env(string name) => values.FirstOrDefault(v => v.Name == name).Value;
        return new EnvironmentScope(new XdgPaths(Env), Env);
    }

    [Theory]
    [InlineData("USERPROFILE", "/home/ana")]
    [InlineData("HOME", "/home/ana")]
    [InlineData("LOCALAPPDATA", "/home/ana/.cache")]
    [InlineData("LocalAppData", "/home/ana/.cache")]
    [InlineData("APPDATA", "/home/ana/.config")]
    [InlineData("XDG_DATA_HOME", "/home/ana/.local/share")]
    [InlineData("SystemDrive", "/")]
    public void Maps_the_names_the_data_uses(string name, string expected)
    {
        string? resolved = Scope(("HOME", "/home/ana")).Resolve(name);

        Assert.Equal(expected, resolved?.Replace(Path.DirectorySeparatorChar, '/'));
    }

    [Theory]
    [InlineData("SystemRoot")]
    [InlineData("ProgramFiles")]
    [InlineData("ProgramData")]
    [InlineData("PATH")]
    [InlineData("")]
    public void Leaves_names_without_a_meaning_here_unresolved(string name) =>
        Assert.Null(Scope(("HOME", "/home/ana")).Resolve(name));

    [Fact]
    public void Temp_follows_tmpdir_and_otherwise_slash_tmp()
    {
        Assert.Equal("/run/user/1000", Scope(("HOME", "/h"), ("TMPDIR", "/run/user/1000/")).Resolve("TEMP"));
        Assert.Equal("/tmp", Scope(("HOME", "/h")).Resolve("TEMP"));
        Assert.Equal("/tmp", Scope(("HOME", "/h"), ("TMPDIR", "relative")).Resolve("TMP"));
    }
}
