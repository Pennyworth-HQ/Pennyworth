using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Pennyworth.App.Controls;

/// <summary>
/// Five rising bars, like a signal meter, for a stress level from 0 to 1. Lit bars are ink;
/// the top two light in the accent colour, so "high" and "extreme" stand out.
/// </summary>
public sealed class LevelBars : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelBars, double>(nameof(Level));

    public const int Count = 5;

    static LevelBars()
    {
        AffectsRender<LevelBars>(LevelProperty);
    }

    public LevelBars() => ActualThemeVariantChanged += (_, _) => InvalidateVisual();

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>Bars lit for a level: none at 0, one as soon as anything runs, all five at the top.</summary>
    public static int Lit(double level) => level <= 0.02 ? 0 : Math.Clamp((int)Math.Ceiling(Math.Clamp(level, 0, 1) * Count), 1, Count);

    public override void Render(DrawingContext context)
    {
        const double gap = 2;
        double width = (Bounds.Width - (gap * (Count - 1))) / Count;
        if (width <= 0)
        {
            return;
        }

        int lit = Lit(Level);
        for (int i = 0; i < Count; i++)
        {
            double height = Bounds.Height * (i + 1) / Count;
            IBrush brush = i >= lit ? this.Brush("Brush.Track") : i >= Count - 2 ? this.Brush("Brush.Accent") : this.Brush("Brush.TextPrimary");
            context.DrawRectangle(brush, null, new Rect(i * (width + gap), Bounds.Height - height, width, height));
        }
    }
}
