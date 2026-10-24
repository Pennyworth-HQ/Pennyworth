using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Controls;

/// <summary>
/// A horizontal bar split into coloured segments. Colours come from the theme resources
/// <c>Brush.Category.{ColorKey}</c>, so a new design only changes the tokens file.
/// </summary>
public sealed class SegmentedBar : Control
{
    public static readonly StyledProperty<IReadOnlyList<BarSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<SegmentedBar, IReadOnlyList<BarSegment>?>(nameof(Segments));

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<SegmentedBar, IBrush?>(nameof(TrackBrush));

    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<SegmentedBar, double>(nameof(Radius), 6);

    static SegmentedBar()
    {
        AffectsRender<SegmentedBar>(SegmentsProperty, TrackBrushProperty, RadiusProperty);
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

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        var clip = new RoundedRect(bounds, Radius);
        if (TrackBrush is { } track)
        {
            context.DrawRectangle(track, null, clip);
        }

        using (context.PushClip(clip))
        {
            double x = 0;
            foreach (BarSegment segment in Segments ?? [])
            {
                double width = segment.Fraction * bounds.Width;
                if (width <= 0)
                {
                    continue;
                }

                if (this.TryFindResource($"Brush.Category.{segment.ColorKey}", ActualThemeVariant, out object? resource) && resource is IBrush brush)
                {
                    context.DrawRectangle(brush, null, new Rect(x, 0, width, bounds.Height));
                }

                x += width;
            }
        }
    }
}
