using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Pennyworth.App.Controls;

/// <summary>
/// Vertical bars for the last N readings (the CPU chart). The newest bar uses
/// <see cref="HighlightBrush"/>; values above <see cref="Maximum"/> fill the height.
/// </summary>
public sealed class BarChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<BarChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<BarChart, double>(nameof(Maximum), 100);

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<BarChart, IBrush?>(nameof(BarBrush));

    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<BarChart, IBrush?>(nameof(HighlightBrush));

    static BarChart()
    {
        AffectsRender<BarChart>(ValuesProperty, MaximumProperty, BarBrushProperty, HighlightBrushProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public IBrush? HighlightBrush
    {
        get => GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Values is not { Count: > 0 } values || Maximum <= 0)
        {
            return;
        }

        const double gap = 1;
        double width = (Bounds.Width - (gap * (values.Count - 1))) / values.Count;
        for (int i = 0; i < values.Count; i++)
        {
            double height = Math.Clamp(values[i] / Maximum, 0, 1) * Bounds.Height;
            IBrush? brush = i == values.Count - 1 ? HighlightBrush ?? BarBrush : BarBrush;
            if (brush is not null && height > 0)
            {
                context.DrawRectangle(brush, null, new Rect(i * (width + gap), Bounds.Height - height, Math.Max(width, 0.5), height));
            }
        }
    }
}
