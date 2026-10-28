using System.Diagnostics;
using Pennyworth.App.Localization;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

public interface ISettingsHost
{
    ThemePreference Theme { get; }

    void SetTheme(ThemePreference theme);

    LanguagePreference Language { get; }

    void SetLanguage(LanguagePreference language);

    void ShowToast(string message);
}

/// <summary>Settings: theme, language, the pet, and opening the app when the PC starts.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsHost _host;
    private readonly IStartupRegistration? _startup;
    private bool _startWithPc;

    public SettingsViewModel(ISettingsHost host, PetViewModel pet, IStartupRegistration? startup)
    {
        _host = host;
        _startup = startup;
        Pet = pet;
        _startWithPc = ReadStartup();
    }

    public PetViewModel Pet { get; }

    public bool IsThemeSystem => _host.Theme == ThemePreference.System;

    public bool IsThemeLight => _host.Theme == ThemePreference.Light;

    public bool IsThemeDark => _host.Theme == ThemePreference.Dark;

    public bool IsLanguageSystem => _host.Language == LanguagePreference.System;

    public bool IsLanguagePortuguese => _host.Language == LanguagePreference.Portuguese;

    public bool IsLanguageEnglish => _host.Language == LanguagePreference.English;

    /// <summary>False where the OS has no startup switch (and in tests without one).</summary>
    public bool CanStartWithPc => _startup is not null;

    /// <summary>"Open Pennyworth when I turn on the PC": the switch is flipped as soon as the box is ticked.</summary>
    public bool StartWithPc
    {
        get => _startWithPc;
        set
        {
            if (_startWithPc == value)
            {
                return;
            }

            if (_startup is { } startup)
            {
                try
                {
                    startup.SetEnabled(value);
                    _startWithPc = value;
                    _host.ShowToast(value ? Strings.StartupOnToast : Strings.StartupOffToast);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
                {
                    Debug.WriteLine(ex);
                    _host.ShowToast(Strings.StartupFailed);
                }
            }

            // Also when Windows refused: the box goes back to what is really set.
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void SetThemeSystem() => SetTheme(ThemePreference.System);

    [RelayCommand]
    private void SetThemeLight() => SetTheme(ThemePreference.Light);

    [RelayCommand]
    private void SetThemeDark() => SetTheme(ThemePreference.Dark);

    [RelayCommand]
    private void SetLanguageSystem() => _host.SetLanguage(LanguagePreference.System);

    [RelayCommand]
    private void SetLanguagePortuguese() => _host.SetLanguage(LanguagePreference.Portuguese);

    [RelayCommand]
    private void SetLanguageEnglish() => _host.SetLanguage(LanguagePreference.English);

    /// <summary>Re-reads everything; the theme, language or startup key may have changed elsewhere.</summary>
    public void Refresh()
    {
        _startWithPc = ReadStartup();
        OnPropertyChanged(string.Empty);
    }

    private void SetTheme(ThemePreference theme)
    {
        _host.SetTheme(theme);
        Refresh();
    }

    private bool ReadStartup()
    {
        try
        {
            return _startup?.IsEnabled ?? false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or InvalidOperationException)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }
}
