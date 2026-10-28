using Pennyworth.App.Resources;
using Pennyworth.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Pennyworth.App.ViewModels;

/// <summary>
/// The pet on the desktop: an ASCII animal that blinks while idle and says each notification in a
/// speech bubble. Bubbles queue up, stay while the pointer is on them, and go away on their own.
/// </summary>
public sealed partial class PetViewModel : ObservableObject
{
    /// <summary>The view calls <see cref="Tick"/> this often.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(400);

    public static readonly TimeSpan SpeechDuration = TimeSpan.FromSeconds(9);

    /// <summary>A burst of alerts keeps the latest few, not a long line of bubbles.</summary>
    public const int MaxQueued = 3;

    private readonly Queue<PetSpeech> _queue = new();
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _speechEnds;
    private int _tick;

    public PetViewModel(PetKind kind, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
        Kind = kind;
        Art = PetArt.Frame(kind, talking: false, 0);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial PetKind Kind { get; set; }

    public bool IsVisible => Kind != PetKind.None;

    /// <summary>The current frame of the animal.</summary>
    [ObservableProperty]
    public partial string Art { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSpeaking))]
    public partial PetSpeech? Speech { get; private set; }

    public bool IsSpeaking => Speech is not null;

    /// <summary>Set by the view while the pointer is on the pet: the bubble waits to be read.</summary>
    public bool IsHovered { get; set; }

    /// <summary>No animation, for people who turned animations off in Windows.</summary>
    public bool ReduceMotion { get; set; }

    partial void OnKindChanged(PetKind value)
    {
        if (value == PetKind.None)
        {
            _queue.Clear();
            Speech = null;
        }

        Art = PetArt.Frame(value, IsSpeaking, _tick);
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

    /// <summary>Next animation frame; the bubble leaves when its time is up.</summary>
    public void Tick()
    {
        _tick++;
        if (Speech is not null && !IsHovered && _clock() >= _speechEnds)
        {
            Next();
        }

        Art = PetArt.Frame(Kind, IsSpeaking, ReduceMotion ? 0 : _tick);
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
            Art = PetArt.Frame(Kind, talking: false, _tick);
        }
    }

    private void Show(PetSpeech speech)
    {
        Speech = speech;
        _speechEnds = _clock() + SpeechDuration;
        Art = PetArt.Frame(Kind, talking: true, _tick);
    }
}

public sealed record PetSpeech(string Title, string Message, Action? OnClick)
{
    public bool CanOpen => OnClick is not null;
}

/// <summary>One pet on the alerts screen's picker.</summary>
public sealed partial class PetChoiceViewModel(PetKind kind) : ObservableObject
{
    public PetKind Kind { get; } = kind;

    public string Name => PetArt.Name(Kind);

    public string Preview => Kind == PetKind.None ? "" : PetArt.Frame(Kind, talking: false, 0);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public void RefreshTexts() => OnPropertyChanged(nameof(Name));
}

/// <summary>
/// The pets, drawn in the same monospace as the 3D PC. Every frame of a pet has the same lines and
/// widths, so the animal never jumps while it blinks or talks.
/// </summary>
public static class PetArt
{
    /// <summary>Ticks per idle loop: eyes open, a blink on the last one.</summary>
    private const int IdleLoop = 12;

    public static IReadOnlyList<PetKind> All { get; } = Enum.GetValues<PetKind>();

    public static string Name(PetKind kind) => kind switch
    {
        PetKind.Cat => Strings.PetCat,
        PetKind.Dog => Strings.PetDog,
        PetKind.Owl => Strings.PetOwl,
        PetKind.Bunny => Strings.PetBunny,
        PetKind.Robot => Strings.PetRobot,
        PetKind.Ghost => Strings.PetGhost,
        _ => Strings.PetNone,
    };

    /// <summary>The frame at <paramref name="tick"/>: talking opens and closes the mouth, idle blinks now and then.</summary>
    public static string Frame(PetKind kind, bool talking, int tick)
    {
        if (kind == PetKind.None)
        {
            return "";
        }

        (string open, string blink, string talk) = Frames(kind);
        if (talking)
        {
            return tick % 2 == 0 ? talk : open;
        }

        return tick % IdleLoop == IdleLoop - 1 ? blink : open;
    }

    private static (string Open, string Blink, string Talk) Frames(PetKind kind) => kind switch
    {
        PetKind.Cat => (
            """
             /\_/\
            ( o.o )
             > ^ <
            """,
            """
             /\_/\
            ( -.- )
             > ^ <
            """,
            """
             /\_/\
            ( o.o )
             > O <
            """),
        PetKind.Dog => (
            """
             /^ ^\
            / o o \
            V\ Y /V
             / - \
            """,
            """
             /^ ^\
            / - - \
            V\ Y /V
             / - \
            """,
            """
             /^ ^\
            / o o \
            V\ Y /V
             / O \
            """),
        PetKind.Owl => (
            """
             ,___,
             (O,O)
             /)_)
              ""
            """,
            """
             ,___,
             (-,-)
             /)_)
              ""
            """,
            """
             ,___,
             (O.O)
             /)_)
              ""
            """),
        PetKind.Bunny => (
            """
            (\_/)
            (o.o)
            (> <)
            """,
            """
            (\_/)
            (-.-)
            (> <)
            """,
            """
            (\_/)
            (oOo)
            (> <)
            """),
        PetKind.Robot => (
            """
              [___]
              |o_o|
             /|___|\
              d   b
            """,
            """
              [___]
              |-_-|
             /|___|\
              d   b
            """,
            """
              [___]
              |oOo|
             /|___|\
              d   b
            """),
        _ => (
            """
             .-.
            (o o)
            | - |
            '~~~'
            """,
            """
             .-.
            (- -)
            | - |
            '~~~'
            """,
            """
             .-.
            (o o)
            | O |
            '~~~'
            """),
    };
}
