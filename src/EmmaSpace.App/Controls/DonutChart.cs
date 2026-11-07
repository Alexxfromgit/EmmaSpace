using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EmmaSpace.App.Controls;

public sealed record DonutSlice(double Value, Color Color, string Label);

/// <summary>Simple donut chart; slices are drawn clockwise from 12 o'clock with small gaps.</summary>
public sealed class DonutChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<DonutSlice>?> SlicesProperty =
        AvaloniaProperty.Register<DonutChart, IReadOnlyList<DonutSlice>?>(nameof(Slices));

    public static readonly StyledProperty<string?> CenterTextProperty =
        AvaloniaProperty.Register<DonutChart, string?>(nameof(CenterText));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<DonutChart, IBrush?>(nameof(Foreground), Brushes.Gray);

    static DonutChart() => AffectsRender<DonutChart>(SlicesProperty, CenterTextProperty, ForegroundProperty);

    public IReadOnlyList<DonutSlice>? Slices { get => GetValue(SlicesProperty); set => SetValue(SlicesProperty, value); }
    public string? CenterText { get => GetValue(CenterTextProperty); set => SetValue(CenterTextProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public override void Render(DrawingContext context)
    {
        var slices = Slices;
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (slices is null || slices.Count == 0 || size < 20) return;
        var total = slices.Sum(s => s.Value);
        if (total <= 0) return;

        var thickness = size * 0.16;
        var radius = (size - thickness) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var start = -Math.PI / 2;
        const double gap = 0.012;
        foreach (var slice in slices)
        {
            var sweep = slice.Value / total * Math.PI * 2;
            if (sweep > gap * 2)
            {
                var pen = new Pen(new SolidColorBrush(slice.Color), thickness, lineCap: PenLineCap.Flat);
                context.DrawGeometry(null, pen, Arc(center, radius, start + gap / 2, sweep - gap));
            }
            start += sweep;
        }

        if (!string.IsNullOrEmpty(CenterText))
        {
            var text = new FormattedText(CenterText, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), size * 0.09, Foreground);
            context.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
        }
    }

    private static StreamGeometry Arc(Point c, double r, double start, double sweep)
    {
        var geometry = new StreamGeometry();
        using var ctx = geometry.Open();
        var p0 = new Point(c.X + r * Math.Cos(start), c.Y + r * Math.Sin(start));
        var end = start + sweep;
        var p1 = new Point(c.X + r * Math.Cos(end), c.Y + r * Math.Sin(end));
        ctx.BeginFigure(p0, false);
        ctx.ArcTo(p1, new Size(r, r), 0, sweep > Math.PI, SweepDirection.Clockwise);
        ctx.EndFigure(false);
        return geometry;
    }
}
