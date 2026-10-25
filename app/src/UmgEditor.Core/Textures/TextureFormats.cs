using System.Collections.Concurrent;
using System.Text.Json;

namespace UmgEditor.Core.Textures;

/// <summary>One texture package's pixel format, size and mip count (inline B8G8R8A8 = can be a clone source).</summary>
public sealed record TextureFormat(string Texture, string PixelFormat, int Width, int Height, int Mips, bool InlineMips)
{
    public bool Cloneable => PixelFormat == "PF_B8G8R8A8" && InlineMips;
}

/// <summary>
/// The format of every UI texture package (step 3: clone sources for textures that can't be cloned themselves, e.g.
/// ETC2). Built once per game fingerprint by loading each texture of the UI index; cached in work/cache.
/// </summary>
public sealed class TextureFormats
{
    private const int FormatVersion = 1;

    public IReadOnlyList<TextureFormat> All { get; }
    private readonly Dictionary<string, TextureFormat> _byTexture;

    public TextureFormats(IReadOnlyList<TextureFormat> all)
    {
        All = all;
        _byTexture = all.ToDictionary(f => f.Texture, StringComparer.OrdinalIgnoreCase);
    }

    public TextureFormat? Of(string texture) => _byTexture.GetValueOrDefault(GamePaths.PackageOf(texture));

    public static TextureFormats LoadOrBuild(GameSource source, IReadOnlyList<UiAssetEntry> index)
    {
        Directory.CreateDirectory(source.Config.CacheDir);
        var cachePath = Path.Combine(source.Config.CacheDir, $"texture_formats_v{FormatVersion}_{source.Fingerprint}.json");
        if (File.Exists(cachePath) && JsonSerializer.Deserialize<List<TextureFormat>>(File.ReadAllText(cachePath)) is { } cached)
            return new TextureFormats(cached);
        var all = Build(source, index);
        File.WriteAllText(cachePath, JsonSerializer.Serialize(all));
        return new TextureFormats(all);
    }

    public static IReadOnlyList<TextureFormat> Build(GameSource source, IReadOnlyList<UiAssetEntry> index)
    {
        var found = new ConcurrentBag<TextureFormat>();
        var textures = index.Where(e => e.Kind == UiAssetKind.Texture && !e.IsBulkImageSet && !e.IsPlatformOverride).Select(e => GamePaths.ToPackagePath(e.Path)).ToList();
        Parallel.ForEach(textures, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, texture =>
        {
            if (Modding.TextureEdits.LoadTexture(source, texture) is not { } t) return;
            var p = t.PlatformData;
            var inline = p.Mips.Length > 0 && p.Mips.All(m => m.BulkData is { } b && b.BulkDataFlags.HasFlag(CUE4Parse.UE4.Assets.Objects.EBulkDataFlags.BULKDATA_ForceInlinePayload) && b.Data != null);
            found.Add(new TextureFormat(texture, p.PixelFormat, p.SizeX, p.SizeY, p.Mips.Length, inline));
        });
        return found.OrderBy(f => f.Texture, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
