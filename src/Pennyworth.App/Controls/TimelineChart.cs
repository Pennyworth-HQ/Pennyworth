using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Pennyworth.App.Controls;

/// <summary>
/// A metric over hours or days: one bar per time bucket for its busiest second, a line for its
/// average, and the bars that reached <see cref="Threshold"/> in <see cref="HotBrush"/>. NaN
/// marks a bucket with no data (the app was closed): no bar, and a break in the line. Hovering
/// shows that bucket's <see cref="Labels"/> entry.
/// </summary>
public sealed class TimelineChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> PeaksProperty =
        AvaloniaProperty.Register<TimelineChart, IReadOnlyList<double>?>(nameof(Peaks));

    public static readonly StyledProperty<IReadOnlyList<double>?> AveragesProperty =
        AvaloniaProperty.Register<TimelineChart, IReadOnlyList<double>?>(nameof(Averages));

    public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty =
        AvaloniaProperty.Register<TimelineChart, IReadOnlyList<string>?>(nameof(Labels));

    public static readonly StyledProperty<double> ThresholdProperty =
        AvaloniaProperty.Register<TimelineChart, double>(nameof(Threshold), 90);

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<TimelineChart, IBrush?>(nameof(BarBrush));

    public static readonly StyledProperty<IBrush?> HotBrushProperty =
        AvaloniaProperty.Register<TimelineChart, IBrush?>(nameof(HotBrush));

    public static readonly StyledProperty<IBrush?> LineBrushProperty =
        AvaloniaProperty.Register<TimelineChart, IBrush?>(nameof(LineBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<TimelineChart, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<TimelineChart, IBrush?>(nameof(TextBrush));

    private const double Maximum = 100;
    private int? _hover;

    static TimelineChart()
    {
        AffectsRender<TimelineChart>(PeaksProperty, AveragesProperty, LabelsProperty, ThresholdProperty,
            BarBrushProperty, HotBrushProperty, LineBrushProperty, GridBrushProperty, TextBrushProperty);
    }

    public IReadOnlyList<double>? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public IReadOnlyList<double>? Averages
    {
        get => GetValue(AveragesProperty);
        set => SetValue(AveragesProperty, value);
    }

    public IReadOnlyList<string>? Labels
    {
        get => GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    public double Threshold
    {
        get => GetValue(ThresholdProperty);
        set => SetValue(ThresholdProperty, value);
    }

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public IBrush? HotBrush
    {
        get => GetValue(HotBrushProperty);
        set => SetValue(HotBrushProperty, value);
    }

    public IBrush? LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        int count = Peaks?.Count ?? 0;
        int? index = count == 0 || Bounds.Width <= 0
            ? null
            : Math.Clamp((int)(e.GetPosition(this).X / Bounds.Width * count), 0, count - 1);
        if (index != _hover)
        {
            _hover = index;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        // Transparent fill so the whole area takes the pointer, not only the bars.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        double width = Bounds.Width, height = Bounds.Height;
        if (GridBrush is { } grid)
        {
            var pen = new Pen(grid, 1);
            var dashed = new Pen(grid, 1, new DashStyle([3, 3], 0));
            context.DrawLine(pen, new Point(0, height - 0.5), new Point(width, height - 0.5));
            context.DrawLine(pen, new Point(0, Y(50)), new Point(width, Y(50)));
            context.DrawLine(dashed, new Point(0, Y(Threshold)), new Point(width, Y(Threshold)));
        }

        if (Peaks is not { Count: > 0 } peaks)
        {
            return;
        }

        double slot = width / peaks.Count;
        double bar = Math.Max(slot - (slot > 3 ? 1 : 0), 0.6);
        for (int i = 0; i < peaks.Count; i++)
        {
            if (double.IsNaN(peaks[i]))
            {
                continue;
            }

            IBrush? brush = peaks[i] >= Threshold ? HotBrush ?? BarBrush : BarBrush;
            double top = Y(peaks[i]);
            if (brush is not null && height - top > 0)
            {
                context.DrawRectangle(brush, null, new Rect(i * slot, top, bar, Math.Max(height - top, 1)));
            }
        }

        if (Averages is { } averages && LineBrush is { } line)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext g = geometry.Open())
            {
                bool open = false;
                for (int i = 0; i < averages.Count; i++)
                {
                    if (double.IsNaN(averages[i]))
                    {
                        if (open)
                        {
                            g.EndFigure(false);
                            open = false;
                        }

                        continue;
                    }

                    var point = new Point((i * slot) + (bar / 2), Y(averages[i]));
                    if (!open)
                    {
                        g.BeginFigure(point, false);
                        open = true;
                    }
                    else
                    {
                        g.LineTo(point);
                    }
                }

                if (open)
                {
                    g.EndFigure(false);
                }
            }

            context.DrawGeometry(null, new Pen(line, 1.5), geometry);
        }

        if (_hover is { } hover && hover < peaks.Count)
        {
            DrawHover(context, hover, slot, bar);
        }
    }

    private void DrawHover(DrawingContext context, int index, double slot, double bar)
    {
        double x = (index * slot) + (bar / 2);
        if (TextBrush is { } textBrush)
        {
            context.DrawLine(new Pen(textBrush, 1), new Point(x, 0), new Point(x, Bounds.Height));
        }

        if (Labels is not { } labels || index >= labels.Count || TextBrush is null)
        {
            return;
        }

        var typeface = new Typeface(this.TryFindResource("FontFamily.Mono", ActualThemeVariant, out object? font) && font is FontFamily family ? family : FontFamily.Default);
        var text = new FormattedText(labels[index], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10.5, TextBrush);
        double left = x + 6 + text.Width > Bounds.Width ? x - 6 - text.Width : x + 6;
        var background = new Rect(left - 4, 0, text.Width + 8, text.Height + 4);
        if (this.TryFindResource("Brush.Surface", ActualThemeVariant, out object? value) && value is IBrush surface)
        {
            context.DrawRectangle(surface, GridBrush is { } grid ? new Pen(grid, 1) : null, background);
        }

        context.DrawText(text, new Point(left, 2));
    }

    private double Y(double value) => Bounds.Height - (Math.Clamp(value / Maximum, 0, 1) * Bounds.Height);
}
