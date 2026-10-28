using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Pennyworth.App.Controls;

/// <summary>
/// Line icons for the parts of the PC a stress test watches, drawn on a 24×24 grid in the
/// design's ink colour. <see cref="Level"/> fills the thermometer and, once
/// <see cref="Hot"/>, turns the icon to the accent colour.
/// </summary>
public sealed class ComponentIcon : Control
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<ComponentIcon, string?>(nameof(Kind));

    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<ComponentIcon, double>(nameof(Level));

    public const double Hot = 0.85;

    static ComponentIcon()
    {
        AffectsRender<ComponentIcon>(KindProperty, LevelProperty);
    }

    public ComponentIcon() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    /// <summary>Cpu, Memory, Disk, Gpu, Temperature, Power or Stress.</summary>
    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        double scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        if (scale <= 0)
        {
            return;
        }

        double level = Math.Clamp(Level, 0, 1);
        IBrush ink = level >= Hot ? this.Brush("Brush.Accent") : this.Brush("Brush.TextPrimary");
        // In grid units: scaled with the icon, about 1.5 px at the usual 24-28 px.
        var pen = new Pen(ink, 1.4, lineCap: PenLineCap.Square);
        double offsetX = (Bounds.Width - (24 * scale)) / 2, offsetY = (Bounds.Height - (24 * scale)) / 2;
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, offsetY)))
        {
            switch (Kind)
            {
                case "Cpu":
                    Cpu(context, pen);
                    break;
                case "Memory":
                    Memory(context, pen, ink);
                    break;
                case "Disk":
                    Disk(context, pen, ink);
                    break;
                case "Gpu":
                    Gpu(context, pen);
                    break;
                case "Temperature":
                    Thermometer(context, pen, ink, level);
                    break;
                case "Power":
                    context.DrawGeometry(ink, null, Path("M13.5,2 L5,13.5 L11,13.5 L10,22 L19,10 L13,10 Z"));
                    break;
                default:
                    Flame(context, pen, ink, level);
                    break;
            }
        }
    }

    private static void Cpu(DrawingContext context, Pen pen)
    {
        context.DrawRectangle(null, pen, new Rect(6, 6, 12, 12));
        context.DrawRectangle(null, pen, new Rect(9.5, 9.5, 5, 5));
        foreach (double at in new[] { 9.0, 12, 15 })
        {
            context.DrawLine(pen, new Point(at, 2.5), new Point(at, 6));
            context.DrawLine(pen, new Point(at, 18), new Point(at, 21.5));
            context.DrawLine(pen, new Point(2.5, at), new Point(6, at));
            context.DrawLine(pen, new Point(18, at), new Point(21.5, at));
        }
    }

    private static void Memory(DrawingContext context, Pen pen, IBrush ink)
    {
        context.DrawRectangle(null, pen, new Rect(2.5, 7, 19, 8));
        foreach (double x in new[] { 5.0, 10.5, 16 })
        {
            context.DrawRectangle(ink, null, new Rect(x, 9, 3, 4));
        }

        for (double x = 4.5; x <= 20; x += 2.5)
        {
            context.DrawLine(pen, new Point(x, 15), new Point(x, 18));
        }
    }

    private static void Disk(DrawingContext context, Pen pen, IBrush ink)
    {
        context.DrawEllipse(null, pen, new Point(12, 6.5), 8, 3);
        context.DrawGeometry(null, pen, Path("M4,6.5 L4,17.5 A8,3 0 0 0 20,17.5 L20,6.5"));
        context.DrawGeometry(null, pen, Path("M4,12 A8,3 0 0 0 20,12"));
        context.DrawEllipse(ink, null, new Point(16.5, 16.5), 1, 1);
    }

    private static void Gpu(DrawingContext context, Pen pen)
    {
        context.DrawRectangle(null, pen, new Rect(2.5, 6, 19, 11));
        context.DrawEllipse(null, pen, new Point(8.5, 11.5), 3, 3);
        context.DrawEllipse(null, pen, new Point(15.5, 11.5), 3, 3);
        context.DrawLine(pen, new Point(5, 17), new Point(5, 20));
        context.DrawLine(pen, new Point(5, 20), new Point(13, 20));
    }

    private void Thermometer(DrawingContext context, Pen pen, IBrush ink, double level)
    {
        context.DrawGeometry(null, pen, Path("M10,14.5 L10,4.5 A2,2 0 0 1 14,4.5 L14,14.5 A4,4 0 1 1 10,14.5 Z"));
        context.DrawEllipse(ink, null, new Point(12, 17.8), 2.2, 2.2);
        // The column rises with the reading.
        double top = 15.5 - (10 * level);
        context.DrawRectangle(ink, null, new Rect(11.2, top, 1.6, 15.8 - top));
        var scale = new Pen(this.Brush("Brush.TextTertiary"), 1);
        for (double y = 6; y <= 13; y += 3.5)
        {
            context.DrawLine(scale, new Point(16, y), new Point(18, y));
        }
    }

    private void Flame(DrawingContext context, Pen pen, IBrush ink, double level)
    {
        StreamGeometry flame = Path("M12,2.5 C13,6 17.5,8.5 17.5,14 A5.5,5.5 0 0 1 6.5,14 C6.5,11 8,9.5 9,8 C9.5,10 10.5,11 11.5,11.5 C11,8 11,5 12,2.5 Z");
        if (level >= Hot)
        {
            context.DrawGeometry(ink, null, flame);
        }
        else
        {
            context.DrawGeometry(this.Brush("Brush.TextPrimary", Math.Max(level, 0.08)), pen, flame);
        }
    }

    private static StreamGeometry Path(string data) => StreamGeometry.Parse(data);
}
