using Avalonia.Media;
using EmmaSpace.App.Services;
using EmmaSpace.Core.Analysis;
using EmmaSpace.Core.Model;

namespace EmmaSpace.App.Controls;

public readonly record struct TreemapRect(int X, int Y, int Width, int Height)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>Result of laying out and shading a treemap: BGRA pixels plus lookup data for hit testing.</summary>
public sealed class TreemapFrame(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public uint[] Pixels { get; } = new uint[width * height];

    /// <summary>The leaf node drawn at each pixel, or -1.</summary>
    public int[] Owner { get; } = new int[width * height];

    public Dictionary<int, TreemapRect> Rects { get; } = [];

    /// <summary>Direct children of the focus folder, for labels.</summary>
    public List<(int Node, TreemapRect Rect)> TopLevel { get; } = [];

    public int NodeAt(int x, int y) => x < 0 || y < 0 || x >= Width || y >= Height ? -1 : Owner[y * Width + x];
}

/// <summary>
/// Squarified treemap (Bruls, Huizing, van Wijk) with cushion shading (van Wijk, van de Wetering):
/// every nesting level adds a parabolic ridge, so the light catches the structure of the tree
/// without needing borders.
/// </summary>
public static class TreemapLayout
{
    private const double InitialHeight = 0.5;
    private const double HeightFactor = 0.82;
    private const double Ambient = 0.32;
    private const double Diffuse = 0.68;
    private static readonly (double X, double Y, double Z) s_light = Normalize(-1, -1, 9);

    private struct Surface
    {
        public double X1, X2, Y1, Y2; // cushion coefficients
    }

    public static TreemapFrame Render(FileTree tree, int focus, int width, int height, Color background, CancellationToken ct)
    {
        var frame = new TreemapFrame(width, height);
        Array.Fill(frame.Owner, -1);
        Array.Fill(frame.Pixels, ToBgra(background, 1.0));
        if (width < 2 || height < 2 || tree.Nodes[focus].Size == 0) return frame;

        var rect = new Rect(0, 0, width, height);
        frame.Rects[focus] = Round(rect);
        LayoutChildren(tree, focus, rect, default, InitialHeight, depth: 0, frame, ct);
        return frame;
    }

    private readonly record struct Rect(double X, double Y, double W, double H);

    private static void LayoutChildren(FileTree tree, int node, Rect rect, Surface surface, double h, int depth,
        TreemapFrame frame, CancellationToken ct)
    {
        if (depth % 4 == 0) ct.ThrowIfCancellationRequested();

        var children = new List<int>();
        ulong total = 0;
        foreach (var c in tree.Children(node))
        {
            var size = tree.Nodes[c].Size;
            if (size == 0) break; // children are sorted largest first
            children.Add(c);
            total += size;
        }
        if (children.Count == 0)
        {
            PaintLeaf(tree, node, rect, surface, frame);
            return;
        }

        // A thin gutter between the top levels keeps big blocks readable.
        var gutter = depth < 2 && rect.W > 40 && rect.H > 40 ? 1.0 : 0.0;
        var scale = rect.W * rect.H / total;
        var start = 0;
        var area = rect;
        while (start < children.Count)
        {
            // Grow the row while the worst aspect ratio keeps improving.
            var shortSide = Math.Min(area.W, area.H);
            var end = start;
            double rowSum = 0, worst = double.MaxValue;
            while (end < children.Count)
            {
                var a = tree.Nodes[children[end]].Size * scale;
                var next = WorstRatio(children, start, end, rowSum + a, shortSide, tree, scale);
                if (next > worst) break;
                worst = next;
                rowSum += a;
                end++;
            }

            var horizontal = area.W >= area.H; // lay the row along the shorter side
            var thickness = rowSum / shortSide;
            var offset = 0.0;
            for (var i = start; i < end; i++)
            {
                var c = children[i];
                var length = tree.Nodes[c].Size * scale / thickness;
                var r = horizontal
                    ? new Rect(area.X, area.Y + offset, thickness, length)
                    : new Rect(area.X + offset, area.Y, length, thickness);
                offset += length;
                Place(tree, c, Shrink(r, gutter), surface, h, depth, frame, ct);
            }
            area = horizontal
                ? new Rect(area.X + thickness, area.Y, Math.Max(0, area.W - thickness), area.H)
                : new Rect(area.X, area.Y + thickness, area.W, Math.Max(0, area.H - thickness));
            start = end;
        }
    }

    private static void Place(FileTree tree, int node, Rect r, Surface surface, double h, int depth, TreemapFrame frame,
        CancellationToken ct)
    {
        if (r.W < 0.5 || r.H < 0.5) return;
        var s = surface;
        AddRidge(r.X, r.X + r.W, h, ref s.X1, ref s.X2);
        AddRidge(r.Y, r.Y + r.H, h, ref s.Y1, ref s.Y2);
        var rounded = Round(r);
        frame.Rects[node] = rounded;
        if (depth == 0) frame.TopLevel.Add((node, rounded));

        if (tree.Nodes[node].IsDirectory && r.W >= 3 && r.H >= 3)
            LayoutChildren(tree, node, r, s, h * HeightFactor, depth + 1, frame, ct);
        else
            PaintLeaf(tree, node, r, s, frame);
    }

    private static void PaintLeaf(FileTree tree, int node, Rect r, Surface s, TreemapFrame frame)
    {
        var color = tree.Nodes[node].IsDirectory
            ? CategoryPalette.Folder
            : CategoryPalette.ColorOf(FileCategories.Categorize(tree.GetExtension(node)));
        var (x0, y0, x1, y1) = Bounds(r, frame);
        for (var y = y0; y < y1; y++)
        {
            var ny = -(2 * s.Y2 * (y + 0.5) + s.Y1);
            var row = y * frame.Width;
            for (var x = x0; x < x1; x++)
            {
                var nx = -(2 * s.X2 * (x + 0.5) + s.X1);
                var cos = (nx * s_light.X + ny * s_light.Y + s_light.Z) / Math.Sqrt(nx * nx + ny * ny + 1);
                var intensity = Ambient + Math.Max(0, Diffuse * cos);
                frame.Pixels[row + x] = ToBgra(color, intensity);
                frame.Owner[row + x] = node;
            }
        }
    }

    private static double WorstRatio(List<int> children, int start, int end, double sum, double side, FileTree tree, double scale)
    {
        var maxA = tree.Nodes[children[start]].Size * scale; // largest first
        var minA = tree.Nodes[children[end]].Size * scale;
        var s2 = sum * sum;
        var w2 = side * side;
        return Math.Max(w2 * maxA / s2, s2 / (w2 * Math.Max(minA, 1e-9)));
    }

    private static void AddRidge(double x1, double x2, double h, ref double s1, ref double s2)
    {
        s1 += 4 * h * (x2 + x1) / (x2 - x1);
        s2 -= 4 * h / (x2 - x1);
    }

    private static Rect Shrink(Rect r, double by) =>
        by <= 0 || r.W <= 2 * by || r.H <= 2 * by ? r : new Rect(r.X + by, r.Y + by, r.W - 2 * by, r.H - 2 * by);

    private static (int X0, int Y0, int X1, int Y1) Bounds(Rect r, TreemapFrame f) => (
        Math.Clamp((int)Math.Round(r.X), 0, f.Width),
        Math.Clamp((int)Math.Round(r.Y), 0, f.Height),
        Math.Clamp((int)Math.Round(r.X + r.W), 0, f.Width),
        Math.Clamp((int)Math.Round(r.Y + r.H), 0, f.Height));

    private static TreemapRect Round(Rect r)
    {
        var x0 = (int)Math.Round(r.X);
        var y0 = (int)Math.Round(r.Y);
        return new TreemapRect(x0, y0, (int)Math.Round(r.X + r.W) - x0, (int)Math.Round(r.Y + r.H) - y0);
    }

    private static uint ToBgra(Color c, double intensity)
    {
        static uint Channel(byte v, double i) => (uint)Math.Clamp(v * i, 0, 255);
        return 0xFF000000u | (Channel(c.R, intensity) << 16) | (Channel(c.G, intensity) << 8) | Channel(c.B, intensity);
    }

    private static (double, double, double) Normalize(double x, double y, double z)
    {
        var len = Math.Sqrt(x * x + y * y + z * z);
        return (x / len, y / len, z / len);
    }
}
