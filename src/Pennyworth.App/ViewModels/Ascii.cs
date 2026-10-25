using System.Text;

namespace Pennyworth.App.ViewModels;

/// <summary>The design's text-mode drawings: sparklines, bars, the chip and the boot screen.</summary>
public static class Ascii
{
    private const string SparkLevels = "▁▂▃▄▅▆▇█";

    /// <summary>One block character per value, scaled to <paramref name="max"/>.</summary>
    public static string Spark(IEnumerable<double> values, double max)
    {
        var text = new StringBuilder();
        foreach (double value in values)
        {
            int level = (int)Math.Floor(value / max * SparkLevels.Length);
            text.Append(SparkLevels[Math.Clamp(level, 0, SparkLevels.Length - 1)]);
        }

        return text.ToString();
    }

    /// <summary>"####....": <paramref name="fraction"/> of <paramref name="width"/> filled.</summary>
    public static string Bar(double fraction, int width, char fill = '#', char empty = '.')
    {
        int filled = Math.Clamp((int)Math.Round(fraction * width), 0, width);
        return new string(fill, filled) + new string(empty, width - filled);
    }

    /// <summary>Several parts in one bar, each with its own character; the rest is empty.</summary>
    public static string StackedBar(IEnumerable<(char Fill, double Fraction)> parts, int width, char empty = '.')
    {
        var text = new StringBuilder(width);
        foreach (var (fill, fraction) in parts)
        {
            int count = Math.Clamp((int)Math.Round(fraction * width), 0, width - text.Length);
            text.Append(fill, count);
        }

        return text.Append(empty, width - text.Length).ToString();
    }

    public static string RandomBits(int count)
    {
        var text = new StringBuilder(count);
        for (int i = 0; i < count; i++)
        {
            text.Append(Random.Shared.Next(2) == 0 ? '0' : '1');
        }

        return text.ToString();
    }

    /// <summary>"01001101 11010010 ..." for the status bar.</summary>
    public static string BitsLine(string bits) => Grouped(bits[..Math.Min(32, bits.Length)], 8);

    /// <summary>Three rows of sixteen bits in groups of four, under the welcome screen's computer.</summary>
    public static string BitsBlock(string bits) => string.Join('\n',
        Enumerable.Range(0, 3).Select(row => Grouped(bits.Substring(Math.Min(row * 16, bits.Length), Math.Min(16, Math.Max(bits.Length - (row * 16), 0))), 4)));

    /// <summary>The little chip at the bottom of the sidebar, its bits flickering.</summary>
    public static string ChipArt(string bits) =>
        " +-+-+-+-+-+\n" +
        $"-| {bits[..4]} {bits[4..8]} |-\n" +
        $"-| {bits[8..12]} {bits[12..16]} |-\n" +
        " +-+-+-+-+-+";

    /// <summary>
    /// The welcome screen's computer: a 40-column screen showing the first <paramref name="shown"/>
    /// boot lines, a cursor, and a stand.
    /// </summary>
    public static string BootScreen(IReadOnlyList<string> lines, int shown, bool cursorOn)
    {
        const int width = 40;
        var body = lines.Take(Math.Min(shown, lines.Count)).ToList();
        body.Add(shown >= lines.Count ? "> _" : cursorOn ? "_" : " ");
        while (body.Count < 10)
        {
            body.Add("");
        }

        var text = new StringBuilder();
        text.Append('+').Append('-', width).Append("+\n");
        foreach (string line in body)
        {
            string content = (line + new string(' ', width))[..(width - 2)];
            text.Append("|  ").Append(content).Append("|\n");
        }

        text.Append('+').Append('-', 19).Append("++").Append('-', 19).Append("+\n");
        text.Append(' ', 15).Append("-----++-----");
        return text.ToString();
    }

    /// <summary>A boot line with dot leaders: "> drives ............... C:  D:".</summary>
    public static string Leader(string label, string value)
    {
        const int column = 24;
        string start = "> " + label + " ";
        return start + new string('.', Math.Max(column - start.Length, 3)) + " " + value;
    }

    /// <summary>
    /// The 3D view's drive: a 30×8 grid filled with one character per category, animated by
    /// <paramref name="progress"/> (0 to 1).
    /// </summary>
    public static string DriveArt(IEnumerable<(char Fill, double Fraction)> parts, double progress, string label)
    {
        const int width = 30, height = 8, cells = width * height;
        var sequence = new StringBuilder(cells);
        foreach (var (fill, fraction) in parts)
        {
            sequence.Append(fill, Math.Clamp((int)Math.Round(fraction * cells), 0, cells - sequence.Length));
        }

        int filled = (int)Math.Floor(sequence.Length * Math.Clamp(progress, 0, 1));
        var text = new StringBuilder();
        text.Append('+').Append('-', width + 2).Append("+\n");
        for (int row = 0; row < height; row++)
        {
            text.Append("| ");
            for (int column = 0; column < width; column++)
            {
                int i = (row * width) + column;
                text.Append(i < filled ? sequence[i] : '.');
            }

            text.Append(" |\n");
        }

        text.Append('+').Append('-', width + 2).Append("+\n");
        text.Append("  ").Append(label).Append("  ").Append((int)Math.Round(Math.Clamp(progress, 0, 1) * 100)).Append('%');
        return text.ToString();
    }

    /// <summary>Two letters for the app icon box: "Google Chrome" → "GC", "Steam" → "ST".</summary>
    public static string Initials(string name)
    {
        var words = name.Split([' ', '-', '_', '.', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => char.IsLetterOrDigit(word[0]))
            .ToArray();
        string initials = words.Length switch
        {
            0 => "?",
            1 => words[0].Length > 1 ? words[0][..2] : words[0],
            _ => $"{words[0][0]}{words[1][0]}",
        };
        return initials.ToUpperInvariant();
    }

    private static string Grouped(string bits, int size) =>
        string.Join(' ', Enumerable.Range(0, (bits.Length + size - 1) / size).Select(i => bits.Substring(i * size, Math.Min(size, bits.Length - (i * size)))));
}
