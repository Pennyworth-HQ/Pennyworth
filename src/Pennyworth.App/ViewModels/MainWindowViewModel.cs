using System.Collections.ObjectModel;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly IDriveService _drives;
    private readonly IStorageScanner _scanner;
    private readonly IPathClassifier _classifier;
    private readonly ILocationInfo _locations;
    private readonly IExplorerLauncher _explorer;
    private readonly Dictionary<string, StorageSummaryViewModel> _summaries = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _scanCancellation;

    public MainWindowViewModel(
        IDriveService drives,
        IStorageScanner scanner,
        IPathClassifier classifier,
        ILocationInfo locations,
        IExplorerLauncher explorer)
    {
        _drives = drives;
        _scanner = scanner;
        _classifier = classifier;
        _locations = locations;
        _explorer = explorer;
    }

    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    /// <summary>
    /// The drive shown. Clearing it is ignored: a drive list clears its selection when its
    /// page unloads, and that must not forget the drive.
    /// </summary>
    public DriveViewModel? SelectedDrive
    {
        get;
        set
        {
            if (value is null && field is not null)
            {
                OnPropertyChanged(); // Lets the list restore its selection.
                return;
            }

            if (SetProperty(ref field, value))
            {
                OnSelectedDriveChanged(value);
            }
        }
    }

    /// <summary>A <see cref="StorageSummaryViewModel"/> or a <see cref="FolderBrowserViewModel"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStoragePage), nameof(IsFoldersPage))]
    [NotifyCanExecuteChangedFor(nameof(ShowFoldersCommand))]
    public partial object? CurrentPage { get; private set; }

    public bool IsStoragePage => CurrentPage is StorageSummaryViewModel;

    public bool IsFoldersPage => CurrentPage is FolderBrowserViewModel;

    /// <summary>The summary of the selected drive, shown by the Storage page.</summary>
    private StorageSummaryViewModel? CurrentSummary { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RescanCommand))]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    /// <summary>The scan started by the latest drive selection; lets tests await it.</summary>
    public Task CurrentScan { get; private set; } = Task.CompletedTask;

    /// <summary>Loads the drives and starts scanning the Windows drive.</summary>
    public void Initialize()
    {
        Drives.Clear();
        foreach (DriveSummary drive in _drives.GetDrives())
        {
            Drives.Add(new DriveViewModel(drive));
        }

        SelectedDrive = Drives.FirstOrDefault(d => string.Equals(d.Summary.RootPath, _drives.SystemDriveRoot, StringComparison.OrdinalIgnoreCase))
            ?? Drives.FirstOrDefault();
    }

    private void OnSelectedDriveChanged(DriveViewModel? value)
    {
        if (value is not null)
        {
            CurrentScan = ShowDriveAsync(value, forceRescan: false);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRescan))]
    private void Rescan()
    {
        if (SelectedDrive is { } drive)
        {
            CurrentScan = ShowDriveAsync(drive, forceRescan: true);
        }
    }

    private bool CanRescan() => !IsScanning;

    [RelayCommand]
    private void ShowStorage()
    {
        if (CurrentSummary is { } summary)
        {
            CurrentPage = summary;
        }
    }

    [RelayCommand(CanExecute = nameof(CanShowFolders))]
    private void ShowFolders()
    {
        if (!IsFoldersPage)
        {
            CurrentSummary!.BrowseAllCommand.Execute(null);
        }
    }

    private bool CanShowFolders() => CurrentSummary?.IsComplete == true;

    private async Task ShowDriveAsync(DriveViewModel drive, bool forceRescan)
    {
        _scanCancellation?.Cancel();

        if (!forceRescan && _summaries.TryGetValue(drive.Summary.RootPath, out var existing))
        {
            CurrentSummary = existing;
            CurrentPage = existing;
            StatusText = string.Format(Strings.ScanComplete, existing.FileCount);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _scanCancellation = cancellation;
        StorageSummaryViewModel summary = null!;
        summary = new StorageSummaryViewModel(drive.Summary, _locations, node => OpenFolder(node, summary));
        _summaries[drive.Summary.RootPath] = summary;
        CurrentSummary = summary;
        CurrentPage = summary;
        IsScanning = true;
        StatusText = string.Format(Strings.ScanningFiles, 0);

        var progress = new Progress<ScanProgress>(p =>
        {
            summary.ApplyProgress(p);
            if (_scanCancellation == cancellation)
            {
                StatusText = string.Format(Strings.ScanningFiles, p.FileCount);
            }
        });

        try
        {
            StorageTree tree = await _scanner.ScanAsync(drive.Summary.RootPath, _classifier, progress, cancellation.Token);
            summary.ApplyResult(tree);
            ShowFoldersCommand.NotifyCanExecuteChanged();
            if (_scanCancellation == cancellation)
            {
                StatusText = string.Format(Strings.ScanComplete, tree.FileCount);
            }
        }
        catch (OperationCanceledException)
        {
            // Forget the partial result so the drive is scanned again next time.
            if (_summaries.GetValueOrDefault(drive.Summary.RootPath) == summary)
            {
                _summaries.Remove(drive.Summary.RootPath);
            }
        }
        finally
        {
            if (_scanCancellation == cancellation)
            {
                IsScanning = false;
            }
        }
    }

    private void OpenFolder(StorageNode node, StorageSummaryViewModel returnTo) =>
        CurrentPage = new FolderBrowserViewModel(node, _explorer, () => CurrentPage = returnTo);
}

public sealed class DriveViewModel(DriveSummary summary)
{
    public DriveSummary Summary { get; } = summary;

    public string Name => $"{Label} ({Letter})";

    /// <summary>"C:".</summary>
    public string Letter => Summary.RootPath.TrimEnd('\\');

    /// <summary>The volume label, or "Local Disk" when the drive has none.</summary>
    public string Label => string.IsNullOrWhiteSpace(Summary.Label) ? Strings.LocalDisk : Summary.Label;

    public string UsedPercentText => UsedFraction.ToString("P0");

    public string UsedText => string.Format(Strings.DriveUsed, ByteSize.Format(Summary.UsedBytes), ByteSize.Format(Summary.TotalBytes));

    public double UsedFraction => Summary.TotalBytes == 0 ? 0 : (double)Summary.UsedBytes / Summary.TotalBytes;
}
