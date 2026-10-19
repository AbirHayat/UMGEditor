using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UmgEditor.Core;
using UmgEditor.Core.Layout;
using UmgEditor.Core.Textures;

namespace UmgEditor.App.Preview;

public sealed record PreviewOptions(
    bool Outlines = false,
    bool ClipRects = false,
    bool HighlightAssumed = true,
    bool ShowHidden = false,
    bool HighlightAnimated = false,
    WidgetNode? Selected = null);

/// <summary>Draws a LayoutResult. Shared by the on-screen preview and PNG export so both show the same thing.</summary>
public sealed class PreviewRenderer(Func<string, DecodedImage> textures)
{
    private readonly Dictionary<string, BitmapSource?> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Typeface Label = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface PlaceholderFace = new(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush Hatch = MakeHatch();
    private static readonly Pen TextPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(200, 120, 120, 120)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen OutlinePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 160, 255)), 1));
    private static readonly Pen AssumedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(245, 130, 0)), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen AnimatedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(220, 38, 38)), 1.5) { DashStyle = DashStyles.Dot });
    private static readonly Pen ClipPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(180, 120, 120, 120)), 1) { DashStyle = DashStyles.DashDot });
    private static readonly Pen SelectedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0, 220, 255)), 2.5));

    /// <param name="scale">screen px per viewport px</param>
    public void Render(DrawingContext dc, LayoutResult layout, PreviewOptions options, double scale, Vector offset)
    {
        var viewport = new Rect(offset.X, offset.Y, layout.ViewportWidth * scale, layout.ViewportHeight * scale);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(24, 24, 28)), null, viewport);
        if (layout.Root == null) return;

        var toScreen = new Matrix(scale, 0, 0, scale, offset.X, offset.Y);
        dc.PushClip(new RectangleGeometry(viewport));
        Draw(dc, layout.Root, options, toScreen);
        // overlays on top, unclipped by widget clips so they stay visible
        foreach (var box in layout.All())
        {
            if (!box.Visible && !options.ShowHidden) continue;
            var r = ScreenRect(box.Bounds, toScreen);
            if (options.Outlines) dc.DrawRectangle(null, OutlinePen, r);
            if (options.HighlightAssumed && box.Approximation != null) dc.DrawRectangle(null, AssumedPen, r);
            if (options.HighlightAnimated && box.Node.Animations.Any(a => a.MovesWidget)) dc.DrawRectangle(null, AnimatedPen, r);
            if (options.ClipRects && box.Node.ClipsToBounds) dc.DrawRectangle(null, ClipPen, r);
        }
        if (options.Selected is { } sel && layout.All().FirstOrDefault(b => ReferenceEquals(b.Node, sel)) is { } selBox)
            dc.DrawRectangle(null, SelectedPen, ScreenRect(selBox.Bounds, toScreen));
        dc.Pop();
    }

    private void Draw(DrawingContext dc, LayoutBox box, PreviewOptions options, Matrix toScreen)
    {
        var ghost = !box.Visible;
        if (ghost && !options.ShowHidden) return;

        int pushes = 0;
        if (box.Clip is { } clip) { dc.PushClip(new RectangleGeometry(ScreenRect(clip, toScreen))); pushes++; }
        // stored opacity 0 (faded in by an animation) is shown as a ghost when hidden widgets are requested
        var opacity = box.Opacity * (ghost ? 0.3 : 1);
        if (options.ShowHidden && opacity < 0.3) opacity = 0.3;
        if (opacity < 1) { dc.PushOpacity(opacity); pushes++; }

        var m = box.ToViewport;
        dc.PushTransform(new MatrixTransform(new Matrix(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32) * toScreen));
        var local = new Rect(0, 0, box.LocalSize.X, box.LocalSize.Y);
        switch (box.Content)
        {
            case ContentKind.Texture: DrawTexture(dc, box, local); break;
            case ContentKind.Text: DrawTextPlaceholder(dc, box, local); break;
            case ContentKind.Unknown:
                dc.DrawRectangle(null, TextPen, local);
                DrawLabel(dc, box.Node.ClassName, local, Brushes.Gray, 10);
                break;
        }
        dc.Pop();

        foreach (var child in box.Children) Draw(dc, child, options, toScreen);
        for (int i = 0; i < pushes; i++) dc.Pop();
    }

    private void DrawTexture(DrawingContext dc, LayoutBox box, Rect local)
    {
        if (box.TexturePath == null) { DrawLabel(dc, "no texture", local, Brushes.Gray, 10); return; }
        var img = textures(box.TexturePath);
        if (img.IsPlaceholder)
        {
            // materials and anything else that isn't a decodable Texture2D
            dc.DrawRectangle(Hatch, TextPen, local);
            var what = img.PlaceholderReason!.Contains("Material") ? "material" : "not rendered";
            DrawLabel(dc, $"{what}: {GamePaths.ShortName(box.TexturePath)}", local, Brushes.White, 11);
            return;
        }
        if (Bitmap(box.TexturePath, img) is { } bmp) dc.DrawImage(bmp, local);
        if (box.TintNotApplied && local.Width >= 12 && local.Height >= 12)
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(200, 245, 130, 0)), null, new Point(local.Right - 4, local.Top + 4), 3, 3);
    }

    private static void DrawTextPlaceholder(DrawingContext dc, LayoutBox box, Rect local)
    {
        dc.DrawRectangle(null, TextPen, local);
        var size = box.FontSize > 0 ? box.FontSize : 24;
        var sample = new FormattedText(box.Node.Name, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, PlaceholderFace, size * 0.7,
            new SolidColorBrush(Color.FromArgb(170, 200, 200, 200)), 1.0)
        {
            MaxTextWidth = Math.Max(1, local.Width), MaxTextHeight = Math.Max(1, local.Height), Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = box.TextJustification?.EndsWith("Center") == true ? TextAlignment.Center : box.TextJustification?.EndsWith("Right") == true ? TextAlignment.Right : TextAlignment.Left,
        };
        var y = box.TextVerticalAlignment?.EndsWith("Center") == true ? (local.Height - sample.Height) / 2
              : box.TextVerticalAlignment?.EndsWith("Bottom") == true ? local.Height - sample.Height : 0;
        dc.DrawText(sample, new Point(0, Math.Max(0, y)));
        var tag = new FormattedText($"font not rendered · size {size:0}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Label,
            Math.Max(4, Math.Min(10, local.Height / 4)), new SolidColorBrush(Color.FromArgb(200, 245, 130, 0)), 1.0) { MaxTextWidth = Math.Max(1, local.Width) };
        dc.DrawText(tag, new Point(1, local.Height - tag.Height));
    }

    private static void DrawLabel(DrawingContext dc, string text, Rect local, Brush brush, double size)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Label, size, brush, 1.0)
        { MaxTextWidth = Math.Max(1, local.Width - 2), MaxTextHeight = Math.Max(1, local.Height), Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(ft, new Point(local.X + 1, local.Y + 1));
    }

    private BitmapSource? Bitmap(string path, DecodedImage img)
    {
        lock (_bitmaps)
        {
            if (_bitmaps.TryGetValue(path, out var cached)) return cached;
            var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Bgra, img.Width * 4);
            bmp.Freeze();
            return _bitmaps[path] = bmp;
        }
    }

    private static Rect ScreenRect(Core.Layout.RectangleF r, Matrix toScreen)
    {
        var p = toScreen.Transform(new Point(r.X, r.Y));
        return new Rect(p.X, p.Y, r.Width * toScreen.M11, r.Height * toScreen.M22);
    }

    private static Brush MakeHatch()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromArgb(90, 140, 60, 200)), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(null, new Pen(new SolidColorBrush(Color.FromArgb(200, 200, 140, 255)), 1.5),
            new LineGeometry(new Point(0, 8), new Point(8, 0))));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 8, 8), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    private static Pen Frozen(Pen p) { p.Freeze(); return p; }
}
