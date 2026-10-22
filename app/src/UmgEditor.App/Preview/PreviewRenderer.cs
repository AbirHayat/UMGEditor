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
    bool HighlightEdited = true,
    WidgetNode? Selected = null,
    IReadOnlySet<string>? Changed = null);

/// <summary>Draws a LayoutResult. Shared by the on-screen preview and PNG export so both show the same thing.</summary>
public sealed class PreviewRenderer(Func<string, DecodedImage> textures)
{
    private readonly Dictionary<string, BitmapSource?> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DecodedImage, BitmapSource> _replacements = new();
    private static readonly Pen TrialPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(217, 70, 239)), 2) { DashStyle = DashStyles.Dash });

    /// <summary>Step 3: replacement images (project texture edits and a trial import) drawn instead of the stock textures.</summary>
    public TextureOverrides Overrides { get; set; } = TextureOverrides.None;
    private static readonly Typeface Label = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface PlaceholderFace = new(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush Hatch = MakeHatch();
    private static readonly Pen TextPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(200, 120, 120, 120)), 1) { DashStyle = DashStyles.Dash });
    private static readonly Pen OutlinePen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 160, 255)), 1));
    private static readonly Pen AssumedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(245, 130, 0)), 1.5) { DashStyle = DashStyles.Dash });
    private static readonly Pen AnimatedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(220, 38, 38)), 1.5) { DashStyle = DashStyles.Dot });
    private static readonly Pen ClipPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(180, 120, 120, 120)), 1) { DashStyle = DashStyles.DashDot });
    private static readonly Pen ChangedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(16, 185, 129)), 2) { DashStyle = DashStyles.DashDot });
    private static readonly Pen EditedPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(59, 130, 246)), 2) { DashStyle = DashStyles.Dash });
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
            if (options.Changed != null && options.Changed.Contains(LayoutExport.PathOf(box))) dc.DrawRectangle(null, ChangedPen, r);
            if (options.HighlightEdited && box.Inputs.Any(i => i.Source == Core.Layout.ValueSource.Edited)) dc.DrawRectangle(null, EditedPen, r);
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
        var replaced = Overrides.For(box);
        var img = replaced?.Image ?? textures(box.TexturePath);
        if (img.IsPlaceholder)
        {
            // materials and anything else that isn't a decodable Texture2D
            dc.DrawRectangle(Hatch, TextPen, local);
            var what = img.PlaceholderReason!.Contains("Material") ? "material" : "not rendered";
            DrawLabel(dc, $"{what}: {GamePaths.ShortName(box.TexturePath)}", local, Brushes.White, 11);
            return;
        }
        var bmp = replaced != null ? _replacements.GetValue(replaced.Image, i => ToBitmap(i)) : Bitmap(box.TexturePath, img);
        if (bmp != null)
        {
            var margins = Overrides.MarginsFor(box);
            if (margins.Any) DrawBox(dc, bmp, img.Width, img.Height, margins, local);
            else dc.DrawImage(bmp, local);
        }
        if (replaced is { IsTrial: true }) dc.DrawRectangle(null, TrialPen, local);
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
            return _bitmaps[path] = ToBitmap(img);
        }
    }

    public static BitmapSource ToBitmap(DecodedImage img)
    {
        var bmp = BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Bgra, img.Width * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// UMG's Box draw (9-slice): the margins are fractions of the texture; the borders keep the texture's own size in
    /// local units (scaled down together when the box is smaller), the middle stretches. Same rule as PreviewRaster.
    /// </summary>
    private static void DrawBox(DrawingContext dc, BitmapSource bmp, int tw, int th, PreviewRaster.Margins m, Rect local)
    {
        static double[] Cuts(double size, int texture, double near, double far, out double[] uv)
        {
            double n = near * texture, f = far * texture;
            var scale = n + f > size ? size / (n + f) : 1;
            n *= scale; f *= scale;
            uv = [0, near, 1 - far, 1];
            return [0, n, size - f, size];
        }
        var xs = Cuts(local.Width, tw, m.Left, m.Right, out var us);
        var ys = Cuts(local.Height, th, m.Top, m.Bottom, out var vs);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
            {
                double w = xs[c + 1] - xs[c], h = ys[r + 1] - ys[r];
                if (w <= 0 || h <= 0 || us[c + 1] <= us[c] || vs[r + 1] <= vs[r]) continue;
                var brush = new ImageBrush(bmp)
                {
                    Viewbox = new Rect(us[c], vs[r], us[c + 1] - us[c], vs[r + 1] - vs[r]),
                    ViewboxUnits = BrushMappingMode.RelativeToBoundingBox, Stretch = Stretch.Fill,
                };
                brush.Freeze();
                dc.DrawRectangle(brush, null, new Rect(local.X + xs[c], local.Y + ys[r], w, h));
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
