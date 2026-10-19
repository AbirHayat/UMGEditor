using AssetRipper.TextureDecoder.Astc;
using AssetRipper.TextureDecoder.Etc;
using AssetRipper.TextureDecoder.Rgb.Formats;
using CUE4Parse.UE4.Assets.Exports.Texture;

namespace UmgEditor.Core.Textures;

/// <summary>
/// A decoded texture in BGRA32 (B,G,R,A byte order, straight alpha, top row first), which is what WPF's Bgra32 expects.
/// A placeholder carries no pixels, only the reason it couldn't be decoded.
/// </summary>
public sealed record DecodedImage(int Width, int Height, byte[] Bgra, string Format, string? PlaceholderReason = null)
{
    public bool IsPlaceholder => PlaceholderReason != null;

    public static DecodedImage Placeholder(string format, string reason) => new(0, 0, [], format, reason);
}

/// <summary>Turns one mip of block-compressed or raw pixel data into BGRA32. Swappable for testing or another library.</summary>
public interface ITextureDecoder
{
    string Name { get; }
    bool CanDecode(EPixelFormat format);
    DecodedImage Decode(EPixelFormat format, byte[] data, int width, int height);
}

/// <summary>Managed decoders from AssetRipper.TextureDecoder (MIT). Output of AssetRipper is RGBA and is swapped to BGRA here.</summary>
public sealed class AssetRipperTextureDecoder : ITextureDecoder
{
    public string Name => "AssetRipper.TextureDecoder 2.6.2";

    private static readonly Dictionary<EPixelFormat, (int X, int Y)> AstcBlocks = new()
    {
        [EPixelFormat.PF_ASTC_4x4] = (4, 4), [EPixelFormat.PF_ASTC_6x6] = (6, 6), [EPixelFormat.PF_ASTC_8x8] = (8, 8),
        [EPixelFormat.PF_ASTC_10x10] = (10, 10), [EPixelFormat.PF_ASTC_12x12] = (12, 12),
    };

    public bool CanDecode(EPixelFormat format) => format is EPixelFormat.PF_ETC2_RGB or EPixelFormat.PF_ETC2_RGBA or EPixelFormat.PF_ETC1
        or EPixelFormat.PF_B8G8R8A8 or EPixelFormat.PF_R8G8B8A8 or EPixelFormat.PF_G8 || AstcBlocks.ContainsKey(format);

    public DecodedImage Decode(EPixelFormat format, byte[] data, int width, int height)
    {
        var name = format.ToString();
        byte[] rgba;
        switch (format)
        {
            case EPixelFormat.PF_ETC2_RGB:
                EtcDecoder.DecompressETC2<ColorRGBA<byte>, byte>(data, width, height, out rgba);
                break;
            case EPixelFormat.PF_ETC2_RGBA:
                EtcDecoder.DecompressETC2A8<ColorRGBA<byte>, byte>(data, width, height, out rgba);
                break;
            case EPixelFormat.PF_ETC1:
                EtcDecoder.DecompressETC<ColorRGBA<byte>, byte>(data, width, height, out rgba);
                break;
            case EPixelFormat.PF_B8G8R8A8:
                return CheckSize(new DecodedImage(width, height, data[..(width * height * 4)], name));
            case EPixelFormat.PF_R8G8B8A8:
                rgba = data[..(width * height * 4)];
                break;
            case EPixelFormat.PF_G8:
                var bgra = new byte[width * height * 4];
                for (int i = 0; i < width * height; i++) { bgra[i * 4] = bgra[i * 4 + 1] = bgra[i * 4 + 2] = data[i]; bgra[i * 4 + 3] = 255; }
                return new DecodedImage(width, height, bgra, name);
            default:
                if (AstcBlocks.TryGetValue(format, out var block))
                {
                    AstcDecoder.DecodeASTC<ColorRGBA<byte>, byte>(data, width, height, block.X, block.Y, out rgba);
                    break;
                }
                return DecodedImage.Placeholder(name, $"{name} is not supported by {Name}");
        }
        return CheckSize(new DecodedImage(width, height, SwapRedBlue(rgba), name));
    }

    /// <summary>RGBA → BGRA (in place on a copy).</summary>
    public static byte[] SwapRedBlue(byte[] rgba)
    {
        var bgra = (byte[]) rgba.Clone();
        for (int i = 0; i + 3 < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
        return bgra;
    }

    private static DecodedImage CheckSize(DecodedImage img) =>
        img.Bgra.Length == img.Width * img.Height * 4 ? img
            : DecodedImage.Placeholder(img.Format, $"decoder returned {img.Bgra.Length} bytes for {img.Width}x{img.Height}");
}
