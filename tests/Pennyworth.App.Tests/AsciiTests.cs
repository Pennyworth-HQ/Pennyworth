using Pennyworth.App.Controls;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Tests;

public class AsciiTests
{
    [Fact]
    public void Draws_sparklines_and_bars()
    {
        Assert.Equal("▁▅█", Ascii.Spark([0, 12.5, 30], 25)); // same levels as the design: floor(v / max * 8)
        Assert.Equal("###.", Ascii.Bar(0.75, 4));
        Assert.Equal("##%:......", Ascii.StackedBar([('#', 0.2), ('%', 0.1), (':', 0.1)], 10));
    }

    [Fact]
    public void Draws_the_chip_and_bit_lines()
    {
        string bits = new string('1', 32) + new string('0', 32);

        Assert.Equal("11111111 11111111 11111111 11111111", Ascii.BitsLine(bits));
        Assert.Contains("-| 1111 1111 |-", Ascii.ChipArt(bits));
        Assert.Equal(3, Ascii.BitsBlock(bits).Split('\n').Length);
    }

    [Fact]
    public void Boot_screen_reveals_lines_one_by_one()
    {
        string[] lines = ["PENNYWORTH 0.1", "> ready."];

        string first = Ascii.BootScreen(lines, 1, cursorOn: true);
        string done = Ascii.BootScreen(lines, 2, cursorOn: false);

        Assert.Contains("PENNYWORTH 0.1", first);
        Assert.DoesNotContain("ready", first);
        Assert.Contains("> _", done);
        Assert.All(done.Split('\n').Take(11), line => Assert.Equal(42, line.Length));
    }

    [Fact]
    public void Drive_art_fills_with_category_characters()
    {
        string half = Ascii.DriveArt([('#', 0.5), ('%', 0.5)], 0.5, @"C:\");

        Assert.EndsWith(@"C:\  50%", half);
        string grid = half[..half.LastIndexOf('\n')];
        Assert.Equal(120, grid.Count(c => c == '#'));
        Assert.Equal(0, grid.Count(c => c == '%'));
    }

    [Theory]
    [InlineData("Google Chrome", "GC")]
    [InlineData("Steam", "ST")]
    [InlineData("WSL / Docker virtual machine", "WD")]
    [InlineData("", "?")]
    public void Initials_for_app_icons(string name, string expected)
    {
        Assert.Equal(expected, Ascii.Initials(name));
    }
}

public class AsciiPcRendererTests
{
    [Fact]
    public void Draws_every_part_with_a_label()
    {
        var renderer = new AsciiPcRenderer();

        var frame = renderer.Render(0, PcPart.Drive, hovered: null, rain: false);

        Assert.Equal(6, frame.Labels.Count);
        Assert.Single(frame.Labels, label => label.IsHighlighted);
        Assert.Contains(frame.Layers[1], c => c != ' '); // the case
        Assert.Contains(frame.Layers[2], c => c is '-' or '|' or '/' or '\\');
        Assert.Contains(frame.Layers[3], c => c == '.'); // the selected part is filled
        Assert.DoesNotContain(frame.Layers[0], c => c != ' ');
    }

    [Fact]
    public void Picks_parts_under_their_label()
    {
        var renderer = new AsciiPcRenderer();
        var frame = renderer.Render(0, PcPart.Drive, null, rain: false);

        // Labels can sit over a neighbouring part; like the design, the smaller part wins there.
        Assert.All(frame.Labels, label => Assert.NotNull(renderer.HitTest(label.Column, label.Row)));
        var trash = frame.Labels.Single(label => label.Part == PcPart.Trash);
        Assert.Equal(PcPart.Trash, renderer.HitTest(trash.Column, trash.Row));

        Assert.Null(renderer.HitTest(-50, -50));
    }

    [Fact]
    public void Rain_only_falls_through_empty_cells()
    {
        var frame = new AsciiPcRenderer().Render(3.7, PcPart.Memory, null, rain: true);

        Assert.Contains(frame.Layers[0], c => c is '0' or '1');
        for (int i = 0; i < frame.Layers[0].Length; i++)
        {
            if (frame.Layers[0][i] != ' ')
            {
                Assert.True(frame.Layers[1][i] == ' ' && frame.Layers[2][i] == ' ' && frame.Layers[3][i] == ' ');
            }
        }
    }

    [Fact]
    public void Turns_on_its_own_unless_dragged()
    {
        var renderer = new AsciiPcRenderer();
        double start = renderer.Angle;

        renderer.Advance(1);
        Assert.True(renderer.Angle > start);

        renderer.BeginDrag();
        renderer.DragBy(100, 0);
        double dragged = renderer.Angle;
        renderer.Advance(1);
        Assert.Equal(dragged, renderer.Angle);
        renderer.EndDrag();
        renderer.Advance(1); // still resting after the drag
        Assert.Equal(dragged, renderer.Angle);
    }
}
