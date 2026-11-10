using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Pennyworth.App.Controls;

/// <summary>
/// A radar (spider) chart: one spoke per part of the PC, the load now as a filled shape and the
/// peak of the run as a dashed outline, so a lopsided load is visible at a glance. While
/// <see cref="IsSweeping"/> a radar beam turns, to show a test is running.
/// </summary>
public sealed class RadarChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<string>?> LabelsProperty =
        AvaloniaProperty.Register<RadarChart, IReadOnlyList<string>?>(nameof(Labels));

    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<RadarChart, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IReadOnlyList<double>?> PeaksProperty =
        AvaloniaProperty.Register<RadarChart, IReadOnlyList<double>?>(nameof(Peaks));

    /// <summary>Spokes when there are no labels (the small icon version).</summary>
    public static readonly StyledProperty<int> SpokesProperty =
        AvaloniaProperty.Register<RadarChart, int>(nameof(Spokes));

    public static readonly StyledProperty<bool> IsSweepingProperty =
        AvaloniaProperty.Register<RadarChart, bool>(nameof(IsSweeping));

    /// <summary>Above this share a spoke's label and dot turn to the accent colour.</summary>
    public const double Hot = 0.85;

    private const double LabelRoom = 34;
    private const double SweepSeconds = 4;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private bool _attached;

    static RadarChart()
    {
        AffectsRender<RadarChart>(LabelsProperty, ValuesProperty, PeaksProperty, SpokesProperty, IsSweepingProperty);
    }

    public RadarChart()
    {
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
        _timer.Tick += (_, _) => InvalidateVisual();
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<string>? Labels
    {
        get => GetValue(LabelsProperty);
        set => SetValue(LabelsProperty, value);
    }

    /// <summary>One value per spoke, 0 to 1.</summary>
    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>The highest value per spoke so far, 0 to 1.</summary>
    public IReadOnlyList<double>? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public int Spokes
    {
        get => GetValue(SpokesProperty);
        set => SetValue(SpokesProperty, value);
    }

    public bool IsSweeping
    {
        get => GetValue(IsSweepingProperty);
        set => SetValue(IsSweepingProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsSweepingProperty)
        {
            UpdateTimer();
        }
    }

    // Animates only while sweeping and on screen.
    private void UpdateTimer()
    {
        if (IsSweeping && _attached)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    public override void Render(DrawingContext context)
    {
        int count = Labels?.Count ?? (Spokes > 0 ? Spokes : Values?.Count ?? 0);
        // Labels need room around the chart; the small icon version has none.
        double radius = (Math.Min(Bounds.Width, Bounds.Height) / 2) - (Labels is { Count: > 0 } ? LabelRoom : 1);
        if (count < 3 || radius <= 4)
        {
            return;
        }

        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        Point At(int spoke, double share)
        {
            double angle = (-Math.PI / 2) + (spoke * 2 * Math.PI / count);
            return new Point(center.X + (Math.Cos(angle) * radius * share), center.Y + (Math.Sin(angle) * radius * share));
        }

        // Rings at 25, 50, 75 and 100 %; the outer one stronger.
        var ring = new Pen(this.Brush("Brush.Border"), 1);
        var outer = new Pen(this.Brush("Brush.BorderStrong"), 1);
        for (int r = 1; r <= 4; r++)
        {
            context.DrawGeometry(null, r == 4 ? outer : ring, Polygon(count, i => At(i, r / 4d)));
        }

        for (int i = 0; i < count; i++)
        {
            context.DrawLine(ring, center, At(i, 1));
        }

        if (IsSweeping)
        {
            DrawSweep(context, center, radius);
        }

        IBrush accent = this.Brush("Brush.Accent");
        if (Peaks is { Count: > 0 } peaks)
        {
            var dashed = new Pen(this.Brush("Brush.TextSecondary"), 1, new DashStyle([3, 3], 0));
            context.DrawGeometry(null, dashed, Polygon(count, i => At(i, Share(peaks, i))));
        }

        if (Values is { Count: > 0 } values)
        {
            context.DrawGeometry(this.Brush("Brush.Accent", 0.18), new Pen(accent, 1.5), Polygon(count, i => At(i, Share(values, i))));
            for (int i = 0; i < count; i++)
            {
                double share = Share(values, i);
                context.DrawEllipse(share >= Hot ? accent : this.Brush("Brush.TextPrimary"), null, At(i, share), 3, 3);
            }
        }

        DrawLabels(context, count, At, center);
    }

    private void DrawSweep(DrawingContext context, Point center, double radius)
    {
        // A beam with a fading trail, a full turn every few seconds.
        double turn = _clock.Elapsed.TotalSeconds / SweepSeconds * 2 * Math.PI;
        const int trail = 14;
        for (int t = trail; t >= 0; t--)
        {
            double angle = turn - (t * 0.035);
            var end = new Point(center.X + (Math.Cos(angle) * radius), center.Y + (Math.Sin(angle) * radius));
            double opacity = t == 0 ? 0.9 : 0.28 * (1 - (t / (double)trail));
            context.DrawLine(new Pen(this.Brush("Brush.Accent", opacity), t == 0 ? 1.5 : 2.5), center, end);
        }

        context.DrawEllipse(this.Brush("Brush.Accent"), null, center, 2.5, 2.5);
    }

    private void DrawLabels(DrawingContext context, int count, Func<int, double, Point> at, Point center)
    {
        var typeface = new Typeface(this.MonoFont());
        for (int i = 0; i < count; i++)
        {
            string text = Labels is { } labels && i < labels.Count ? labels[i] : "";
            if (text.Length == 0)
            {
                continue;
            }

            bool hot = Values is { } values && Share(values, i) >= Hot;
            var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10,
                hot ? this.Brush("Brush.Accent") : this.Brush("Brush.TextSecondary"));
            Point tip = at(i, 1);
            Point anchor = at(i, 1 + (16 / Math.Max(Point.Distance(center, tip), 1)));

            // Left of centre the text ends at the anchor, right of it starts there, on the axis it's centred.
            double dx = anchor.X - center.X;
            double x = Math.Abs(dx) < 4 ? anchor.X - (formatted.Width / 2) : dx < 0 ? anchor.X - formatted.Width : anchor.X;
            double y = anchor.Y - (formatted.Height / 2);
            context.DrawText(formatted, new Point(x, y));
        }
    }

    private static double Share(IReadOnlyList<double> values, int i) => i < values.Count ? Math.Clamp(values[i], 0, 1) : 0;

    private static StreamGeometry Polygon(int count, Func<int, Point> point)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            g.BeginFigure(point(0), isFilled: true);
            for (int i = 1; i < count; i++)
            {
                g.LineTo(point(i));
            }

            g.EndFigure(isClosed: true);
        }

        return geometry;
    }
}
