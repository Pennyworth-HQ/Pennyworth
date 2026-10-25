using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.App.Views;
using Pennyworth.Cleanup;
using Pennyworth.Insights.Classification;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring;
using Pennyworth.Monitoring.Hardware;
using Pennyworth.Scanning;

namespace Pennyworth.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root: the only place concrete services are created.
            var classifier = new RuleClassifier(KnowledgeBaseLoader.LoadEmbedded().Locations);
            var monitor = new SystemMonitor(KnowledgeBaseLoader.LoadProcesses().Processes);
            var viewModel = new MainWindowViewModel(new AppServices(
                new WindowsDriveService(),
                new DirectoryScanner(),
                classifier,
                new KnowledgeBaseLocationInfo(classifier),
                new ExplorerLauncher(),
                new CleanupService(KnowledgeBaseLoader.LoadCleanupRules().Rules),
                new ElevatedHiddenSpaceService(),
                new FileSettingsStore(FileSettingsStore.DefaultPath),
                monitor,
                new ProcessCloser(),
                new CleanupHistory(CleanupHistory.DefaultPath),
                Specs: new WindowsSpecsReader(),
                Clipboard: new AvaloniaClipboardService(() => desktop.MainWindow)));

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.Exit += (_, _) => monitor.Dispose();
            viewModel.Initialize();

            // Live numbers once per second; the first sample right away.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += async (_, _) => await viewModel.TickAsync();
            timer.Start();
            _ = viewModel.TickAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
