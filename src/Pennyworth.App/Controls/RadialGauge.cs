using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Pennyworth.App.Controls;

/// <summary>
/// A 270° dial for one reading (0 to 1): a track, the value arc (accent once it is
/// <see cref="Hot"/>) and a tick at the peak. The number itself is laid over it in XAML.
/// </summary>
public sealed class RadialGauge : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<RadialGauge, double>(nameof(Value));

    public static readonly StyledProperty<double> PeakProperty =
        AvaloniaProperty.Register<RadialGauge, double>(nameof(Peak));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<RadialGauge, double>(nameof(Thickness), 6);

    public const double Hot = 0.85;

    private const double Start = 135;
    private const double Sweep = 270;

    static RadialGauge()
    {
        AffectsRender<RadialGauge>(ValueProperty, PeakProperty, ThicknessProperty);
    }

    public RadialGauge() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Peak
    {
        get => GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        double radius = (Math.Min(Bounds.Width, Bounds.Height) / 2) - Thickness;
        if (radius <= 2)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        double value = Math.Clamp(Value, 0, 1);
        context.DrawGeometry(null, new Pen(this.Brush("Brush.Track"), Thickness, lineCap: PenLineCap.Flat), Arc(center, radius, Start, Sweep));
        if (value > 0.002)
        {
            IBrush brush = value >= Hot ? this.Brush("Brush.Accent") : this.Brush("Brush.TextPrimary");
            context.DrawGeometry(null, new Pen(brush, Thickness, lineCap: PenLineCap.Flat), Arc(center, radius, Start, Sweep * value));
        }

        // Scale ticks every 10 %, inside the arc.
        var tick = new Pen(this.Brush("Brush.BorderStrong"), 1);
        for (int i = 0; i <= 10; i++)
        {
            double angle = Radians(Start + (Sweep * i / 10));
            double inner = radius - Thickness - (i % 5 == 0 ? 6 : 3);
            context.DrawLine(tick, Polar(center, radius - Thickness - 1, angle), Polar(center, inner, angle));
        }

        if (Peak > 0.002)
        {
            double angle = Radians(Start + (Sweep * Math.Clamp(Peak, 0, 1)));
            context.DrawLine(new Pen(this.Brush("Brush.Accent"), 2), Polar(center, radius - (Thickness / 2) - 2, angle), Polar(center, radius + (Thickness / 2) + 2, angle));
        }
    }

    private static StreamGeometry Arc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(Polar(center, radius, Radians(startDegrees)), isFilled: false);
            g.ArcTo(Polar(center, radius, Radians(startDegrees + sweepDegrees)), new Size(radius, radius), 0,
                isLargeArc: sweepDegrees > 180, SweepDirection.Clockwise);
            g.EndFigure(isClosed: false);
        }

        return geometry;
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static Point Polar(Point center, double radius, double angle) =>
        new(center.X + (Math.Cos(angle) * radius), center.Y + (Math.Sin(angle) * radius));
}
