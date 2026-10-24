using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using Pennyworth.App.Views;
using Pennyworth.Insights.Classification;
using Pennyworth.KnowledgeBase;
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
            var viewModel = new MainWindowViewModel(
                new WindowsDriveService(),
                new DirectoryScanner(),
                classifier,
                new KnowledgeBaseLocationInfo(classifier),
                new ExplorerLauncher());

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            viewModel.Initialize();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
