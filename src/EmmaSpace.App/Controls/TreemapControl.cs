using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EmmaSpace.Core.Formatting;
using EmmaSpace.Core.Model;

namespace EmmaSpace.App.Controls;

/// <summary>
/// Interactive cushion treemap. Layout and shading run on a background thread into a bitmap at
/// device resolution; the UI thread only blits it and draws selection, hover and labels.
/// </summary>
public sealed class TreemapControl : Control
{
    public static readonly StyledProperty<FileTree?> TreeProperty =
        AvaloniaProperty.Register<TreemapControl, FileTree?>(nameof(Tree));

    public static readonly StyledProperty<int> FocusNodeProperty =
        AvaloniaProperty.Register<TreemapControl, int>(nameof(FocusNode), FileTree.Root);

    public static readonly StyledProperty<int> SelectedNodeProperty =
        AvaloniaProperty.Register<TreemapControl, int>(nameof(SelectedNode), FileTree.None, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> HoveredNodeProperty =
        AvaloniaProperty.Register<TreemapControl, int>(nameof(HoveredNode), FileTree.None, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> RevisionProperty =
        AvaloniaProperty.Register<TreemapControl, int>(nameof(Revision));

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<TreemapControl, IBrush?>(nameof(Background));

    private static readonly IPen s_selectionOuter = new ImmutablePen(new ImmutableSolidColorBrush(Colors.Black, 0.55), 3);
    private static readonly IPen s_selectionInner = new ImmutablePen(Brushes.White, 1.5);
    private static readonly IPen s_hoverPen = new ImmutablePen(new ImmutableSolidColorBrush(Colors.White, 0.7), 1);
    private static readonly IBrush s_labelBand = new ImmutableSolidColorBrush(Colors.Black, 0.42);
    private static readonly Typeface s_labelFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);

    private WriteableBitmap? _bitmap;
    private TreemapFrame? _frame;
    private CancellationTokenSource? _renderCts;
    private DispatcherTimer? _debounce;
    private double _scale = 1;

    static TreemapControl()
    {
        AffectsRender<TreemapControl>(SelectedNodeProperty, HoveredNodeProperty);
        FocusableProperty.OverrideDefaultValue<TreemapControl>(true);
    }

    public FileTree? Tree { get => GetValue(TreeProperty); set => SetValue(TreeProperty, value); }
    public int FocusNode { get => GetValue(FocusNodeProperty); set => SetValue(FocusNodeProperty, value); }
    public int SelectedNode { get => GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }
    public int HoveredNode { get => GetValue(HoveredNodeProperty); set => SetValue(HoveredNodeProperty, value); }
    public int Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    /// <summary>Raised on double-click with the node under the pointer.</summary>
    public event EventHandler<int>? NodeActivated;

    /// <summary>Raised on right-button press with the node under the pointer, or -1 over empty space.</summary>
    public event EventHandler<int>? NodeContextRequested;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TreeProperty || change.Property == FocusNodeProperty || change.Property == RevisionProperty ||
            change.Property == BackgroundProperty)
        {
            if (change.Property == TreeProperty) _frame = null;
            ScheduleRender(immediate: true);
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // First real size (e.g. the tab just became visible): draw now; while resizing: debounce.
        ScheduleRender(immediate: _frame is null);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _renderCts?.Cancel();
        _debounce?.Stop();
    }

    private void ScheduleRender(bool immediate)
    {
        if (immediate)
        {
            _debounce?.Stop();
            _ = RenderFrameAsync();
            return;
        }
        _debounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) =>
        {
            _debounce!.Stop();
            _ = RenderFrameAsync();
        });
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task RenderFrameAsync()
    {
        _renderCts?.Cancel();
        var tree = Tree;
        var focus = FocusNode;
        _scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var width = (int)(Bounds.Width * _scale);
        var height = (int)(Bounds.Height * _scale);
        if (tree is null || width < 2 || height < 2 || focus < 0 || focus >= tree.Count)
        {
            _frame = null;
            InvalidateVisual();
            return;
        }

        var cts = _renderCts = new CancellationTokenSource();
        var background = (Background as ISolidColorBrush)?.Color ?? Color.Parse("#16151D");
        try
        {
            var frame = await Task.Run(() =>
            {
                using var lease = tree.Lease();
                return TreemapLayout.Render(tree, focus, width, height, background, cts.Token);
            }, cts.Token);
            if (cts.IsCancellationRequested || !ReferenceEquals(tree, Tree)) return;
            _frame = frame;
            UploadBitmap(frame);
            InvalidateVisual();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            // Fire-and-forget task: make failures visible instead of silently losing them.
            System.Diagnostics.Trace.TraceError("Treemap render failed: {0}", ex);
        }
    }

    private unsafe void UploadBitmap(TreemapFrame frame)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96 * _scale, 96 * _scale),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        using var locked = _bitmap.Lock();
        var rowBytes = frame.Width * 4;
        var bytes = MemoryMarshal.AsBytes(frame.Pixels.AsSpan());
        for (var y = 0; y < frame.Height; y++)
            bytes.Slice(y * rowBytes, rowBytes).CopyTo(new Span<byte>((byte*)locked.Address + y * locked.RowBytes, rowBytes));
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Background ?? Brushes.Transparent, bounds);
        var frame = _frame;
        if (frame is null || _bitmap is null) return;

        context.DrawImage(_bitmap, new Rect(0, 0, frame.Width, frame.Height), new Rect(0, 0, frame.Width / _scale, frame.Height / _scale));
        DrawLabels(context, frame);

        if (HoveredNode >= 0 && frame.Rects.TryGetValue(HoveredNode, out var hover))
            context.DrawRectangle(null, s_hoverPen, ToDip(hover).Deflate(0.5));
        if (SelectedNode >= 0 && frame.Rects.TryGetValue(SelectedNode, out var sel))
        {
            var r = ToDip(sel).Deflate(1.5);
            context.DrawRectangle(null, s_selectionOuter, r);
            context.DrawRectangle(null, s_selectionInner, r);
        }
    }

    private void DrawLabels(DrawingContext context, TreemapFrame frame)
    {
        var tree = Tree;
        if (tree is null) return;
        foreach (var (node, rect) in frame.TopLevel)
        {
            var r = ToDip(rect);
            if (r.Width < 72 || r.Height < 34 || node >= tree.Count || tree.Nodes[node].IsDeleted) continue;
            var text = new FormattedText($"{tree.GetName(node)}  {ByteSize.Format(tree.Nodes[node].Size)}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, s_labelFace, 11.5, Brushes.White)
            {
                MaxTextWidth = Math.Max(1, r.Width - 12),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            var band = new Rect(r.X, r.Y, r.Width, text.Height + 6);
            using (context.PushClip(r))
            {
                context.FillRectangle(s_labelBand, band);
                context.DrawText(text, new Point(r.X + 6, r.Y + 3));
            }
        }
    }

    private Rect ToDip(TreemapRect r) => new(r.X / _scale, r.Y / _scale, r.Width / _scale, r.Height / _scale);

    private int NodeAt(Point p) => _frame?.NodeAt((int)(p.X * _scale), (int)(p.Y * _scale)) ?? FileTree.None;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var node = NodeAt(e.GetPosition(this));
        if (node != HoveredNode) HoveredNode = node;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoveredNode = FileTree.None;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var node = NodeAt(e.GetPosition(this));
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed) NodeContextRequested?.Invoke(this, node);
        if (node < 0) return;
        SelectedNode = node;
        if (e.ClickCount == 2 && !point.Properties.IsRightButtonPressed)
            NodeActivated?.Invoke(this, node);
        e.Handled = true;
    }
}
