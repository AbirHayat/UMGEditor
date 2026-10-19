using System.Text.Json;

namespace UmgEditor.Core.Layout;

/// <summary>Widget rects of a layout as JSON (the format work/scripts/validate_layout.py reads).</summary>
public static class LayoutExport
{
    /// <summary>"CanvasPanel_0/ScaleBox_0/.../Mask": unique even when names repeat across embedded blueprints.</summary>
    public static string PathOf(LayoutBox box)
    {
        var parts = new List<string>();
        for (var b = box; b != null; b = b.Parent) parts.Add(b.Node.Name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    public static string ToJson(LayoutResult layout, string packagePath, object? extra = null) =>
        JsonSerializer.Serialize(new
        {
            Package = packagePath,
            layout.ViewportWidth,
            layout.ViewportHeight,
            layout.DpiScale,
            layout.ScaleBoxes,
            Extra = extra,
            Widgets = layout.All().Select(b => new
            {
                b.Node.Name,
                Path = PathOf(b),
                Class = b.Node.ClassName,
                Package = GamePaths.ShortName(b.Node.PackagePath),
                b.Visible,
                Px = new { b.Bounds.X, b.Bounds.Y, W = b.Bounds.Width, H = b.Bounds.Height },
                b.PixelsPerUnit,
                b.Approximation,
                b.TexturePath,
            }),
        }, new JsonSerializerOptions { WriteIndented = true });
}
