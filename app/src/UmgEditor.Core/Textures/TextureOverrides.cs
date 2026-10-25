using UmgEditor.Core.Layout;

namespace UmgEditor.Core.Textures;

/// <summary>
/// A replacement image shown in a preview (step 3): boxes that draw <see cref="Texture"/> and belong to one of
/// <see cref="Packages"/> (the re-pointed ones) show <see cref="Image"/>; with <see cref="Widget"/> set (an edit of only
/// that widget) only that widget's box does. A trial is an import that isn't a project edit yet.
/// </summary>
public sealed record TextureOverride(string Texture, IReadOnlySet<string> Packages, DecodedImage Image, bool MarginsZero, bool IsTrial = false, string? Widget = null);

/// <summary>The replacement images a preview draws instead of the stock textures (the last one for a box wins).</summary>
public sealed class TextureOverrides(IReadOnlyList<TextureOverride> list)
{
    public static readonly TextureOverrides None = new([]);
    public IReadOnlyList<TextureOverride> All => list;
    public int Count => list.Count;

    public TextureOverride? For(LayoutBox box) => box.TexturePath is { } path
        ? list.LastOrDefault(o => GamePaths.PackageOf(path).Equals(o.Texture, StringComparison.OrdinalIgnoreCase) && o.Packages.Contains(box.Node.PackagePath)
                                  && (o.Widget == null || o.Widget == box.Node.Name))
        : null;

    /// <summary>The image size an auto-sized box takes (by texture path: approximate when an unreplaced package shows it too).</summary>
    public (int W, int H)? SizeOf(string texture) =>
        list.LastOrDefault(o => GamePaths.PackageOf(texture).Equals(o.Texture, StringComparison.OrdinalIgnoreCase)) is { } o ? (o.Image.Width, o.Image.Height) : null;

    /// <summary>The 9-slice margins a box is drawn with: 0 for a replacement with Margins 0, otherwise its stored Margin.</summary>
    public PreviewRaster.Margins MarginsFor(LayoutBox box) => For(box) is { MarginsZero: true } ? default : PreviewRaster.StoredMargins(box.Node);

    public TextureOverrides With(TextureOverride? extra) => extra == null ? this : new([.. list, extra]);
}
