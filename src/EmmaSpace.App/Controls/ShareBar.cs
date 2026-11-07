using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EmmaSpace.App.Controls;

/// <summary>A thin rounded bar showing a percentage; cheaper than a ProgressBar in long lists.</summary>
public sealed class ShareBar : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ShareBar, double>(nameof(Value));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ShareBar, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<ShareBar, IBrush?>(nameof(Track));

    static ShareBar() => AffectsRender<ShareBar>(ValueProperty, FillProperty, TrackProperty);

    /// <summary>0–100.</summary>
    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Track { get => GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 6);

    public override void Render(DrawingContext context)
    {
        var h = Math.Min(6, Bounds.Height);
        var y = (Bounds.Height - h) / 2;
        var radius = h / 2;
        context.DrawRectangle(Track, null, new RoundedRect(new Rect(0, y, Bounds.Width, h), radius));
        var w = Bounds.Width * Math.Clamp(Value, 0, 100) / 100;
        if (w >= 1) context.DrawRectangle(Fill, null, new RoundedRect(new Rect(0, y, Math.Max(w, h), h), radius));
    }
}
