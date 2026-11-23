using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Views;

/// <summary>
/// The pet's own little window: borderless, see-through and above the other apps, in the corner
/// over the clock until the person drags it somewhere else. It grows upwards when the pet talks,
/// so the animal stays where it was put.
/// </summary>
public sealed partial class PetWindow : Window
{
    /// <summary>Space between the pet and the edge of the screen, in device-independent pixels.</summary>
    private const int EdgeMargin = 12;

    /// <summary>The bottom-right corner the window keeps, in screen pixels; null until first shown.</summary>
    private PixelPoint? _anchor;
    private bool _placing;

    public PetWindow() => InitializeComponent();

    /// <summary>A double click on the pet.</summary>
    public event EventHandler? OpenRequested;

    private PetViewModel? ViewModel => DataContext as PetViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_anchor is null && Screens.ScreenFromWindow(this) is { } screen)
        {
            int margin = (int)(EdgeMargin * screen.Scaling);
            _anchor = new PixelPoint(screen.WorkingArea.Right - margin, screen.WorkingArea.Bottom - margin);
        }

        Place();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
        {
            Place();
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        if (ViewModel is { } pet)
        {
            pet.IsHovered = true;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (ViewModel is { } pet)
        {
            pet.IsHovered = false;
        }
    }

    private void OnPetPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            OpenRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        BeginMoveDrag(e);
        _anchor = Position + Size();
    }

    private void OnBubblePressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
        {
            ViewModel?.OpenSpeechCommand.Execute(null);
        }
    }

    /// <summary>Moves the window so its bottom-right corner sits on the anchor.</summary>
    private void Place()
    {
        if (_anchor is not { } anchor || _placing)
        {
            return;
        }

        _placing = true;
        Position = anchor - Size();
        _placing = false;
    }

    private PixelPoint Size() => new((int)(ClientSize.Width * RenderScaling), (int)(ClientSize.Height * RenderScaling));
}
