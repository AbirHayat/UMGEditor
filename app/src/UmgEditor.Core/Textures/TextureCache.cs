using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace UmgEditor.Core.Textures;

/// <summary>
/// Loads a UI texture by object path, decodes its first mip, and caches the BGRA pixels on disk
/// (work/cache/tex) keyed by package path + container fingerprint. Placeholders are cached in memory only.
/// </summary>
public sealed class TextureCache(GameSource source, ITextureDecoder decoder)
{
    private const uint Magic = 0x41524742; // "BGRA"
    private readonly string _dir = Path.Combine(source.Config.CacheDir, "tex");
    private readonly ConcurrentDictionary<string, DecodedImage> _memory = new(StringComparer.OrdinalIgnoreCase);

    public ITextureDecoder Decoder => decoder;
    private int _diskHits, _decodes;
    public int DiskHits => _diskHits;
    public int Decodes => _decodes;

    /// <param name="objectPath">"/Game/.../Tex720/X.X" (a soft object path) or "/Game/.../Tex720/X"</param>
    public DecodedImage Get(string objectPath)
    {
        var package = GamePaths.PackageOf(objectPath);
        return _memory.GetOrAdd(package, Load);
    }

    private DecodedImage Load(string package)
    {
        var file = Path.Combine(_dir, $"{Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(package.ToLowerInvariant())))[..20]}_{source.Fingerprint}.bgra");
        if (TryReadCache(file) is { } cached) { Interlocked.Increment(ref _diskHits); return cached; }

        DecodedImage image;
        try
        {
            var pkg = source.LoadPackage(package);
            var tex = pkg.GetExports().OfType<UTexture2D>().FirstOrDefault();
            if (tex == null)
            {
                var type = pkg.GetExports().FirstOrDefault()?.ExportType ?? "nothing";
                return DecodedImage.Placeholder(type, $"not a Texture2D ({type})");
            }
            var mip = tex.GetFirstMip();
            if (mip?.BulkData?.Data is not { Length: > 0 } data)
                return DecodedImage.Placeholder(tex.Format.ToString(), "no mip data");
            image = decoder.CanDecode(tex.Format)
                ? decoder.Decode(tex.Format, data, mip.SizeX, mip.SizeY)
                : DecodedImage.Placeholder(tex.Format.ToString(), $"{tex.Format} not supported by {decoder.Name}");
            Interlocked.Increment(ref _decodes);
        }
        catch (Exception e)
        {
            return DecodedImage.Placeholder("?", e.GetType().Name + ": " + e.Message);
        }

        if (!image.IsPlaceholder) WriteCache(file, image);
        return image;
    }

    private static DecodedImage? TryReadCache(string file)
    {
        if (!File.Exists(file)) return null;
        using var r = new BinaryReader(File.OpenRead(file));
        if (r.ReadUInt32() != Magic) return null;
        int w = r.ReadInt32(), h = r.ReadInt32();
        var format = r.ReadString();
        var pixels = r.ReadBytes(w * h * 4);
        return pixels.Length == w * h * 4 ? new DecodedImage(w, h, pixels, format) : null;
    }

    private void WriteCache(string file, DecodedImage image)
    {
        Directory.CreateDirectory(_dir);
        var tmp = file + ".tmp";
        using (var w = new BinaryWriter(File.Create(tmp)))
        {
            w.Write(Magic); w.Write(image.Width); w.Write(image.Height); w.Write(image.Format); w.Write(image.Bgra);
        }
        File.Move(tmp, file, overwrite: true);
    }
}
