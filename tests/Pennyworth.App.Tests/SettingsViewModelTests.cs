using Pennyworth.App.Localization;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.Platform;

namespace Pennyworth.App.Tests;

[Collection(nameof(LanguageSwitching))]
public class SettingsViewModelTests
{
    private static MainWindowViewModel Create(MemorySettings settings, IStartupRegistration? startup = null) =>
        new(TestApp.Services(settings: settings) with { Startup = startup });

    [Fact]
    public void Settings_is_a_page_of_its_own()
    {
        MainWindowViewModel viewModel = Create(new MemorySettings());

        viewModel.GoSettingsCommand.Execute(null);

        Assert.True(viewModel.IsSettingsPage);
        Assert.Same(viewModel.Settings, viewModel.MainContent);
    }

    [Fact]
    public void A_chosen_theme_is_saved_and_ignores_windows()
    {
        var settings = new MemorySettings();
        MainWindowViewModel viewModel = Create(settings);

        viewModel.Settings.SetThemeDarkCommand.Execute(null);
        viewModel.SyncSystemTheme(isDark: false);

        Assert.True(viewModel.IsDarkTheme);
        Assert.True(viewModel.Settings.IsThemeDark);
        Assert.Equal(ThemePreference.Dark, settings.Current.Theme);
    }

    [Fact]
    public void The_system_theme_follows_windows()
    {
        var settings = new MemorySettings(new AppSettings { WelcomeSeen = true, Language = LanguagePreference.English, Theme = ThemePreference.Light });
        MainWindowViewModel viewModel = Create(settings);

        viewModel.Settings.SetThemeSystemCommand.Execute(null);
        viewModel.SyncSystemTheme(isDark: true);

        Assert.True(viewModel.IsDarkTheme);
        Assert.True(viewModel.Settings.IsThemeSystem);
        Assert.Equal(ThemePreference.System, settings.Current.Theme);
    }

    [Fact]
    public void Ticking_start_with_the_pc_turns_it_on_and_off()
    {
        var startup = new FakeStartup();
        MainWindowViewModel viewModel = Create(new MemorySettings(), startup);

        viewModel.Settings.StartWithPc = true;
        Assert.True(startup.IsEnabled);
        Assert.True(viewModel.HasToast);

        viewModel.Settings.StartWithPc = false;
        Assert.False(startup.IsEnabled);
    }

    [Fact]
    public void The_box_shows_what_windows_has_when_the_page_opens()
    {
        var startup = new FakeStartup { IsEnabled = true };
        MainWindowViewModel viewModel = Create(new MemorySettings(), startup);

        viewModel.GoSettingsCommand.Execute(null);

        Assert.True(viewModel.Settings.StartWithPc);
        Assert.Equal(0, startup.Writes);
    }

    [Fact]
    public void A_refused_change_keeps_the_box_as_it_was()
    {
        var startup = new FakeStartup { Refuses = true };
        MainWindowViewModel viewModel = Create(new MemorySettings(), startup);

        viewModel.Settings.StartWithPc = true;

        Assert.False(viewModel.Settings.StartWithPc);
        Assert.Equal(Resources.Strings.StartupFailed, viewModel.Toast);
    }

    [Fact]
    public void Without_a_startup_switch_the_option_is_hidden()
    {
        MainWindowViewModel viewModel = Create(new MemorySettings());

        Assert.False(viewModel.Settings.CanStartWithPc);
        viewModel.Settings.StartWithPc = true;
        Assert.False(viewModel.Settings.StartWithPc);
    }

    private sealed class FakeStartup : IStartupRegistration
    {
        public bool IsEnabled { get; set; }

        public bool Refuses { get; init; }

        public int Writes { get; private set; }

        public void SetEnabled(bool enabled)
        {
            if (Refuses)
            {
                throw new UnauthorizedAccessException("Blocked by policy.");
            }

            Writes++;
            IsEnabled = enabled;
        }
    }
}
