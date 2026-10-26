using Pennyworth.App.Localization;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Tests;

/// <summary>Tests that switch the process-wide language run alone.</summary>
[CollectionDefinition(nameof(LanguageSwitching), DisableParallelization = true)]
public sealed class LanguageSwitching;

[Collection(nameof(LanguageSwitching))]
public sealed class LocalizationTests : IDisposable
{
    public void Dispose() => Localizer.Instance.SetLanguage(LanguagePreference.English);

    [Fact]
    public void Switches_strings_and_number_formats()
    {
        Localizer.Instance.SetLanguage(LanguagePreference.Portuguese);

        Assert.Equal("Para onde foi seu espaço", Localizer.Instance["StorageTitle"]);
        Assert.Equal("1,50 KB", Pennyworth.Core.Formatting.ByteSize.Format(1536));
        Assert.True(Localizer.Instance.IsPortuguese);

        Localizer.Instance.SetLanguage(LanguagePreference.English);

        Assert.Equal("Where your space has gone", Localizer.Instance["StorageTitle"]);
        Assert.Equal("1.50 KB", Pennyworth.Core.Formatting.ByteSize.Format(1536));
    }

    [Fact]
    public void Observed_text_follows_the_language()
    {
        Localizer.Instance.SetLanguage(LanguagePreference.English);
        var seen = new List<string>();

        using (Localizer.Instance.Observe("Categories", upper: true).Subscribe(new Collect(seen)))
        {
            Localizer.Instance.SetLanguage(LanguagePreference.Portuguese);
        }

        Localizer.Instance.SetLanguage(LanguagePreference.English); // after unsubscribing: ignored

        Assert.Equal(["CATEGORIES", "CATEGORIAS"], seen);
    }

    [Fact]
    public void Unknown_keys_show_the_key()
    {
        Assert.Equal("NoSuchKey", Localizer.Instance["NoSuchKey"]);
    }

    private sealed class Collect(List<string> seen) : IObserver<string>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error) => throw error;

        public void OnNext(string value) => seen.Add(value);
    }
}

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pw-settings-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Round_trips_settings()
    {
        var store = new FileSettingsStore(Path.Combine(_dir, "sub", "settings.json"));
        var settings = new AppSettings { Language = LanguagePreference.Portuguese, Theme = ThemePreference.Dark, WelcomeSeen = true };

        store.Save(settings);

        Assert.Equal(settings, store.Load());
    }

    [Fact]
    public void Settings_from_before_the_assistant_still_load()
    {
        string path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{ "language": "Portuguese", "theme": "Dark", "welcomeSeen": true }""");

        var settings = new FileSettingsStore(path).Load();

        Assert.True(settings.WelcomeSeen);
        Assert.Empty(settings.Assistant.Profiles);
        Assert.Empty(settings.Assistant.HidePreviewFor);
        Assert.Null(settings.Assistant.Default);
        Assert.NotNull(new MainWindowViewModel(TestApp.Services(settings: new FileSettingsStore(path)) with { Assistant = AskTestServices.Create(_dir) }).Ask);
    }

    [Fact]
    public void Missing_or_broken_files_give_defaults()
    {
        string path = Path.Combine(_dir, "settings.json");
        Assert.Equal(new AppSettings(), new FileSettingsStore(path).Load());

        File.WriteAllText(path, "{ not json");

        Assert.Equal(new AppSettings(), new FileSettingsStore(path).Load());
    }
}
