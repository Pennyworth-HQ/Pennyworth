using Pennyworth.App.ViewModels;
using Pennyworth.Cleanup;
using Pennyworth.Core.Storage;
using Pennyworth.KnowledgeBase;
using Pennyworth.Monitoring;
using static Pennyworth.App.Tests.SampleTrees;

namespace Pennyworth.App.Tests;

public sealed class CleanupViewModelTests : IDisposable
{
    private static readonly CleanupRule Temp = FakeCleanup.Rule("temp", SafetyLevel.Safe);
    private static readonly CleanupRule Bin = FakeCleanup.Rule("recycle-bin", SafetyLevel.UsuallySafe, kind: CleanupKind.RecycleBin);
    private static readonly CleanupRule Update = FakeCleanup.Rule("windows-update", SafetyLevel.UsuallySafe, admin: true);
    private static readonly CleanupRule Empty = FakeCleanup.Rule("dumps", SafetyLevel.Safe);

    private readonly string _dir = Directory.CreateTempSubdirectory("pw-history-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Measures_and_ticks_only_safe_items_that_have_something()
    {
        var (cleanup, _) = Create();

        await cleanup.MeasureAsync();

        Assert.Equal(CleanupPhase.Select, cleanup.Phase);
        Assert.Equal(["temp", "recycle-bin", "windows-update"], cleanup.Items.Select(i => i.Rule.Id));
        Assert.Equal(["temp"], cleanup.Items.Where(i => i.IsSelected).Select(i => i.Rule.Id));
        Assert.Equal(3 * Gb, cleanup.SafeBytes);
        Assert.Equal(3 * Gb, cleanup.SelectedBytes);
        Assert.Equal([SafetyLevel.Safe, SafetyLevel.UsuallySafe], cleanup.Groups.Select(g => g.Safety));
    }

    [Fact]
    public async Task Admin_items_cannot_be_ticked()
    {
        var (cleanup, _) = Create();
        await cleanup.MeasureAsync();
        var update = cleanup.Items.Single(i => i.Rule == Update);

        cleanup.ToggleCommand.Execute(update);

        Assert.False(update.IsSelected);
        Assert.True(update.NeedsFullScan);
    }

    [Fact]
    public async Task Cleans_only_after_confirmation_and_reports_what_was_freed()
    {
        var (cleanup, host) = Create();
        await cleanup.MeasureAsync();
        cleanup.ToggleCommand.Execute(cleanup.Items.Single(i => i.Rule == Bin));

        cleanup.AskCleanCommand.Execute(null);
        var dialog = Assert.IsType<CleanConfirmDialogViewModel>(host.Dialog);
        Assert.True(dialog.ShowBinNote);
        Assert.Empty(host.Service.Cleaned);

        dialog.ConfirmCommand.Execute(null);
        await dialog.Run;

        Assert.Equal(["temp", "recycle-bin"], host.Service.Cleaned);
        Assert.Equal(CleanupPhase.Done, cleanup.Phase);
        Assert.Equal(4 * Gb, cleanup.FreedBytes);
        Assert.Equal(2, cleanup.SkippedFiles);
        Assert.Single(new CleanupHistory(Path.Combine(_dir, "h.jsonl")).Read());

        await cleanup.FinishCommand.ExecuteAsync(null);
        Assert.Equal(["windows-update"], cleanup.Items.Select(i => i.Rule.Id));
    }

    [Fact]
    public async Task Shows_every_file_before_cleaning_largest_first()
    {
        var explorer = new FakeExplorer();
        var (cleanup, _) = Create(explorer);
        await cleanup.MeasureAsync();
        var temp = cleanup.Items.Single(i => i.Rule == Temp);

        cleanup.OpenPreviewCommand.Execute(temp);
        var preview = cleanup.Preview!;

        Assert.True(cleanup.IsPreviewing);
        Assert.Equal("See what will be deleted (3 files)", temp.PreviewText);
        Assert.Equal(["big.tmp", "notes.log", "small.tmp"], preview.Files.Select(f => f.Name));
        Assert.Equal(@"temp\sub", preview.Files[1].Folder);
        Assert.Equal([@"C:\fake\temp"], preview.Folders.Select(f => f.Path));

        preview.Search = "LOG";
        Assert.Equal(["notes.log"], preview.Files.Select(f => f.Name));
        Assert.Equal("1 matching", preview.MatchText);

        preview.ShowInExplorerCommand.Execute(preview.Files[0].Path);
        Assert.Equal([@"C:\fake\temp\sub\notes.log"], explorer.Shown);

        preview.ToggleIncludeCommand.Execute(null);
        Assert.False(temp.IsSelected);
        Assert.Equal(0, cleanup.SelectedBytes);

        preview.BackCommand.Execute(null);
        Assert.False(cleanup.IsPreviewing);
    }

    [Fact]
    public async Task Recycle_bin_items_show_where_they_came_from()
    {
        var (cleanup, _) = Create();
        await cleanup.MeasureAsync();

        cleanup.OpenPreviewCommand.Execute(cleanup.Items.Single(i => i.Rule == Bin));
        var big = cleanup.Preview!.Files[0];

        Assert.Equal("big.doc", big.Name);
        Assert.StartsWith(@"C:\Users\Bob · deleted ", big.Location);
        Assert.False(big.CanShow);
    }

    [Fact]
    public async Task Cleans_exactly_the_lists_that_were_shown()
    {
        var (cleanup, host) = Create();
        await cleanup.MeasureAsync();
        var temp = cleanup.Items.Single(i => i.Rule == Temp);

        cleanup.AskCleanCommand.Execute(null);
        var dialog = Assert.IsType<CleanConfirmDialogViewModel>(host.Dialog);
        Assert.Equal("3.00 GB in 3 files and items.", dialog.CountLine);

        dialog.ReviewCommand.Execute(temp);
        Assert.Null(host.Dialog);
        Assert.Same(temp, cleanup.Preview!.Item);

        cleanup.AskCleanCommand.Execute(null);
        var again = Assert.IsType<CleanConfirmDialogViewModel>(host.Dialog);
        again.ConfirmCommand.Execute(null);
        await again.Run;

        Assert.Same(temp.Estimate, Assert.Single(host.Service.CleanedLists));
        Assert.False(cleanup.IsPreviewing);
    }

    [Fact]
    public async Task Says_so_when_there_is_nothing_to_clean()
    {
        var host = new DialogHost(new FakeCleanup((Empty, 0)));
        var cleanup = new CleanupViewModel(host.Service, host, () => { });

        await cleanup.MeasureAsync();

        Assert.Equal(CleanupPhase.Empty, cleanup.Phase);
        Assert.False(cleanup.CanClean);
    }

    private (CleanupViewModel Cleanup, DialogHost Host) Create(FakeExplorer? explorer = null)
    {
        var host = new DialogHost(new FakeCleanup((Temp, 3 * Gb), (Bin, Gb), (Update, 5 * Gb), (Empty, 0)));
        return (new CleanupViewModel(host.Service, host, () => { }, new CleanupHistory(Path.Combine(_dir, "h.jsonl")), explorer), host);
    }

    private sealed class DialogHost(FakeCleanup service) : IDialogHost
    {
        public FakeCleanup Service { get; } = service;

        public object? Dialog { get; private set; }

        public List<string> Toasts { get; } = [];

        public void ShowDialog(object dialog) => Dialog = dialog;

        public void CloseDialog() => Dialog = null;

        public void ShowToast(string message) => Toasts.Add(message);
    }
}

public class MemoryViewModelTests
{
    [Fact]
    public void Keeps_rows_and_their_expansion_across_updates()
    {
        var live = new LiveViewModel();
        var memory = new MemoryViewModel(live, new Host(), new FakeCloser(closes: true));
        live.Apply(Sample(Chrome(2 * Gb), Code(Gb)));
        memory.Update();
        var chrome = memory.Apps[0];

        memory.ToggleCommand.Execute(chrome);
        live.Apply(Sample(Code(3 * Gb), Chrome(2 * Gb)));
        memory.Update();

        Assert.Equal(["code", "chrome"], memory.Apps.Select(a => a.Key));
        Assert.Same(chrome, memory.Apps[1]);
        Assert.True(chrome.IsExpanded);
        Assert.Equal("−", chrome.Sign);
        Assert.Equal("Google LLC · 2 processes", chrome.Subtitle);
        Assert.Equal("GC", chrome.Initials);
        Assert.True(chrome.HasStorage);
    }

    [Fact]
    public async Task Closes_gracefully_then_offers_force_close()
    {
        var host = new Host();
        var closer = new FakeCloser(closes: false);
        var live = new LiveViewModel();
        var memory = new MemoryViewModel(live, host, closer);
        live.Apply(Sample(Chrome(Gb)));
        memory.Update();

        memory.CloseCommand.Execute(memory.Apps[0]);
        var dialog = Assert.IsType<CloseAppDialogViewModel>(host.Dialog);
        dialog.CloseCommand.Execute(null);
        await dialog.Run;

        Assert.Equal([10, 11], closer.Closed);
        Assert.True(dialog.IsRefused);
        dialog.ForceCloseCommand.Execute(null);
        Assert.Equal([10, 11], closer.Forced);
        Assert.Null(host.Dialog);
        Assert.Single(host.Toasts);
    }

    [Fact]
    public void Protected_windows_processes_cannot_be_closed()
    {
        var services = new ProcessExplanation("windows-services", ["svchost"], new("Windows services", "Serviços do Windows"),
            new("Background", "Fundo"), null, IsProtected: true, CanClose: false, ListServices: true);
        var group = new AppGroup("kb:windows-services", services, null, null, null, Gb, 1,
            [new AppProcess(4, "svchost", Gb, 1, ["Windows Update", "Print Spooler"])]);
        var live = new LiveViewModel();
        var memory = new MemoryViewModel(live, new Host(), new FakeCloser(closes: true));
        live.Apply(Sample(group));
        memory.Update();

        var row = Assert.Single(memory.Apps);

        Assert.False(row.CanClose);
        Assert.True(row.IsProtected);
        Assert.True(row.CanExpand);
        Assert.Equal("Windows Update, Print Spooler", Assert.Single(row.Children).Name);
    }

    private static AppGroup Chrome(long memory) => new("chrome", null, "Google Chrome", "Google LLC", @"C:\Program Files\Chrome\chrome.exe", memory, 2,
        [new AppProcess(10, "chrome", memory / 2, 1, []), new AppProcess(11, "chrome", memory / 2, 1, [])]);

    private static AppGroup Code(long memory) => new("code", null, "Visual Studio Code", "Microsoft", null, memory, 1,
        [new AppProcess(20, "code", memory, 1, [])]);

    private static MonitorSample Sample(params AppGroup[] apps) => new(new MemoryStatus(16 * Gb, 8 * Gb, 4 * Gb, Gb), 5, null, apps);

    private sealed class Host : IMemoryHost
    {
        public object? Dialog { get; private set; }

        public List<string> Toasts { get; } = [];

        public StorageCategory? CategoryOf(string imagePath) => StorageCategory.Apps;

        public void ShowStorageCategory(StorageCategory? category)
        {
        }

        public void ShowDialog(object dialog) => Dialog = dialog;

        public void CloseDialog() => Dialog = null;

        public void ShowToast(string message) => Toasts.Add(message);
    }
}

public class PcViewModelTests
{
    [Fact]
    public async Task Parts_show_live_status_from_the_rest_of_the_app()
    {
        var viewModel = new MainWindowViewModel(TestApp.Services());
        viewModel.Initialize();
        await viewModel.CurrentScan;
        var pc = viewModel.Pc;

        pc.SelectPartCommand.Execute(PcPart.Memory);

        Assert.True(pc.IsMemory);
        Assert.False(pc.ShowHint);
        Assert.Equal("02 / 06 · MEMORY", pc.PartIndexText);
        Assert.Equal(6, pc.PartLabels.Count);
        Assert.Equal(34, pc.MemoryAscii.Length); // [ + 32 + ]

        pc.AdvanceAnimation(TimeSpan.FromSeconds(2));
        Assert.Equal(1, pc.DriveArtProgress);
        Assert.Contains("100%", pc.DriveArt);
        Assert.Contains(pc.DriveLegend, line => line.Mark == "#" && line.Name == "Games");
    }

    [Fact]
    public void Zoom_stays_within_limits()
    {
        var pc = new MainWindowViewModel(TestApp.Services()).Pc;

        for (int i = 0; i < 20; i++)
        {
            pc.ZoomInCommand.Execute(null);
        }

        Assert.Equal(PcViewModel.MaxZoom, pc.Zoom);
        pc.ZoomResetCommand.Execute(null);
        Assert.Equal("100%", pc.ZoomText);
    }
}
