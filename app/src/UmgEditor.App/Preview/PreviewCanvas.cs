using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Layout;

namespace UmgEditor.App.Preview;

/// <summary>
/// Shows a layout scaled to fit, with an optional screenshot underlay; clicking selects the widget under the cursor.
/// 6f: the selected widget gets handles (drag the box to move, an edge to resize; Ctrl = free, Esc cancels, arrow keys
/// nudge 1 unit), or the reason it can't be edited. The canvas only reports gestures; the edit logic is in SlotDrag.
/// </summary>
public sealed class PreviewCanvas : FrameworkElement
{
    // AffectsMeasure: when zoomed the canvas is the viewport's size × zoom
    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(nameof(Layout), typeof(LayoutResult), typeof(PreviewCanvas),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RendererProperty = Register(nameof(Renderer), typeof(PreviewRenderer));
    public static readonly DependencyProperty OptionsProperty = Register(nameof(Options), typeof(PreviewOptions));
    public static readonly DependencyProperty UnderlayProperty = Register(nameof(Underlay), typeof(BitmapSource));
    public static readonly DependencyProperty UnderlayOpacityProperty = Register(nameof(UnderlayOpacity), typeof(double), 0.5);
    /// <summary>Underlay crop: the game-view rectangle inside the screenshot, in screenshot pixels.</summary>
    public static readonly DependencyProperty UnderlayCropProperty = Register(nameof(UnderlayCrop), typeof(Int32Rect), Int32Rect.Empty);
    /// <summary>The selected widget's handles (null: nothing selected or nothing to drag).</summary>
    public static readonly DependencyProperty HandlesProperty = Register(nameof(Handles), typeof(DragAvailability));
    /// <summary>Shown next to the widget while dragging ("Offsets.Left 126 → 146 (+20 u)").</summary>
    public static readonly DependencyProperty DragLabelProperty = Register(nameof(DragLabel), typeof(string));
    /// <summary>Screen px per viewport px; 0 = fit the viewport into the available space. Zoomed, the canvas takes the
    /// viewport's size × zoom (inside a ScrollViewer) and drags get finer by the same factor.</summary>
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(nameof(Zoom), typeof(double), typeof(PreviewCanvas),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((PreviewCanvas) d).ScrollSelectionIntoView()));

    public LayoutResult? Layout { get => (LayoutResult?) GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public PreviewRenderer? Renderer { get => (PreviewRenderer?) GetValue(RendererProperty); set => SetValue(RendererProperty, value); }
    public PreviewOptions? Options { get => (PreviewOptions?) GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }
    public BitmapSource? Underlay { get => (BitmapSource?) GetValue(UnderlayProperty); set => SetValue(UnderlayProperty, value); }
    public double UnderlayOpacity { get => (double) GetValue(UnderlayOpacityProperty); set => SetValue(UnderlayOpacityProperty, value); }
    public Int32Rect UnderlayCrop { get => (Int32Rect) GetValue(UnderlayCropProperty); set => SetValue(UnderlayCropProperty, value); }
    public DragAvailability? Handles { get => (DragAvailability?) GetValue(HandlesProperty); set => SetValue(HandlesProperty, value); }
    public string? DragLabel { get => (string?) GetValue(DragLabelProperty); set => SetValue(DragLabelProperty, value); }
    public double Zoom { get => (double) GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    /// <summary>Raised with the deepest visible widget under the click.</summary>
    public event Action<LayoutBox>? BoxClicked;

    // gestures, wired by the window to the view model
    /// <summary>A drag on this handle starts; false when it can't (the canvas then does nothing).</summary>
    public Func<DragHandle, bool>? DragStart { get; set; }
    /// <summary>The drag moved (dx, dy) viewport px from where it started; free = Ctrl held (no snapping).</summary>
    public Action<float, float, bool>? DragMove { get; set; }
    /// <summary>Mouse-up (true: commit) or Esc / lost capture (false: cancel).</summary>
    public Action<bool>? DragEnd { get; set; }
    /// <summary>Arrow key: move the selected widget by (dx, dy) units.</summary>
    public Action<int, int>? Nudge { get; set; }
    /// <summary>Ctrl + mouse wheel: one zoom step in (+1) or out (-1).</summary>
    public Action<int>? ZoomStep { get; set; }

    private const double Grab = 6, HandleSize = 8, DragThreshold = 3;
    private double _scale;
    private Vector _offset;
    private Point _down;
    private DragHandle? _pending;
    private bool _dragging;

    public PreviewCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
    }

    private static DependencyProperty Register(string name, Type type, object? def = null) =>
        DependencyProperty.Register(name, type, typeof(PreviewCanvas), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    protected override Size MeasureOverride(Size available)
    {
        if (Zoom > 0 && Layout is { } l) return new Size(l.ViewportWidth * Zoom, l.ViewportHeight * Zoom);
        // fit: take what the parent offers (a ScrollViewer with scrolling disabled offers its viewport)
        return new Size(double.IsInfinity(available.Width) ? 400 : available.Width, double.IsInfinity(available.Height) ? 300 : available.Height);
    }

    /// <summary>After a zoom change, scroll the selected widget into view (once the new size is laid out).</summary>
    private void ScrollSelectionIntoView() => Dispatcher.BeginInvoke(() =>
    {
        if (SelectedBox is { } box && _scale > 0) BringIntoView(Rect.Inflate(ScreenRect(box), 60, 60));
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        ZoomStep?.Invoke(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private LayoutBox? SelectedBox => Options?.Selected is { } sel ? Layout?.All().FirstOrDefault(b => ReferenceEquals(b.Node, sel)) : null;

    private Rect ScreenRect(LayoutBox box) =>
        new(_offset.X + box.Bounds.X * _scale, _offset.Y + box.Bounds.Y * _scale, box.Bounds.Width * _scale, box.Bounds.Height * _scale);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(45, 45, 50)), null, new Rect(RenderSize));
        if (Layout is not { } layout || Renderer is not { } renderer) return;
        _scale = Zoom > 0 ? Zoom : Math.Min(ActualWidth / layout.ViewportWidth, ActualHeight / layout.ViewportHeight);
        _offset = Zoom > 0 ? new Vector(0, 0) : new Vector((ActualWidth - layout.ViewportWidth * _scale) / 2, (ActualHeight - layout.ViewportHeight * _scale) / 2);
        renderer.Render(dc, layout, Options ?? new PreviewOptions(), _scale, _offset);

        if (Underlay is { } shot)
        {
            BitmapSource source = shot;
            if (!UnderlayCrop.IsEmpty && UnderlayCrop.X + UnderlayCrop.Width <= shot.PixelWidth && UnderlayCrop.Y + UnderlayCrop.Height <= shot.PixelHeight)
                source = new CroppedBitmap(shot, UnderlayCrop);
            dc.PushOpacity(UnderlayOpacity);
            dc.DrawImage(source, new Rect(_offset.X, _offset.Y, layout.ViewportWidth * _scale, layout.ViewportHeight * _scale));
            dc.Pop();
        }
        DrawHandles(dc);
    }

    private static readonly Pen HandlePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(37, 99, 235)), 1.5));
    private static readonly Brush ReasonBack = Frozen(new SolidColorBrush(Color.FromArgb(235, 254, 242, 242)));
    private static readonly Pen ReasonPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(252, 165, 165)), 1));
    private static readonly Brush LabelBack = Frozen(new SolidColorBrush(Color.FromArgb(230, 17, 24, 39)));
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private void DrawHandles(DrawingContext dc)
    {
        var box = SelectedBox;
        var status = "none";
        if (box != null && Handles is { } h)
        {
            var r = ScreenRect(box);
            status = string.Create(CultureInfo.InvariantCulture, $"box {r.Left:0.#},{r.Top:0.#},{r.Right:0.#},{r.Bottom:0.#}");
            if (h.Any)
            {
                var allowed = Enum.GetValues<DragHandle>().Where(h.Allows).ToList();
                status += " handles " + string.Join(",", allowed);
                foreach (var edge in DragAvailability.Edges.Where(h.Allows))
                {
                    var c = edge switch
                    {
                        DragHandle.Left => new Point(r.Left, r.Top + r.Height / 2), DragHandle.Right => new Point(r.Right, r.Top + r.Height / 2),
                        DragHandle.Top => new Point(r.Left + r.Width / 2, r.Top), _ => new Point(r.Left + r.Width / 2, r.Bottom),
                    };
                    dc.DrawRectangle(Brushes.White, HandlePen, new Rect(c.X - HandleSize / 2, c.Y - HandleSize / 2, HandleSize, HandleSize));
                }
            }
            else if (h.Reason is { } reason && !_dragging)
            {
                status += " reason";
                Label(dc, reason, r, ReasonBack, ReasonPen, Brushes.DarkRed);
            }
            if (DragLabel is { } label && _dragging) Label(dc, label, r, LabelBack, null, Brushes.White);
        }
        AutomationProperties.SetItemStatus(this, status + (_dragging ? " dragging" : ""));
    }

    /// <summary>A wrapped label under the box (above it when there's no room below).</summary>
    private void Label(DrawingContext dc, string text, Rect box, Brush back, Pen? border, Brush fore)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 11.5, fore, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(120, Math.Min(380, ActualWidth - 16)),
        };
        var w = ft.WidthIncludingTrailingWhitespace + 12;
        var hgt = ft.Height + 8;
        var x = Math.Clamp(box.Left, 4, Math.Max(4, ActualWidth - w - 4));
        var y = box.Bottom + 6 + hgt <= ActualHeight ? box.Bottom + 6 : Math.Max(4, box.Top - hgt - 6);
        dc.DrawRoundedRectangle(back, border, new Rect(x, y, w, hgt), 3, 3);
        dc.DrawText(ft, new Point(x + 6, y + 4));
    }

    /// <summary>The handle under a canvas point: an edge within Grab px of it, else Move inside the box.</summary>
    private DragHandle? HandleAt(Point p)
    {
        if (SelectedBox is not { } box || Handles is not { Any: true } h) return null;
        var r = ScreenRect(box);
        bool inX = p.X >= r.Left - Grab && p.X <= r.Right + Grab, inY = p.Y >= r.Top - Grab && p.Y <= r.Bottom + Grab;
        var candidates = new (DragHandle Handle, bool Hit, double Distance)[]
        {
            (DragHandle.Left, inY && Math.Abs(p.X - r.Left) <= Grab, Math.Abs(p.X - r.Left)),
            (DragHandle.Right, inY && Math.Abs(p.X - r.Right) <= Grab, Math.Abs(p.X - r.Right)),
            (DragHandle.Top, inX && Math.Abs(p.Y - r.Top) <= Grab, Math.Abs(p.Y - r.Top)),
            (DragHandle.Bottom, inX && Math.Abs(p.Y - r.Bottom) <= Grab, Math.Abs(p.Y - r.Bottom)),
        };
        var edge = candidates.Where(c => c.Hit && h.Allows(c.Handle)).OrderBy(c => c.Distance).Select(c => (DragHandle?) c.Handle).FirstOrDefault();
        if (edge != null) return edge;
        return r.Contains(p) && h.Allows(DragHandle.Move) ? DragHandle.Move : null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (Layout == null || _scale <= 0) return;
        _down = e.GetPosition(this);
        _pending = HandleAt(_down);
        if (_pending != null)
        {
            CaptureMouse();
            e.Handled = true;
            return;
        }
        SelectAt(_down);
    }

    private void SelectAt(Point point)
    {
        if (Layout is not { } layout) return;
        var v = (Vector) point - _offset;
        var p = new Point(v.X / _scale, v.Y / _scale);
        var hit = layout.All()
            .Where(b => b.Visible && b.Bounds.Contains((float) p.X, (float) p.Y) && (b.Clip is not { } c || c.Contains((float) p.X, (float) p.Y)))
            .Where(b => b.Content != ContentKind.None || b.Children.Count == 0)
            .OrderBy(b => b.Bounds.Width * b.Bounds.Height)
            .FirstOrDefault();
        if (hit != null) BoxClicked?.Invoke(hit);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_pending is { } handle && !_dragging && (p - _down).Length >= DragThreshold)
        {
            _dragging = DragStart?.Invoke(handle) == true;
            if (!_dragging) { _pending = null; ReleaseMouseCapture(); }
        }
        if (_dragging)
        {
            var d = (p - _down) / _scale;
            DragMove?.Invoke((float) d.X, (float) d.Y, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
            return;
        }
        Cursor = HandleAt(p) switch
        {
            DragHandle.Move => Cursors.SizeAll,
            DragHandle.Left or DragHandle.Right => Cursors.SizeWE,
            DragHandle.Top or DragHandle.Bottom => Cursors.SizeNS,
            _ => null,
        };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_pending == null) return;
        var wasDragging = _dragging;
        Finish(commit: true);
        // a press on the box without moving is a click: select what's under it (maybe a child of the selected widget)
        if (!wasDragging) SelectAt(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging) Finish(commit: false);
    }

    private void Finish(bool commit)
    {
        var wasDragging = _dragging;
        _dragging = false;
        _pending = null;
        if (IsMouseCaptured) ReleaseMouseCapture(); // may re-enter OnLostMouseCapture, now with _dragging false
        if (wasDragging) DragEnd?.Invoke(commit);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && (_dragging || _pending != null))
        {
            Finish(commit: false);
            e.Handled = true;
            return;
        }
        if (_dragging) return;
        (int dx, int dy)? nudge = e.Key switch { Key.Left => (-1, 0), Key.Right => (1, 0), Key.Up => (0, -1), Key.Down => (0, 1), _ => null };
        if (nudge is { } n && SelectedBox != null && Handles != null)
        {
            Nudge?.Invoke(n.dx, n.dy);
            e.Handled = true;
        }
    }
}
