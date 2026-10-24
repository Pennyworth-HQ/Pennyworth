using Pennyworth.App.Services;
using Pennyworth.App.ViewModels;
using static Pennyworth.App.Tests.SampleTrees;

namespace Pennyworth.App.Tests;

public class MainWindowViewModelTests
{
    private static readonly DriveSummary DriveC = new(@"C:\", "", 200 * Gb, 100 * Gb);
    private static readonly DriveSummary DriveD = new(@"D:\", "Games", 500 * Gb, 400 * Gb);

    private static (MainWindowViewModel ViewModel, FakeScanner Scanner) Create()
    {
        var scanner = new FakeScanner(_ => Build());
        var viewModel = new MainWindowViewModel(
            new FakeDriveService(DriveD, DriveC),
            scanner,
            new NullClassifier(),
            new FakeLocations(),
            new FakeExplorer());
        return (viewModel, scanner);
    }

    [Fact]
    public async Task Starts_by_scanning_the_windows_drive()
    {
        var (viewModel, scanner) = Create();

        viewModel.Initialize();
        await viewModel.CurrentScan;

        Assert.Equal(2, viewModel.Drives.Count);
        Assert.Equal(@"C:\", viewModel.SelectedDrive?.Summary.RootPath);
        Assert.Equal([@"C:\"], scanner.ScannedRoots);
        var summary = Assert.IsType<StorageSummaryViewModel>(viewModel.CurrentPage);
        Assert.True(summary.IsComplete);
        Assert.False(viewModel.IsScanning);
    }

    [Fact]
    public async Task Reuses_finished_scans_when_switching_back()
    {
        var (viewModel, scanner) = Create();
        viewModel.Initialize();
        await viewModel.CurrentScan;

        viewModel.SelectedDrive = viewModel.Drives.Single(d => d.Summary.RootPath == @"D:\");
        await viewModel.CurrentScan;
        viewModel.SelectedDrive = viewModel.Drives.Single(d => d.Summary.RootPath == @"C:\");
        await viewModel.CurrentScan;

        Assert.Equal([@"C:\", @"D:\"], scanner.ScannedRoots);
    }

    [Fact]
    public async Task Rescan_scans_again()
    {
        var (viewModel, scanner) = Create();
        viewModel.Initialize();
        await viewModel.CurrentScan;

        viewModel.RescanCommand.Execute(null);
        await viewModel.CurrentScan;

        Assert.Equal([@"C:\", @"C:\"], scanner.ScannedRoots);
    }

    [Fact]
    public async Task Switching_drives_cancels_the_running_scan()
    {
        var (viewModel, scanner) = Create();
        scanner.Gate = new TaskCompletionSource();
        viewModel.Initialize();
        Task firstScan = viewModel.CurrentScan;
        Assert.True(viewModel.IsScanning);

        scanner.Gate = null;
        viewModel.SelectedDrive = viewModel.Drives.Single(d => d.Summary.RootPath == @"D:\");
        await viewModel.CurrentScan;
        await firstScan;

        var summary = Assert.IsType<StorageSummaryViewModel>(viewModel.CurrentPage);
        Assert.Equal(@"D:\", summary.Drive.RootPath);
        Assert.False(viewModel.IsScanning);
    }

    [Fact]
    public async Task Opening_a_folder_and_going_back_returns_to_the_summary()
    {
        var (viewModel, _) = Create();
        viewModel.Initialize();
        await viewModel.CurrentScan;
        var summary = Assert.IsType<StorageSummaryViewModel>(viewModel.CurrentPage);

        summary.BrowseAllCommand.Execute(null);
        var browser = Assert.IsType<FolderBrowserViewModel>(viewModel.CurrentPage);
        browser.BackCommand.Execute(null);

        Assert.Same(summary, viewModel.CurrentPage);
    }

    [Fact]
    public void Drive_names_use_label_or_local_disk()
    {
        Assert.Equal("Games (D:)", new DriveViewModel(DriveD).Name);
        Assert.EndsWith("(C:)", new DriveViewModel(DriveC).Name);
        Assert.Equal(0.5, new DriveViewModel(DriveC).UsedFraction, 3);
    }
}
