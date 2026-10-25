using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Controls;

/// <summary>One frame of the 3D PC: four character layers plus part labels.</summary>
/// <param name="Layers">
/// Row-major characters per layer: 0 binary rain, 1 case and motherboard (dim),
/// 2 parts, 3 the selected or hovered part (accent). A space is empty.
/// </param>
public sealed record AsciiFrame(char[][] Layers, IReadOnlyList<AsciiLabel> Labels);

/// <param name="Column">Centre of the label, in character columns.</param>
/// <param name="Row">Bottom of the label, in character rows.</param>
public sealed record AsciiLabel(PcPart Part, double Column, double Row, bool IsHighlighted);

/// <summary>
/// The design's text-mode 3D PC: boxes projected with perspective into a character grid with a
/// z-buffer, edges drawn with - | / \ by angle, the selected part filled and swept by a scan plane,
/// and binary "rain" falling through the empty cells.
/// </summary>
public sealed class AsciiPcRenderer
{
    public const int Columns = 340;
    public const int Rows = 156;

    /// <summary>Glyph width over line height of the design's monospace grid (7 px font, 7.6 px lines).</summary>
    public const double CellAspect = 0.6 / 1.0857;

    private const double AutoRotateSpeed = 0.15; // radians per second
    private const double IdleBeforeRotate = 1.8; // seconds

    private readonly char[][] _layers = Enumerable.Range(0, 4).Select(_ => new char[Columns * Rows]).ToArray();
    private readonly byte[] _layerOf = new byte[Columns * Rows];
    private readonly float[] _depth = new float[Columns * Rows];
    private readonly List<(PcPart Part, double X0, double X1, double Y0, double Y1, double Area)> _hits = [];

    private double _angle = -0.9;
    private double _tilt = 0.32;
    private double _zoom = 1;
    private double _idle = IdleBeforeRotate;
    private (double Angle, double Tilt)? _dragStart;

    /// <summary>Parts as boxes [x0, x1, y0, y1, z0, z1]; case and motherboard are drawn dim and can't be picked.</summary>
    private static readonly (PcPart? Part, double[][] Boxes)[] Model =
    [
        (null, [[-2.2, 2.2, -4.2, 4.2, -4.6, 4.6]]),
        (null, [[1.75, 1.95, -2.4, 3.9, -4.2, 3.4]]),
        (PcPart.Power, [[-2.0, 2.0, -4.05, -2.9, 0.8, 4.4]]),
        (PcPart.Processor, [[0.4, 1.75, 1.3, 3.1, -1.9, -0.2], [1.45, 1.75, 1.7, 2.7, -1.5, -0.6]]),
        (PcPart.Memory, [[1.0, 1.75, 1.1, 3.5, 0.3, 0.5], [1.0, 1.75, 1.1, 3.5, 0.75, 0.95], [1.0, 1.75, 1.1, 3.5, 1.2, 1.4], [1.0, 1.75, 1.1, 3.5, 1.65, 1.85]]),
        (PcPart.Graphics, [[-0.9, 1.75, -1.3, -0.5, -4.2, 1.8]]),
        (PcPart.Drive, [[-1.9, -0.2, -2.7, -2.2, -4.4, -2.6]]),
        (PcPart.Trash, [[3.2, 4.6, -4.2, -2.4, -0.7, 0.7], [3.1, 4.7, -2.4, -2.2, -0.8, 0.8]]),
    ];

    /// <summary>The zoom the view eases toward (0.5 to 2.6).</summary>
    public double ZoomTarget { get; set; } = 1;

    public double Angle => _angle;

    public bool IsDragging => _dragStart is not null;

    /// <summary>Turns slowly on its own after a short pause, and eases the zoom.</summary>
    public void Advance(double seconds)
    {
        _idle += seconds;
        if (!IsDragging && _idle > IdleBeforeRotate)
        {
            _angle += seconds * AutoRotateSpeed;
        }

        _zoom += (ZoomTarget - _zoom) * 0.18;
    }

    public void BeginDrag() => _dragStart = (_angle, _tilt);

    /// <summary>Rotates by a pointer drag in pixels since <see cref="BeginDrag"/>.</summary>
    public void DragBy(double dx, double dy)
    {
        if (_dragStart is { } start)
        {
            _angle = start.Angle + (dx * 0.01);
            _tilt = Math.Clamp(start.Tilt + (dy * 0.006), -0.2, 0.9);
            _idle = 0;
        }
    }

    public void EndDrag()
    {
        _dragStart = null;
        _idle = 0;
    }

    /// <summary>The smallest part under a grid position from the last frame (a little slack around it).</summary>
    public PcPart? HitTest(double column, double row)
    {
        PcPart? best = null;
        double bestArea = double.MaxValue;
        foreach (var hit in _hits)
        {
            if (column >= hit.X0 - 1 && column <= hit.X1 + 1 && row >= hit.Y0 - 1 && row <= hit.Y1 + 1 && hit.Area < bestArea)
            {
                best = hit.Part;
                bestArea = hit.Area;
            }
        }

        return best;
    }

    /// <param name="seconds">Time, for the scan plane and the rain.</param>
    public AsciiFrame Render(double seconds, PcPart selected, PcPart? hovered, bool rain)
    {
        foreach (char[] layer in _layers)
        {
            Array.Fill(layer, ' ');
        }

        Array.Clear(_layerOf);
        Array.Fill(_depth, float.MaxValue);
        _hits.Clear();
        var labels = new List<AsciiLabel>();

        var view = new View(_angle, _tilt, _zoom);
        foreach (var (part, boxes) in Model)
        {
            byte layer = part is null ? (byte)1 : part == selected || part == hovered ? (byte)3 : (byte)2;
            if (part == selected)
            {
                foreach (double[] box in boxes)
                {
                    FillBox(view, box);
                }
            }

            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            foreach (double[] box in boxes)
            {
                foreach (var corner in DrawBox(view, box, layer))
                {
                    x0 = Math.Min(x0, corner.X);
                    x1 = Math.Max(x1, corner.X);
                    y0 = Math.Min(y0, corner.Y);
                    y1 = Math.Max(y1, corner.Y);
                }
            }

            if (part is not { } id)
            {
                continue;
            }

            double[] first = boxes[0];
            var top = view.Project((first[0] + first[1]) / 2, boxes.Max(b => b[3]), (first[4] + first[5]) / 2);
            double labelRow = Math.Round(y0) - 2;
            double labelColumn = Math.Round(top.X - 6);
            labels.Add(new AsciiLabel(id, top.X, labelRow, layer == 3));
            _hits.Add((id, Math.Min(x0, labelColumn), Math.Max(x1, labelColumn + 12), Math.Min(y0, labelRow), y1, (x1 - x0) * (y1 - y0)));

            if (id == selected)
            {
                DrawScanPlane(view, first, seconds);
            }
        }

        if (rain)
        {
            DrawRain(seconds);
        }

        return new AsciiFrame(_layers, labels);
    }

    private IEnumerable<(double X, double Y, double Z)> DrawBox(View view, double[] box, byte layer)
    {
        var corners = Corners(view, box);
        for (int i = 0; i < 8; i++)
        {
            foreach (int axis in (int[])[1, 2, 4])
            {
                if ((i & axis) == 0)
                {
                    Line(corners[i], corners[i | axis], layer);
                }
            }
        }

        return corners;
    }

    /// <summary>A plane moving along the part's longest side, like a scanner.</summary>
    private void DrawScanPlane(View view, double[] box, double seconds)
    {
        double[] extent = [box[1] - box[0], box[3] - box[2], box[5] - box[4]];
        int axis = Array.IndexOf(extent, extent.Max());
        double position = box[axis * 2] + (extent[axis] * (seconds / 1.4 % 1));
        double[] plane = (double[])box.Clone();
        plane[axis * 2] = position;
        plane[(axis * 2) + 1] = position;
        var corners = Corners(view, plane);
        for (int i = 0; i < 8; i++)
        {
            for (int a = 0; a < 3; a++)
            {
                int bit = 1 << a;
                if (a != axis && (i & bit) == 0)
                {
                    Line(corners[i], corners[i | bit], 3);
                }
            }
        }
    }

    /// <summary>A checkerboard of dots on every face of the selected part.</summary>
    private void FillBox(View view, double[] box)
    {
        var p = Corners(view, box);
        int[][] faces = [[0, 1, 3, 2], [4, 5, 7, 6], [0, 1, 5, 4], [2, 3, 7, 6], [0, 2, 6, 4], [1, 3, 7, 5]];
        foreach (int[] face in faces)
        {
            var q = face.Select(i => p[i]).ToArray();
            int cx0 = Math.Max(0, (int)Math.Floor(q.Min(v => v.X))), cx1 = Math.Min(Columns - 1, (int)Math.Ceiling(q.Max(v => v.X)));
            int ry0 = Math.Max(0, (int)Math.Floor(q.Min(v => v.Y))), ry1 = Math.Min(Rows - 1, (int)Math.Ceiling(q.Max(v => v.Y)));
            double z = (q.Sum(v => v.Z) / 4) + 0.03;
            for (int r = ry0; r <= ry1; r++)
            {
                for (int c = cx0; c <= cx1; c++)
                {
                    if ((r + c) % 2 == 0 && Inside(q, c, r))
                    {
                        Put(c, r, '.', z, 3);
                    }
                }
            }
        }
    }

    private static bool Inside((double X, double Y, double Z)[] quad, double c, double r)
    {
        int sign = 0;
        for (int k = 0; k < 4; k++)
        {
            var a = quad[k];
            var b = quad[(k + 1) % 4];
            int s = Math.Sign(((b.X - a.X) * (r - a.Y)) - ((b.Y - a.Y) * (c - a.X)));
            if (s != 0)
            {
                if (sign == 0)
                {
                    sign = s;
                }
                else if (s != sign)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void Line((double X, double Y, double Z) from, (double X, double Y, double Z) to, byte layer)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double angle = Math.Abs(Math.Atan2(dy * 1.85, dx));
        if (angle > Math.PI / 2)
        {
            angle = Math.PI - angle;
        }

        char glyph = angle < 0.39 ? '-' : angle > 1.18 ? '|' : dx * dy > 0 ? '\\' : '/';
        int steps = Math.Max((int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy))), 1);
        for (int k = 0; k <= steps; k++)
        {
            double u = (double)k / steps;
            double x = from.X + (dx * u), y = from.Y + (dy * u), z = from.Z + ((to.Z - from.Z) * u);
            Put(x, y, glyph, z, layer);
            if (layer == 1)
            {
                // The case is drawn thicker so it reads as the frame around everything.
                Put(x + 1, y, glyph, z + 0.01, layer);
                Put(x, y + 1, glyph, z + 0.01, layer);
                Put(x + 1, y + 1, glyph, z + 0.01, layer);
            }
        }
    }

    private void Put(double column, double row, char glyph, double z, byte layer)
    {
        int c = (int)Math.Round(column), r = (int)Math.Round(row);
        if (c < 0 || r < 0 || c >= Columns || r >= Rows)
        {
            return;
        }

        int i = (r * Columns) + c;
        if (z < _depth[i])
        {
            if (_layerOf[i] != 0)
            {
                _layers[_layerOf[i]][i] = ' ';
            }

            _depth[i] = (float)z;
            _layerOf[i] = layer;
            _layers[layer][i] = glyph;
        }
    }

    /// <summary>Columns of 0s and 1s falling at their own speeds through the empty cells.</summary>
    private void DrawRain(double seconds)
    {
        int flip = (int)Math.Floor(seconds * 1000 / 380);
        char[] rain = _layers[0];
        for (int c = 0; c < Columns; c++)
        {
            uint h = unchecked((uint)c * 2654435761u);
            if (h % 7 != 0)
            {
                continue;
            }

            double speed = 2 + ((h >> 4) % 5);
            double offset = (h >> 8) % 60;
            double head = ((seconds * speed) + offset) % (Rows + 14);
            for (int r = 0; r < Rows; r++)
            {
                double behind = head - r;
                int i = (r * Columns) + c;
                if (behind >= 0 && behind < 7 && _layerOf[i] == 0)
                {
                    rain[i] = ((((int)(h >> 3)) + r + flip) & 1) == 1 ? '1' : '0';
                }
            }
        }
    }

    private static (double X, double Y, double Z)[] Corners(View view, double[] box)
    {
        var corners = new (double, double, double)[8];
        for (int i = 0; i < 8; i++)
        {
            corners[i] = view.Project(box[(i & 1) != 0 ? 1 : 0], box[(i & 2) != 0 ? 3 : 2], box[(i & 4) != 0 ? 5 : 4]);
        }

        return corners;
    }

    /// <summary>Rotation around the vertical axis, then tilt, then perspective into the grid.</summary>
    private readonly struct View(double angle, double tilt, double zoom)
    {
        private const double Distance = 20, Focal = 20;
        private readonly double _ca = Math.Cos(angle), _sa = Math.Sin(angle), _cb = Math.Cos(tilt), _sb = Math.Sin(tilt);
        private readonly double _sy = 4.3 * Rows / 48 * zoom;

        public (double X, double Y, double Z) Project(double x, double y, double z)
        {
            double x1 = (x * _ca) + (z * _sa), z1 = (-x * _sa) + (z * _ca);
            double y2 = (y * _cb) - (z1 * _sb), z2 = (y * _sb) + (z1 * _cb);
            double q = Focal / (z2 + Distance);
            return ((Columns / 2.0) + (x1 * q * _sy * 1.85), (Rows / 2.0) + 1 - (y2 * q * _sy), z2);
        }
    }
}
