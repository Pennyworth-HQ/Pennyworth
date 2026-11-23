using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Controls;

/// <summary>
/// Draws <see cref="AsciiPcRenderer"/> frames with the theme's monospace font: drag to rotate,
/// scroll to zoom, click a part to select it. Animates only while it is on screen.
/// </summary>
public sealed class AsciiPcView : Control
{
    public static readonly StyledProperty<PcPart> SelectedPartProperty =
        AvaloniaProperty.Register<AsciiPcView, PcPart>(nameof(SelectedPart), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<int> SelectedDiskProperty =
        AvaloniaProperty.Register<AsciiPcView, int>(nameof(SelectedDisk), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<AsciiPcView, double>(nameof(Zoom), 1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty =
        AvaloniaProperty.Register<AsciiPcView, IReadOnlyList<string>?>(nameof(Labels));

    public static readonly StyledProperty<IReadOnlyList<PcDisk>?> DisksProperty =
        AvaloniaProperty.Register<AsciiPcView, IReadOnlyList<PcDisk>?>(nameof(Disks));

    public static readonly StyledProperty<bool> ShowRainProperty =
        AvaloniaProperty.Register<AsciiPcView, bool>(nameof(ShowRain), true);

    private const double DragThreshold = 4;

    private readonly AsciiPcRenderer _renderer = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastTick;
    private PcPick? _hovered;
    private Point? _pressedAt;
    private bool _dragged;

    public AsciiPcView()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);

        // Sub-pixel anti-aliasing paints tiny glyphs in rainbow fringes; the design is monochrome.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
        _timer.Tick += (_, _) =>
        {
            double now = _clock.Elapsed.TotalSeconds;
            _renderer.Advance(Math.Min(now - _lastTick, 0.1));
            _lastTick = now;
            InvalidateVisual();
        };
    }

    public PcPart SelectedPart
    {
        get => GetValue(SelectedPartProperty);
        set => SetValue(SelectedPartProperty, value);
    }

    /// <summary>Which of <see cref="Disks"/> is selected when the drive is.</summary>
    public int SelectedDisk
    {
        get => GetValue(SelectedDiskProperty);
        set => SetValue(SelectedDiskProperty, value);
    }

    /// <summary>Target zoom, 0.5 to 2.6; the drawing eases toward it.</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    /// <summary>Text above each part, in <see cref="PcPart"/> order.</summary>
    public IReadOnlyList<string>? Labels
    {
        get => GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    /// <summary>The disks stacked in the drive bay, each with its name on the front.</summary>
    public IReadOnlyList<PcDisk>? Disks
    {
        get => GetValue(DisksProperty);
        set => SetValue(DisksProperty, value);
    }

    public bool ShowRain
    {
        get => GetValue(ShowRainProperty);
        set => SetValue(ShowRainProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lastTick = _clock.Elapsed.TotalSeconds;
        _timer.Start();
        ActualThemeVariantChanged += OnThemeChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        ActualThemeVariantChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ZoomProperty)
        {
            _renderer.ZoomTarget = Math.Clamp(Zoom, PcViewModel.MinZoom, PcViewModel.MaxZoom);
        }
        else if (change.Property == DisksProperty)
        {
            _renderer.Disks = Disks ?? [];
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _pressedAt = e.GetPosition(this);
        _dragged = false;
        _renderer.BeginDrag();
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        if (_pressedAt is { } start)
        {
            Vector delta = position - start;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > DragThreshold)
            {
                _dragged = true;
            }

            if (_dragged)
            {
                _renderer.DragBy(delta.X, delta.Y);
            }

            return;
        }

        _hovered = HitTest(position);
        Cursor = new Cursor(_hovered is null ? StandardCursorType.SizeAll : StandardCursorType.Hand);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragged && HitTest(e.GetPosition(this)) is { } pick)
        {
            if (pick.Part == PcPart.Drive)
            {
                SelectedDisk = pick.Disk;
            }

            SelectedPart = pick.Part;
        }

        _pressedAt = null;
        _renderer.EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        Zoom = Math.Clamp(Zoom * (e.Delta.Y > 0 ? 1.12 : 1 / 1.12), PcViewModel.MinZoom, PcViewModel.MaxZoom);
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var grid = Grid();
        if (grid.CellWidth <= 0 || !TryGetTypeface(out GlyphTypeface? typeface))
        {
            return;
        }

        AsciiFrame frame = _renderer.Render(_clock.Elapsed.TotalSeconds, new PcPick(SelectedPart, SelectedDisk), _hovered, ShowRain);
        double fontSize = grid.CellWidth / 0.6;
        double ascent = Math.Abs(typeface.Metrics.Ascent) / typeface.Metrics.DesignEmHeight * fontSize;

        IBrush rain = Brush("Brush.TextTertiary", 0.22);
        IBrush[] brushes = [rain, Brush("Brush.TextSecondary"), Brush("Brush.TextPrimary"), Brush("Brush.Accent")];
        for (int layer = 0; layer < 4; layer++)
        {
            DrawLayer(context, frame.Layers[layer], brushes[layer], typeface, fontSize, ascent, grid);
        }

        DrawLabels(context, frame.Labels, grid);
    }

    private (double CellWidth, double CellHeight, double Left, double Top) Grid()
    {
        double cellHeight = Math.Min(Bounds.Height / AsciiPcRenderer.Rows, Bounds.Width / AsciiPcRenderer.Columns / AsciiPcRenderer.CellAspect);
        double cellWidth = cellHeight * AsciiPcRenderer.CellAspect;
        double left = (Bounds.Width - (cellWidth * AsciiPcRenderer.Columns)) / 2;
        double top = (Bounds.Height - (cellHeight * AsciiPcRenderer.Rows)) / 2;
        return (cellWidth, cellHeight, left, top);
    }

    private PcPick? HitTest(Point position)
    {
        var grid = Grid();
        return grid.CellWidth <= 0 ? null
            : _renderer.HitTest((position.X - grid.Left) / grid.CellWidth, (position.Y - grid.Top) / grid.CellHeight);
    }

    /// <summary>One glyph run per row, holding only the non-space characters.</summary>
    private static void DrawLayer(
        DrawingContext context, char[] cells, IBrush brush, GlyphTypeface typeface, double fontSize, double ascent,
        (double CellWidth, double CellHeight, double Left, double Top) grid)
    {
        var glyphs = new List<GlyphInfo>(64);
        var characters = new List<char>(64);
        for (int row = 0; row < AsciiPcRenderer.Rows; row++)
        {
            glyphs.Clear();
            characters.Clear();
            int firstColumn = -1, previousColumn = -1;
            int offset = row * AsciiPcRenderer.Columns;
            for (int column = 0; column < AsciiPcRenderer.Columns; column++)
            {
                char c = cells[offset + column];
                if (c == ' ')
                {
                    continue;
                }

                if (firstColumn < 0)
                {
                    firstColumn = column;
                }
                else
                {
                    // Each glyph advances to the next drawn character, skipping the spaces between.
                    var last = glyphs[^1];
                    glyphs[^1] = new GlyphInfo(last.GlyphIndex, last.GlyphCluster, (column - previousColumn) * grid.CellWidth);
                }

                ushort index = typeface.CharacterToGlyphMap.TryGetGlyph(c, out ushort glyph) ? glyph : (ushort)0;
                glyphs.Add(new GlyphInfo(index, characters.Count, grid.CellWidth));
                characters.Add(c);
                previousColumn = column;
            }

            if (glyphs.Count == 0)
            {
                continue;
            }

            var origin = new Point(grid.Left + (firstColumn * grid.CellWidth), grid.Top + (row * grid.CellHeight) + ascent);
            using var run = new GlyphRun(typeface, fontSize, characters.ToArray(), glyphs.ToArray(), origin);
            context.DrawGlyphRun(brush, run);
        }
    }

    private void DrawLabels(DrawingContext context, IReadOnlyList<AsciiLabel> labels, (double CellWidth, double CellHeight, double Left, double Top) grid)
    {
        var typeface = new Typeface(FontFamilyResource());
        // Labels keep the design's size whatever the grid density.
        double size = Math.Max(grid.CellHeight * AsciiPcRenderer.Density * 2.6, 8);
        foreach (AsciiLabel label in labels)
        {
            string text = label.Text
                ?? (Labels is { } names && (int)label.Part < names.Count ? names[(int)label.Part] : label.Part.ToString());
            double labelSize = label.OnFront ? size * 0.8 : size;
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, labelSize,
                label.IsHighlighted ? Brush("Brush.OnAccent") : Brush(label.OnFront ? "Brush.TextPrimary" : "Brush.TextSecondary"));

            double padX = labelSize * 0.5, padY = labelSize * 0.2;
            double width = formatted.Width + (padX * 2), height = formatted.Height + (padY * 2);
            double x = grid.Left + (label.Column * grid.CellWidth) - (width / 2);
            double y = grid.Top + (label.Row * grid.CellHeight) - (label.OnFront ? height / 2 : height);
            if (label.IsHighlighted)
            {
                context.DrawRectangle(Brush("Brush.Accent"), null, new Rect(x, y, width, height));
            }
            else if (label.OnFront)
            {
                // A plate over the disk's edges so the name stays readable.
                context.DrawRectangle(Brush("Brush.Background", 0.85), new Pen(Brush("Brush.Border"), 1), new Rect(x, y, width, height));
            }

            context.DrawText(formatted, new Point(x + padX, y + padY));
        }
    }

    private bool TryGetTypeface([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GlyphTypeface? typeface) =>
        FontManager.Current.TryGetGlyphTypeface(new Typeface(FontFamilyResource()), out typeface);

    private FontFamily FontFamilyResource() =>
        this.TryFindResource("FontFamily.Mono", ActualThemeVariant, out object? value) && value is FontFamily family ? family : FontFamily.Default;

    private IBrush Brush(string key, double opacity = 1)
    {
        IBrush brush = this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush found ? found : Brushes.Gray;
        return opacity >= 1 || brush is not ISolidColorBrush solid ? brush : new SolidColorBrush(solid.Color, opacity);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();
}
