using System.Globalization;
using System.Numerics;
using UmgEditor.Core.Textures;

namespace UmgEditor.Core.Layout;

/// <summary>
/// A software drawing of a layout (step 3, texture previews without the app): every visible image box with its texture
/// (affine transform, clip, opacity, bilinear sampling, straight-alpha blending), stretched or 9-sliced by its stored
/// Margin (Box draw: margins are fractions of the texture, the border keeps the texture's own size in local units).
/// Text and other widgets are drawn as thin outlines. Not the game's renderer: tint, fonts and materials aren't drawn.
/// </summary>
public static class PreviewRaster
{
    public readonly record struct Margins(float Left, float Top, float Right, float Bottom)
    {
        public bool Any => Left > 0 || Top > 0 || Right > 0 || Bottom > 0;
    }

    /// <summary>The stored Margin of an image widget (0 when not stored).</summary>
    public static Margins StoredMargins(WidgetNode node)
    {
        float M(string side) => node.StoredProperties.FirstOrDefault(kv => kv.Key == "Margin." + side) is { Key: not null } kv
            && float.TryParse(kv.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return new Margins(M("Left"), M("Top"), M("Right"), M("Bottom"));
    }

    /// <param name="image">The texture to draw in a box (null: none / not decodable)</param>
    /// <param name="margins">The 9-slice margins of a box (default: its stored Margin)</param>
    public static DecodedImage Render(LayoutResult layout, Func<LayoutBox, DecodedImage?> image, Func<LayoutBox, Margins>? margins = null,
        bool showHidden = false, uint background = 0xFF202020)
    {
        int w = layout.ViewportWidth, h = layout.ViewportHeight;
        var canvas = new byte[w * h * 4];
        for (int i = 0; i < canvas.Length; i += 4) BitConverter.TryWriteBytes(canvas.AsSpan(i), background);
        margins ??= b => StoredMargins(b.Node);
        if (layout.Root != null) Draw(layout.Root, new RectangleF(0, 0, w, h), 1);
        return new DecodedImage(w, h, canvas, "preview");

        void Draw(LayoutBox box, RectangleF clip, double opacity)
        {
            if (!box.Visible && !showHidden) return;
            if (box.Clip is { } c) clip = clip.Intersect(c);
            opacity *= box.Opacity;
            if (clip.Width > 0 && clip.Height > 0)
            {
                if (box.Content == ContentKind.Texture && image(box) is { IsPlaceholder: false } img) Fill(box, img, margins(box), clip, opacity);
                else if (box.Content is ContentKind.Text or ContentKind.Unknown) Outline(box, clip);
            }
            foreach (var child in box.Children) Draw(child, clip, opacity);
        }

        void Fill(LayoutBox box, DecodedImage img, Margins m, RectangleF clip, double opacity)
        {
            if (!Matrix3x2.Invert(box.ToViewport, out var inverse)) return;
            var bounds = box.Bounds.Intersect(clip);
            float bw = box.LocalSize.X, bh = box.LocalSize.Y;
            if (bw <= 0 || bh <= 0) return;
            for (int y = (int) Math.Floor(bounds.Y); y < Math.Ceiling(bounds.Bottom); y++)
            {
                if (y < 0 || y >= h) continue;
                for (int x = (int) Math.Floor(bounds.X); x < Math.Ceiling(bounds.Right); x++)
                {
                    if (x < 0 || x >= w) continue;
                    var local = Vector2.Transform(new Vector2(x + 0.5f, y + 0.5f), inverse);
                    if (local.X < 0 || local.Y < 0 || local.X >= bw || local.Y >= bh) continue;
                    float u = Slice(local.X, bw, img.Width, m.Left, m.Right), v = Slice(local.Y, bh, img.Height, m.Top, m.Bottom);
                    var (b, g, r, a) = Sample(img, u, v);
                    Blend(canvas, (y * w + x) * 4, b, g, r, (float) (a / 255.0 * opacity));
                }
            }
        }

        void Outline(LayoutBox box, RectangleF clip)
        {
            var r = box.Bounds.Intersect(clip);
            if (r.Width <= 0 || r.Height <= 0) return;
            int x0 = (int) r.X, x1 = (int) Math.Ceiling(r.Right) - 1, y0 = (int) r.Y, y1 = (int) Math.Ceiling(r.Bottom) - 1;
            for (int x = x0; x <= x1; x++) { Dot(x, y0); Dot(x, y1); }
            for (int y = y0; y <= y1; y++) { Dot(x0, y); Dot(x1, y); }
            void Dot(int x, int y) { if (x >= 0 && y >= 0 && x < w && y < h) Blend(canvas, (y * w + x) * 4, 200, 200, 200, 0.35f); }
        }
    }

    /// <summary>Local coordinate → texture coordinate (pixels) along one axis: stretched, or Box-drawn with borders.</summary>
    private static float Slice(float p, float size, int texture, float near, float far)
    {
        if (near <= 0 && far <= 0) return p / size * texture;
        float n = near * texture, f = far * texture; // border sizes in local units = the texture's own pixels
        var scale = n + f > size ? size / (n + f) : 1;
        n *= scale; f *= scale;
        if (p < n) return p / n * near * texture;
        if (p > size - f) return texture - (size - p) / f * far * texture;
        var middle = size - n - f;
        return near * texture + (middle <= 0 ? 0 : (p - n) / middle) * (1 - near - far) * texture;
    }

    private static (byte B, byte G, byte R, byte A) Sample(DecodedImage img, float u, float v)
    {
        float fx = Math.Clamp(u - 0.5f, 0, img.Width - 1), fy = Math.Clamp(v - 0.5f, 0, img.Height - 1);
        int x0 = (int) fx, y0 = (int) fy, x1 = Math.Min(x0 + 1, img.Width - 1), y1 = Math.Min(y0 + 1, img.Height - 1);
        float tx = fx - x0, ty = fy - y0;
        Span<float> o = stackalloc float[4];
        for (int c = 0; c < 4; c++)
        {
            float P(int x, int y) => img.Bgra[(y * img.Width + x) * 4 + c];
            o[c] = (P(x0, y0) * (1 - tx) + P(x1, y0) * tx) * (1 - ty) + (P(x0, y1) * (1 - tx) + P(x1, y1) * tx) * ty;
        }
        return ((byte) (o[0] + 0.5f), (byte) (o[1] + 0.5f), (byte) (o[2] + 0.5f), (byte) (o[3] + 0.5f));
    }

    private static void Blend(byte[] canvas, int at, byte b, byte g, byte r, float alpha)
    {
        if (alpha <= 0) return;
        alpha = Math.Min(1, alpha);
        canvas[at] = (byte) (canvas[at] + (b - canvas[at]) * alpha + 0.5f);
        canvas[at + 1] = (byte) (canvas[at + 1] + (g - canvas[at + 1]) * alpha + 0.5f);
        canvas[at + 2] = (byte) (canvas[at + 2] + (r - canvas[at + 2]) * alpha + 0.5f);
        canvas[at + 3] = 255;
    }

    /// <summary>Crops a drawing to a rectangle (clamped to the image), e.g. around the boxes that matter.</summary>
    public static DecodedImage Crop(DecodedImage image, RectangleF area)
    {
        int x0 = Math.Clamp((int) Math.Floor(area.X), 0, image.Width - 1), y0 = Math.Clamp((int) Math.Floor(area.Y), 0, image.Height - 1);
        int x1 = Math.Clamp((int) Math.Ceiling(area.Right), x0 + 1, image.Width), y1 = Math.Clamp((int) Math.Ceiling(area.Bottom), y0 + 1, image.Height);
        var o = new byte[(x1 - x0) * (y1 - y0) * 4];
        for (int y = y0; y < y1; y++)
            image.Bgra.AsSpan((y * image.Width + x0) * 4, (x1 - x0) * 4).CopyTo(o.AsSpan((y - y0) * (x1 - x0) * 4));
        return new DecodedImage(x1 - x0, y1 - y0, o, image.Format);
    }

    /// <summary>Two drawings of the same width stacked (stock above, edited below), with a 4 px gap.</summary>
    public static DecodedImage Stack(DecodedImage top, DecodedImage bottom)
    {
        int w = Math.Max(top.Width, bottom.Width), h = top.Height + 4 + bottom.Height;
        var o = new byte[w * h * 4];
        void Put(DecodedImage img, int y0)
        {
            for (int y = 0; y < img.Height; y++) img.Bgra.AsSpan(y * img.Width * 4, img.Width * 4).CopyTo(o.AsSpan(((y0 + y) * w) * 4));
        }
        Put(top, 0);
        Put(bottom, top.Height + 4);
        return new DecodedImage(w, h, o, "preview");
    }
}
