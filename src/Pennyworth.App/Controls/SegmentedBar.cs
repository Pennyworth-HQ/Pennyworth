using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Controls;

/// <summary>
/// A horizontal bar split into segments. Following the design, every category has its own
/// pattern (solid, stripes, dots, ...) so the bar never relies on colour alone. Colours come
/// from the theme resources <c>Brush.Category.{ColorKey}</c>; the highlighted category is
/// drawn with <c>Brush.Accent</c>.
/// </summary>
public sealed class SegmentedBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<BarSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentedBar, IReadOnlyList<BarSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<SegmentedBar, double>(nameof(Radius));

    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<SegmentedBar, double>(nameof(Gap));

    public static readonly StyledProperty<string?> HighlightKeyProperty =
        AvaloniaProperty.Register<SegmentedBar, string?>(nameof(HighlightKey));

    public static readonly StyledProperty<bool> IsHighlightedProperty =
        AvaloniaProperty.Register<SegmentedBar, bool>(nameof(IsHighlighted));

    static SegmentedBar()
    {
        AffectsRender<SegmentedBar>(SegmentsProperty, TrackBrushProperty, RadiusProperty, GapProperty, HighlightKeyProperty, IsHighlightedProperty);
    }

    public IReadOnlyList<BarSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public IBrush? TrackBrush
    {
        get => GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    /// <summary>Space left between segments.</summary>
    public double Gap
    {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    /// <summary>The segment with this colour key is drawn in the accent colour.</summary>
    public string? HighlightKey
    {
        get => GetValue(HighlightKeyProperty);
        set => SetValue(HighlightKeyProperty, value);
    }

    /// <summary>Draws every segment in the accent colour (used for selected swatches).</summary>
    public bool IsHighlighted
    {
        get => GetValue(IsHighlightedProperty);
        set => SetValue(IsHighlightedProperty, value);
    }

    /// <summary>Raised with the colour key of the segment that was clicked.</summary>
    public event EventHandler<string>? SegmentPressed;

    public SegmentedBar()
    {
        // Brushes are looked up while rendering, so redraw when Windows switches light/dark.
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (SegmentPressed is null)
        {
            return;
        }

        var visible = (Segments ?? []).Where(segment => segment.Fraction > 0).ToArray();
        double available = Math.Max(Bounds.Width - (Gap * Math.Max(visible.Length - 1, 0)), 0);
        double x = e.GetPosition(this).X, start = 0;
        foreach (BarSegment segment in visible)
        {
            double end = start + (segment.Fraction * available);
            if (x >= start && x <= end + Gap)
            {
                SegmentPressed(this, segment.ColorKey);
                return;
            }

            start = end + Gap;
        }
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var clip = new RoundedRect(bounds, Radius);
        if (TrackBrush is { } track)
        {
            context.DrawRectangle(track, null, clip);
        }

        var visible = (Segments ?? []).Where(segment => segment.Fraction > 0).ToArray();
        double available = Math.Max(bounds.Width - (Gap * Math.Max(visible.Length - 1, 0)), 0);

        using (context.PushClip(clip))
        {
            double x = 0;
            foreach (BarSegment segment in visible)
            {
                double width = segment.Fraction * available;
                bool highlighted = IsHighlighted || segment.ColorKey == HighlightKey;
                string key = highlighted ? "Brush.Accent" : $"Brush.Category.{segment.ColorKey}";
                if (width >= 0.5 && this.TryFindResource(key, ActualThemeVariant, out object? resource) && resource is IBrush brush)
                {
                    var rect = new Rect(x, 0, width, bounds.Height);
                    using (context.PushClip(rect))
                    {
                        DrawPattern(context, segment.ColorKey, brush, rect);
                    }
                }

                x += width + Gap;
            }
        }
    }

    /// <summary>The patterns from the design, one per category.</summary>
    private static void DrawPattern(DrawingContext context, string colorKey, IBrush brush, Rect rect)
    {
        switch (colorKey)
        {
            case "Games":
                context.DrawRectangle(brush, null, rect);
                break;
            case "Apps":
                Diagonals(context, new Pen(brush, 2), rect, spacing: 8.5, rising: true);
                break;
            case "YourFiles":
                for (double y = rect.Top + 2.5; y < rect.Bottom; y += 5)
                {
                    for (double x = rect.Left + 2.5; x < rect.Right; x += 5)
                    {
                        context.DrawEllipse(brush, null, new Point(x, y), 1.4, 1.4);
                    }
                }

                break;
            case "Windows":
                for (double y = rect.Top; y < rect.Bottom; y += 5)
                {
                    context.DrawRectangle(brush, null, new Rect(rect.Left, y, rect.Width, 2));
                }

                break;
            case "HiddenSystem":
                var thin = new Pen(brush, 1);
                Diagonals(context, thin, rect, spacing: 7, rising: true);
                Diagonals(context, thin, rect, spacing: 7, rising: false);
                break;
            case "Other":
                for (double x = rect.Left; x < rect.Right; x += 4)
                {
                    context.DrawRectangle(brush, null, new Rect(x, rect.Top, 1, rect.Height));
                }

                break;
            default: // Unaccounted: a dashed line through the middle.
                double middle = rect.Top + (rect.Height / 2) - 1;
                for (double x = rect.Left; x < rect.Right; x += 8)
                {
                    context.DrawRectangle(brush, null, new Rect(x, middle, 4, 2));
                }

                break;
        }
    }

    private static void Diagonals(DrawingContext context, IPen pen, Rect rect, double spacing, bool rising)
    {
        double height = rect.Height;
        for (double x = rect.Left - height; x < rect.Right; x += spacing)
        {
            context.DrawLine(
                pen,
                new Point(x, rising ? rect.Bottom : rect.Top),
                new Point(x + height, rising ? rect.Top : rect.Bottom));
        }
    }
}
