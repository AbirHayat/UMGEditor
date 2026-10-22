using UmgEditor.Core.Layout;

namespace UmgEditor.Core.Textures;

/// <summary>
/// A before/after drawing of a texture replacement on a screen (step 3, umgmod tex-preview): the screen laid out twice
/// (stock; the image in every box that shows the texture from a re-pointed package, auto-sized images at its size,
/// margins 0 when asked), drawn by <see cref="PreviewRaster"/>, cropped around those boxes and stacked stock above new.
/// Nothing is written to the project.
/// </summary>
public static class TexturePreview
{
    public sealed record Result(DecodedImage Image, int Boxes, bool ShowsHidden);

    public static Result Render(GameSource source, Func<string, WidgetPackageTree> loadTree, WidgetPackageTree screen, string texture,
        IReadOnlyCollection<string> repointed, DecodedImage replacement, bool marginsZero, int width, int height)
    {
        texture = GamePaths.PackageOf(texture);
        var cache = new TextureCache(source, new AssetRipperTextureDecoder());
        var embedded = new Dictionary<string, WidgetPackageTree?>(StringComparer.OrdinalIgnoreCase);
        WidgetPackageTree? Embedded(string p)
        {
            if (embedded.TryGetValue(p, out var t)) return t;
            try { t = loadTree(p); } catch (Exception) { t = null; }
            return embedded[p] = t;
        }
        bool Replaced(LayoutBox b) => b.TexturePath != null && GamePaths.PackageOf(b.TexturePath).Equals(texture, StringComparison.OrdinalIgnoreCase)
            && repointed.Contains(b.Node.PackagePath, StringComparer.OrdinalIgnoreCase);
        (int, int)? StockSize(string t) => cache.Get(t) is { IsPlaceholder: false } i ? (i.Width, i.Height) : null;

        var settings = UiScaleSettings.Load(source);
        var stock = new LayoutEngine(settings, Embedded, StockSize).Arrange(screen, width, height);
        // auto-sized images take the new size (by texture path: an approximation when other packages show it unreplaced)
        var edited = new LayoutEngine(settings, Embedded, t => GamePaths.PackageOf(t).Equals(texture, StringComparison.OrdinalIgnoreCase)
            ? (replacement.Width, replacement.Height) : StockSize(t)).Arrange(screen, width, height);

        var targets = stock.All().Where(Replaced).Concat(edited.All().Where(Replaced)).ToList();
        var showHidden = targets.Count > 0 && targets.All(b => !b.Visible);
        var before = PreviewRaster.Render(stock, b => b.TexturePath is { } p ? cache.Get(p) : null, showHidden: showHidden);
        var after = PreviewRaster.Render(edited, b => Replaced(b) ? replacement : b.TexturePath is { } p ? cache.Get(p) : null,
            b => marginsZero && Replaced(b) ? default : PreviewRaster.StoredMargins(b.Node), showHidden);

        var area = new RectangleF(0, 0, width, height);
        if (targets.Count > 0)
        {
            float x0 = targets.Min(b => b.Bounds.X), y0 = targets.Min(b => b.Bounds.Y), x1 = targets.Max(b => b.Bounds.Right), y1 = targets.Max(b => b.Bounds.Bottom);
            area = new RectangleF(x0 - 60, y0 - 40, x1 - x0 + 120, y1 - y0 + 80);
        }
        return new Result(PreviewRaster.Stack(PreviewRaster.Crop(before, area), PreviewRaster.Crop(after, area)), targets.Count / 2, showHidden);
    }
}
