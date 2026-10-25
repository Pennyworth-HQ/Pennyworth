using System.Globalization;
using Pennyworth.App.Resources;
using Pennyworth.Core.Formatting;
using Pennyworth.Core.Storage;
using Pennyworth.Monitoring;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>What the Memory &amp; CPU screen needs from the shell.</summary>
public interface IMemoryHost : IDialogHost
{
    /// <summary>The storage category an executable lives in (Games, Apps...), or null.</summary>
    StorageCategory? CategoryOf(string imagePath);

    void ShowStorageCategory(StorageCategory? category);
}

/// <summary>Live memory and CPU, with running apps grouped and explained.</summary>
public sealed partial class MemoryViewModel : ObservableObject
{
    /// <summary>Above this, the CPU or memory is "busy" rather than "at ease".</summary>
    private const double BusyCpu = 30;
    private const double BusyMemory = 0.875;

    private readonly IMemoryHost _host;
    private readonly IProcessCloser _closer;
    private readonly Dictionary<string, AppRowViewModel> _rows = [];

    public MemoryViewModel(LiveViewModel live, IMemoryHost host, IProcessCloser closer)
    {
        Live = live;
        _host = host;
        _closer = closer;
    }

    public LiveViewModel Live { get; }

    /// <summary>Synced in place every second, so rows aren't recreated and stay expanded.</summary>
    public System.Collections.ObjectModel.ObservableCollection<AppRowViewModel> Apps { get; } = [];

    public string CalmLine => Live.Cpu < BusyCpu && Live.MemoryUsedFraction < BusyMemory ? Strings.CalmYes : Strings.CalmNo;

    /// <summary>Rebuilds the rows from the latest sample, keeping each app's row (and its expansion).</summary>
    public void Update()
    {
        var seen = new HashSet<string>();
        var rows = new List<AppRowViewModel>();
        foreach (AppGroup group in Live.Apps)
        {
            if (!_rows.TryGetValue(group.Key, out var row))
            {
                StorageCategory? category = group.ImagePath is { } path ? _host.CategoryOf(path) : null;
                _rows[group.Key] = row = new AppRowViewModel(group.Key, category is StorageCategory.Games or StorageCategory.Apps ? category : null);
            }

            row.Update(group);
            rows.Add(row);
            seen.Add(group.Key);
        }

        foreach (string gone in _rows.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            _rows.Remove(gone);
        }

        Sync(Apps, rows);
        OnPropertyChanged(nameof(CalmLine));
    }

    /// <summary>Re-reads every text after a language switch.</summary>
    public void RefreshTexts()
    {
        foreach (AppRowViewModel row in _rows.Values)
        {
            row.Refresh();
        }

        OnPropertyChanged(string.Empty);
    }

    /// <summary>Moves, inserts and removes so <paramref name="target"/> matches <paramref name="wanted"/>.</summary>
    private static void Sync(System.Collections.ObjectModel.ObservableCollection<AppRowViewModel> target, List<AppRowViewModel> wanted)
    {
        for (int i = 0; i < wanted.Count; i++)
        {
            int at = target.IndexOf(wanted[i]);
            if (at < 0)
            {
                target.Insert(i, wanted[i]);
            }
            else if (at != i)
            {
                target.Move(at, i);
            }
        }

        while (target.Count > wanted.Count)
        {
            target.RemoveAt(target.Count - 1);
        }
    }

    [RelayCommand]
    private static void Toggle(AppRowViewModel row)
    {
        if (row.CanExpand)
        {
            row.IsExpanded = !row.IsExpanded;
        }
    }

    [RelayCommand]
    private void Close(AppRowViewModel row)
    {
        if (row.CanClose)
        {
            _host.ShowDialog(new CloseAppDialogViewModel(row, _closer, _host));
        }
    }

    [RelayCommand]
    private void OpenStorage(AppRowViewModel row)
    {
        if (row.StorageCategory is { } category)
        {
            _host.ShowStorageCategory(category);
        }
    }
}

/// <summary>One app in the list, kept across updates so it stays expanded.</summary>
public sealed partial class AppRowViewModel(string key, StorageCategory? storageCategory) : ObservableObject
{
    /// <summary>Processes listed when expanded; the rest are summed in one "+ N" line.</summary>
    private const int MaxChildren = 8;

    public string Key { get; } = key;

    public AppGroup? Group { get; private set; }

    /// <summary>Set for apps installed in Games or Apps folders: offers "Storage".</summary>
    public StorageCategory? StorageCategory { get; } = storageCategory;

    public bool HasStorage => StorageCategory is not null;

    public string Name => Group?.Explanation?.Title.Get(CultureInfo.CurrentUICulture)
        ?? Group?.Description
        ?? Key;

    public string Subtitle
    {
        get
        {
            if (Group?.Explanation is { } explanation)
            {
                return explanation.Subtitle.Get(CultureInfo.CurrentUICulture);
            }

            int count = Group?.Processes.Count ?? 0;
            string processes = count == 1 ? Strings.ProcessCountOne : string.Format(Strings.ProcessCount, count);
            return Group?.Company is { } company ? $"{company} · {processes}" : processes;
        }
    }

    public string Initials => Ascii.Initials(Name);

    public long Memory => Group?.Memory ?? 0;

    public double Cpu => Group?.Cpu ?? 0;

    public string MemoryText => ByteSize.Format(Memory);

    public string CpuText => Cpu.ToString("0.0", CultureInfo.CurrentCulture) + "%";

    public string? Note => Group?.Explanation?.Note?.Get(CultureInfo.CurrentUICulture);

    public bool HasNote => Note is not null;

    public bool CanExpand => (Group?.Processes.Count ?? 0) > 1 || HasNote || Group?.Explanation?.ListServices == true;

    /// <summary>"+" to expand, "−" to collapse, "·" when there is nothing to show.</summary>
    public string Sign => !CanExpand ? "·" : IsExpanded ? "−" : "+";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Sign))]
    public partial bool IsExpanded { get; set; }

    public bool CanClose => Group?.CanClose ?? false;

    public bool IsProtected => Group?.IsProtected ?? false;

    public IReadOnlyList<AppChildViewModel> Children
    {
        get
        {
            if (Group is not { } group)
            {
                return [];
            }

            var children = group.Processes.Take(MaxChildren)
                .Select(p => new AppChildViewModel(
                    p.Services.Count > 0 ? string.Join(", ", p.Services) : $"{p.Name} · {p.Pid}",
                    ByteSize.Format(p.Memory),
                    p.Cpu.ToString("0.0", CultureInfo.CurrentCulture) + "%"))
                .ToList();
            var rest = group.Processes.Skip(MaxChildren).ToArray();
            if (rest.Length > 0)
            {
                children.Add(new AppChildViewModel(
                    $"+ {rest.Length}",
                    ByteSize.Format(rest.Sum(p => p.Memory)),
                    rest.Sum(p => p.Cpu).ToString("0.0", CultureInfo.CurrentCulture) + "%"));
            }

            return children;
        }
    }

    public IReadOnlyList<int> Pids => Group?.Processes.Select(p => p.Pid).ToArray() ?? [];

    internal void Update(AppGroup group)
    {
        Group = group;
        OnPropertyChanged(string.Empty);
    }

    internal void Refresh() => OnPropertyChanged(string.Empty);
}

public sealed record AppChildViewModel(string Name, string MemoryText, string CpuText);

public enum CloseAppState
{
    Asking,
    Closing,

    /// <summary>The app didn't close the normal way: offer to force it.</summary>
    Refused,
}

/// <summary>"Close Google Chrome?" Closes the normal way first; force only after the app refuses.</summary>
public sealed partial class CloseAppDialogViewModel(AppRowViewModel row, IProcessCloser closer, IDialogHost host) : ObservableObject
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(6);

    public AppRowViewModel Row { get; } = row;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(IsAsking), nameof(IsClosing), nameof(IsRefused))]
    public partial CloseAppState State { get; private set; }

    public bool IsAsking => State == CloseAppState.Asking;

    public bool IsClosing => State == CloseAppState.Closing;

    public bool IsRefused => State == CloseAppState.Refused;

    public string Title => State == CloseAppState.Refused
        ? string.Format(Strings.ForceTitle, Row.Name)
        : string.Format(Strings.CloseTitle, Row.Name);

    public string Subtitle => State switch
    {
        CloseAppState.Refused => Strings.ForceSub,
        CloseAppState.Closing => Strings.Closing,
        _ => Strings.CloseSub,
    };

    /// <summary>The close started by <see cref="CloseCommand"/>; lets tests await it.</summary>
    public Task Run { get; private set; } = Task.CompletedTask;

    [RelayCommand]
    private void Cancel() => host.CloseDialog();

    [RelayCommand]
    private void Close() => Run = CloseAsync();

    [RelayCommand]
    private void ForceClose()
    {
        closer.ForceClose(Row.Pids);
        Closed();
    }

    private async Task CloseAsync()
    {
        State = CloseAppState.Closing;
        bool closed = await closer.CloseAsync(Row.Pids, GracePeriod, CancellationToken.None);
        if (closed)
        {
            Closed();
        }
        else
        {
            State = CloseAppState.Refused;
        }
    }

    private void Closed()
    {
        host.CloseDialog();
        host.ShowToast(string.Format(Strings.ClosedToast, Row.Name, Row.MemoryText));
    }
}
