using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Pennyworth.App.Localization;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.App.Services.Windows;
using Pennyworth.App.ViewModels;
using Pennyworth.App.Views;
using Pennyworth.Cleanup;
using Pennyworth.Insights.Classification;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring;
using Pennyworth.Platform;

namespace Pennyworth.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root: the only place concrete services are created, and the only place that
            // asks which operating system this is.
            IPlatform platform = CreatePlatform();
            var classifier = new RuleClassifier(KnowledgeBaseLoader.LoadEmbedded().Locations, platform.ResolveVariable);
            ISystemMonitor? monitor = platform.CreateMonitor(KnowledgeBaseLoader.LoadProcesses().Processes);
            ITray? tray = CreateTray(platform);
            var window = new MainWindow();
            var viewModel = new MainWindowViewModel(new AppServices(
                platform.Drives,
                platform.Scanner,
                classifier,
                new KnowledgeBaseLocationInfo(classifier),
                platform.Explorer,
                platform.CreateCleanup(KnowledgeBaseLoader.LoadCleanupRules().Rules),
                platform.HiddenSpace,
                new FileSettingsStore(platform.SettingsPath),
                platform.Specs,
                monitor,
                platform.Closer,
                new CleanupHistory(platform.CleanupHistoryPath),
                Clipboard: new AvaloniaClipboardService(() => window),
                Assistant: platform.CreateAssistant(() => monitor?.Sample().Memory.Available ?? 0),
                Notifier: tray,
                Startup: platform.Startup,
                Pets: new PetLibrary(Path.Combine(Path.GetDirectoryName(platform.SettingsPath)!, "pets")),
                Files: new AvaloniaFilePicker(() => window)));

            window.DataContext = viewModel;
            desktop.Exit += (_, _) =>
            {
                tray?.Dispose();
                (monitor as IDisposable)?.Dispose();
            };
            viewModel.Initialize();

            PetWindow pet = ShowPet(viewModel);
            desktop.Exit += (_, _) => pet.Close();

            if (tray is null)
            {
                // The pet's window must not keep the app alive once the main one is closed.
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                desktop.MainWindow = window;
                SingleInstance.ShowRequested = () => Dispatcher.UIThread.Post(window.Activate);
                viewModel.ShowWindowRequested += (_, _) => window.Activate();
            }
            else
            {
                RunInTray(desktop, window, viewModel, tray);
            }

            // Live numbers once per second; the first sample right away.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async (_, _) =>
            {
                try
                {
                    await viewModel.TickAsync();
                }
                catch (Exception)
                {
                    // A timer tick is an async void boundary: letting anything escape it terminates
                    // the window. TickAsync already keeps the last numbers when sampling fails.
                }
            };
            timer.Start();
            _ = viewModel.TickAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// With a tray icon the app lives until "Exit": closing the window only hides it when the person
    /// wants it kept in the background, and a start at sign-in begins hidden.
    /// </summary>
    private static void RunInTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, MainWindowViewModel viewModel, ITray tray)
    {
        bool exiting = false;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        void Show()
        {
            viewModel.InBackground = false;
            window.Show();
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
            _ = viewModel.TickAsync(); // Fresh numbers right away, not up to a second later.
        }

        void Exit()
        {
            exiting = true;
            desktop.Shutdown();
        }

        void UpdateMenu() => tray.Show("Pennyworth",
        [
            new TrayMenuItem(Strings.TrayOpen, Show),
            new TrayMenuItem(Strings.TrayPause, () => { viewModel.Alerts.TogglePaused(); UpdateMenu(); }, IsChecked: !viewModel.Alerts.Enabled),
            new TrayMenuItem(Strings.TrayExit, Exit),
        ], Show);

        UpdateMenu();
        Localizer.Instance.LanguageChanged += (_, _) => UpdateMenu();
        viewModel.Alerts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AlertsViewModel.Enabled))
            {
                UpdateMenu();
            }
        };
        viewModel.ShowWindowRequested += (_, _) => Show();
        SingleInstance.ShowRequested = () => Dispatcher.UIThread.Post(Show);

        window.Closing += (_, e) =>
        {
            if (exiting || !viewModel.RunInBackground)
            {
                return;
            }

            e.Cancel = true;
            window.Hide();
            viewModel.InBackground = true;
            if (viewModel.TakeBackgroundHint())
            {
                viewModel.Alerts.Notify(Strings.BackgroundHintTitle, Strings.BackgroundHintBody);
            }
        };
        window.Closed += (_, _) => Exit();

        if (desktop.Args?.Contains(SingleInstance.BackgroundArgument) == true)
        {
            viewModel.InBackground = true;
        }
        else
        {
            Show();
        }
    }

    /// <summary>
    /// The pet's window follows the pet added in Settings: shown with one, hidden with none, and
    /// its bubbles timed only while it is on the screen.
    /// </summary>
    private static PetWindow ShowPet(MainWindowViewModel viewModel)
    {
        PetViewModel pet = viewModel.Pet;
        pet.ReduceMotion = OperatingSystem.IsWindows() && WindowsAnimations.AreOff();
        var window = new PetWindow { DataContext = pet };
        window.OpenRequested += (_, _) => viewModel.RequestShowWindow();

        var timer = new DispatcherTimer { Interval = PetViewModel.TickInterval };
        timer.Tick += (_, _) => pet.Tick();

        void Sync()
        {
            if (pet.IsVisible)
            {
                window.Show();
                timer.Start();
            }
            else
            {
                timer.Stop();
                window.Hide();
            }
        }

        pet.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PetViewModel.IsVisible))
            {
                Sync();
            }
        };
        Sync();
        return window;
    }

    private static ITray? CreateTray(IPlatform platform)
    {
        try
        {
            return platform.CreateTray();
        }
        catch (InvalidOperationException ex)
        {
            // No tray: the app works as before, closing quits and alerts show as toasts.
            System.Diagnostics.Debug.WriteLine(ex);
            return null;
        }
    }

    private static IPlatform CreatePlatform() => OperatingSystem.IsWindows()
        ? new WindowsPlatform()
        : throw new PlatformNotSupportedException(
            "Pennyworth does not run on this operating system yet. Only Windows is supported.");
}
