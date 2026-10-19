using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UmgEditor.Core.Layout;

namespace UmgEditor.App.Preview;

/// <summary>Shows a layout scaled to fit, with an optional screenshot underlay; clicking selects the widget under the cursor.</summary>
public sealed class PreviewCanvas : FrameworkElement
{
    public static readonly DependencyProperty LayoutProperty = Register(nameof(Layout), typeof(LayoutResult));
    public static readonly DependencyProperty RendererProperty = Register(nameof(Renderer), typeof(PreviewRenderer));
    public static readonly DependencyProperty OptionsProperty = Register(nameof(Options), typeof(PreviewOptions));
    public static readonly DependencyProperty UnderlayProperty = Register(nameof(Underlay), typeof(BitmapSource));
    public static readonly DependencyProperty UnderlayOpacityProperty = Register(nameof(UnderlayOpacity), typeof(double), 0.5);
    /// <summary>Underlay crop: the game-view rectangle inside the screenshot, in screenshot pixels.</summary>
    public static readonly DependencyProperty UnderlayCropProperty = Register(nameof(UnderlayCrop), typeof(Int32Rect), Int32Rect.Empty);

    public LayoutResult? Layout { get => (LayoutResult?) GetValue(LayoutProperty); set => SetValue(LayoutProperty, value); }
    public PreviewRenderer? Renderer { get => (PreviewRenderer?) GetValue(RendererProperty); set => SetValue(RendererProperty, value); }
    public PreviewOptions? Options { get => (PreviewOptions?) GetValue(OptionsProperty); set => SetValue(OptionsProperty, value); }
    public BitmapSource? Underlay { get => (BitmapSource?) GetValue(UnderlayProperty); set => SetValue(UnderlayProperty, value); }
    public double UnderlayOpacity { get => (double) GetValue(UnderlayOpacityProperty); set => SetValue(UnderlayOpacityProperty, value); }
    public Int32Rect UnderlayCrop { get => (Int32Rect) GetValue(UnderlayCropProperty); set => SetValue(UnderlayCropProperty, value); }

    /// <summary>Raised with the deepest visible widget under the click.</summary>
    public event Action<LayoutBox>? BoxClicked;

    private double _scale;
    private Vector _offset;

    private static DependencyProperty Register(string name, Type type, object? def = null) =>
        DependencyProperty.Register(name, type, typeof(PreviewCanvas), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(45, 45, 50)), null, new Rect(RenderSize));
        if (Layout is not { } layout || Renderer is not { } renderer) return;
        _scale = Math.Min(ActualWidth / layout.ViewportWidth, ActualHeight / layout.ViewportHeight);
        _offset = new Vector((ActualWidth - layout.ViewportWidth * _scale) / 2, (ActualHeight - layout.ViewportHeight * _scale) / 2);
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
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Layout is not { } layout || _scale <= 0) return;
        var v = (Vector) e.GetPosition(this) - _offset;
        var p = new Point(v.X / _scale, v.Y / _scale);
        var hit = layout.All()
            .Where(b => b.Visible && b.Bounds.Contains((float) p.X, (float) p.Y) && (b.Clip is not { } c || c.Contains((float) p.X, (float) p.Y)))
            .Where(b => b.Content != ContentKind.None || b.Children.Count == 0)
            .OrderBy(b => b.Bounds.Width * b.Bounds.Height)
            .FirstOrDefault();
        if (hit != null) BoxClicked?.Invoke(hit);
    }
}
