using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Controls;

/// <summary>
/// Plays one row of a Codex pet spritesheet: 8 columns, one row per <see cref="PetAnimation"/>,
/// with the frame timings the Codex pets are drawn for.
/// </summary>
public sealed class PetSprite : Control
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<PetSprite, string?>(nameof(Source));

    /// <summary>9 for a classic sheet, 11 for a v2 one.</summary>
    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<PetSprite, int>(nameof(Rows), 9);

    public static readonly StyledProperty<PetAnimation> AnimationProperty =
        AvaloniaProperty.Register<PetSprite, PetAnimation>(nameof(Animation));

    /// <summary>False shows the first frame only, for people who turned animations off.</summary>
    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<PetSprite, bool>(nameof(IsAnimated), true);

    private readonly DispatcherTimer _timer = new();
    private Bitmap? _sheet;
    private int _frame;
    private bool _attached;

    static PetSprite()
    {
        AffectsRender<PetSprite>(SourceProperty, RowsProperty, AnimationProperty);
    }

    public PetSprite()
    {
        _timer.Tick += (_, _) => Advance();
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public PetAnimation Animation
    {
        get => GetValue(AnimationProperty);
        set => SetValue(AnimationProperty, value);
    }

    public bool IsAnimated
    {
        get => GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    /// <summary>How long each frame of an animation holds, in milliseconds; the last frame rests longer.</summary>
    public static IReadOnlyList<int> Frames(PetAnimation animation) => animation switch
    {
        PetAnimation.Idle => [280, 110, 110, 140, 140, 320],
        PetAnimation.RunningRight or PetAnimation.RunningLeft => Uniform(8, 120, 220),
        PetAnimation.Waving => Uniform(4, 140, 280),
        PetAnimation.Jumping => Uniform(5, 140, 280),
        PetAnimation.Failed => Uniform(8, 140, 240),
        PetAnimation.Waiting => Uniform(6, 150, 260),
        PetAnimation.Running => Uniform(6, 120, 220),
        _ => Uniform(6, 150, 280),
    };

    private static int[] Uniform(int count, int duration, int last) =>
        [.. Enumerable.Range(0, count).Select(i => i == count - 1 ? last : duration)];

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            LoadSheet();
        }
        else if (change.Property == AnimationProperty || change.Property == IsAnimatedProperty)
        {
            _frame = 0;
            Schedule();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Schedule();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _timer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        if (_sheet is not { } sheet || Rows <= 0)
        {
            return;
        }

        double cellWidth = sheet.Size.Width / 8;
        double cellHeight = sheet.Size.Height / Rows;
        int row = Math.Min((int)Animation, Rows - 1);
        var source = new Rect(_frame * cellWidth, row * cellHeight, cellWidth, cellHeight);

        // Fit the 192×208 frame into the control, centred, standing on its bottom edge.
        double scale = Math.Min(Bounds.Width / cellWidth, Bounds.Height / cellHeight);
        double width = cellWidth * scale;
        double height = cellHeight * scale;
        context.DrawImage(sheet, source, new Rect((Bounds.Width - width) / 2, Bounds.Height - height, width, height));
    }

    private void LoadSheet()
    {
        _sheet?.Dispose();
        _sheet = null;
        if (Source is { Length: > 0 } path)
        {
            try
            {
                _sheet = new Bitmap(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                // A pet that can't be drawn just isn't drawn; the rest of the window is unaffected.
            }
        }

        _frame = 0;
        Schedule();
    }

    private void Advance()
    {
        _frame = (_frame + 1) % Frames(Animation).Count;
        Schedule();
        InvalidateVisual();
    }

    private void Schedule()
    {
        _timer.Stop();
        if (_attached && _sheet is not null && IsAnimated)
        {
            _timer.Interval = TimeSpan.FromMilliseconds(Frames(Animation)[Math.Min(_frame, Frames(Animation).Count - 1)]);
            _timer.Start();
        }
    }
}
