using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

public enum UnlockDialogState
{
    Asking,

    /// <summary>Windows' permission prompt is open.</summary>
    Waiting,

    Declined,
}

/// <summary>"May I see the rest of the drive?": why admin is needed, what it will and won't do.</summary>
public sealed partial class UnlockDialogViewModel(MainWindowViewModel shell) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsking), nameof(IsWaiting), nameof(IsDeclined))]
    [NotifyCanExecuteChangedFor(nameof(UnlockCommand))]
    public partial UnlockDialogState State { get; private set; }

    public bool IsAsking => State is UnlockDialogState.Asking or UnlockDialogState.Waiting;

    public bool IsWaiting => State == UnlockDialogState.Waiting;

    public bool IsDeclined => State == UnlockDialogState.Declined;

    /// <summary>The unlock started by <see cref="UnlockCommand"/>; lets tests await it.</summary>
    public Task Run { get; private set; } = Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanUnlock))]
    private void Unlock()
    {
        State = UnlockDialogState.Waiting;
        Run = shell.UnlockAsync(this);
    }

    private bool CanUnlock() => State == UnlockDialogState.Asking;

    [RelayCommand]
    private void NotNow() => State = UnlockDialogState.Declined;

    [RelayCommand]
    private void Close() => shell.CloseDialog();

    internal void Declined() => State = UnlockDialogState.Declined;
}
