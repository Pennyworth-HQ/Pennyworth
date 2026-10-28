using System.Globalization;
using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>The rows of a Codex pet spritesheet, top to bottom.</summary>
public enum PetAnimation
{
    Idle,
    RunningRight,
    RunningLeft,
    Waving,
    Jumping,
    Failed,
    Waiting,
    Running,
    Review,
}

/// <summary>What the app is doing, which the pet acts out.</summary>
public enum PetActivity
{
    Resting,

    /// <summary>Scanning or cleaning.</summary>
    Working,

    /// <summary>The assistant is answering.</summary>
    Thinking,
}

public interface IPetHost
{
    void ShowToast(string message);

    void SavePetId(string? id);
}

/// <summary>
/// The pet on the desktop: a Codex pet the person downloaded (from Petdex or another pet site)
/// and added from its file. It says each notification in a speech bubble; bubbles queue up, stay
/// while the pointer is on them, and go away on their own. It runs while the app works.
/// </summary>
public sealed partial class PetViewModel : ObservableObject
{
    /// <summary>The view calls <see cref="Tick"/> this often.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(400);

    public static readonly TimeSpan SpeechDuration = TimeSpan.FromSeconds(9);

    /// <summary>A burst of alerts keeps the latest few, not a long line of bubbles.</summary>
    public const int MaxQueued = 3;

    private readonly Queue<PetSpeech> _queue = new();
    private readonly PetLibrary? _library;
    private readonly IFilePicker? _picker;
    private readonly IPetHost? _host;
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _speechEnds;
    private PetActivity _activity;
    private PetAnimation? _oneShot;
    private DateTimeOffset _oneShotUntil;

    public PetViewModel(PetLibrary? library, IFilePicker? picker, IPetHost? host, Func<DateTimeOffset>? clock = null, string? petId = null)
    {
        _library = library;
        _picker = picker;
        _host = host;
        _clock = clock ?? (() => DateTimeOffset.Now);
        Pet = petId is null ? null : library?.Load(petId);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(Name))]
    public partial PetPackage? Pet { get; private set; }

    public bool IsVisible => Pet is not null;

    /// <summary>Pets can be added only when the app has a pet library (tests may run without).</summary>
    public bool CanAddPets => _library is not null;

    public string Name => Pet?.DisplayName ?? "";

    [ObservableProperty]
    public partial PetAnimation Animation { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpeaking))]
    public partial PetSpeech? Speech { get; private set; }

    public bool IsSpeaking => Speech is not null;

    /// <summary>Set by the view while the pointer is on the pet: the bubble waits to be read.</summary>
    public bool IsHovered { get; set; }

    /// <summary>No animation, for people who turned animations off in Windows.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>The import started last; lets tests await it.</summary>
    public Task LastImport { get; private set; } = Task.CompletedTask;

    partial void OnPetChanged(PetPackage? value)
    {
        _queue.Clear();
        Speech = null;
        Update(_activity);
    }

    /// <summary>"Add a pet…": the file the person downloaded from a pet site.</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        if (_picker is null || _library is null)
        {
            return;
        }

        if (await _picker.PickFileAsync(Strings.PetPickerTitle, Strings.PetPickerFilter, PetLibrary.Patterns) is { } path)
        {
            await ImportAsync(path);
        }
    }

    /// <summary>Adds the pet in a .zip, folder, pet.json or spritesheet, and puts it on the desktop.</summary>
    public Task ImportAsync(string path) => LastImport = ImportCoreAsync(path);

    private async Task ImportCoreAsync(string path)
    {
        if (_library is not { } library)
        {
            return;
        }

        PetImportResult result = await Task.Run(() => library.Import(path));
        if (result.Pet is { } pet)
        {
            Pet = pet;
            _host?.SavePetId(pet.Id);
            Play(PetAnimation.Jumping, TimeSpan.FromSeconds(2));
            Say(string.Format(CultureInfo.CurrentCulture, Strings.PetHelloTitle, pet.DisplayName), Strings.PetHelloBody);
            return;
        }

        _host?.ShowToast(result.Error switch
        {
            PetImportError.BadSpritesheet => Strings.PetBadSpritesheet,
            PetImportError.NotAPet => Strings.PetNotAPet,
            _ => Strings.PetUnreadable,
        });
        Play(PetAnimation.Failed, TimeSpan.FromSeconds(2));
    }

    /// <summary>Takes the pet off the desktop; notifications go back to Windows.</summary>
    [RelayCommand]
    private void Remove()
    {
        if (Pet is { } pet)
        {
            Pet = null;
            _host?.SavePetId(null);
            _host?.ShowToast(string.Format(CultureInfo.CurrentCulture, Strings.PetRemoved, pet.DisplayName));
        }
    }

    /// <param name="onClick">Runs when the person clicks the bubble.</param>
    public void Say(string title, string message, Action? onClick = null)
    {
        if (!IsVisible)
        {
            return;
        }

        var speech = new PetSpeech(title, message, onClick);
        if (Speech is null)
        {
            Show(speech);
            return;
        }

        _queue.Enqueue(speech);
        while (_queue.Count > MaxQueued)
        {
            _queue.Dequeue();
        }
    }

    /// <summary>The bubble leaves when its time is up.</summary>
    public void Tick()
    {
        if (Speech is not null && !IsHovered && _clock() >= _speechEnds)
        {
            Next();
        }
        else
        {
            Update(_activity);
        }
    }

    /// <summary>Called every second with what the app is doing.</summary>
    public void Update(PetActivity activity)
    {
        _activity = activity;
        if (_oneShot is not null && _clock() >= _oneShotUntil)
        {
            _oneShot = null;
        }

        Animation = _oneShot ?? (IsSpeaking ? PetAnimation.Waving : activity switch
        {
            PetActivity.Working => PetAnimation.Running,
            PetActivity.Thinking => PetAnimation.Review,
            _ => PetAnimation.Idle,
        });
    }

    /// <summary>A click on the bubble: does what the notification offers, then moves on.</summary>
    [RelayCommand]
    private void OpenSpeech()
    {
        Action? onClick = Speech?.OnClick;
        Next();
        onClick?.Invoke();
    }

    [RelayCommand]
    private void Dismiss() => Next();

    private void Next()
    {
        if (_queue.TryDequeue(out PetSpeech? next))
        {
            Show(next);
        }
        else
        {
            Speech = null;
            Update(_activity);
        }
    }

    private void Show(PetSpeech speech)
    {
        Speech = speech;
        _speechEnds = _clock() + SpeechDuration;
        Update(_activity);
    }

    private void Play(PetAnimation animation, TimeSpan duration)
    {
        _oneShot = animation;
        _oneShotUntil = _clock() + duration;
        Update(_activity);
    }
}

public sealed record PetSpeech(string Title, string Message, Action? OnClick)
{
    public bool CanOpen => OnClick is not null;
}
