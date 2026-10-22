using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace UmgEditor.Core.Textures;

/// <summary>
/// Minimal PNG decoder for importing art without an imaging library: grey, grey+alpha, RGB, RGBA (8-bit) and palette
/// (1/2/4/8-bit, tRNS alpha), grey also at 1/2/4 bits; non-interlaced; every chunk CRC and all five row filters checked.
/// Output is <see cref="DecodedImage"/> BGRA (straight alpha, top row first), the layout texture mips use. 16-bit and
/// interlaced images are refused with a reason (re-save as 8-bit, non-interlaced).
/// </summary>
public static class PngReader
{
    private static readonly byte[] Signature = [0x89, (byte) 'P', (byte) 'N', (byte) 'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private const int MaxSide = 16384;

    public static (DecodedImage? Image, string? Why) Read(string path) =>
        File.Exists(path) ? Read(File.ReadAllBytes(path)) : (null, $"{path} doesn't exist");

    public static (DecodedImage? Image, string? Why) Read(byte[] png)
    {
        if (png.Length < 8 || !png.AsSpan(0, 8).SequenceEqual(Signature)) return (null, "not a PNG file (signature)");
        int width = 0, height = 0, depth = 0, colour = -1;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();
        bool sawEnd = false;
        for (int at = 8; at < png.Length;)
        {
            if (at + 12 > png.Length) return (null, $"truncated chunk at {at}");
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at));
            if (length < 0 || at + 12L + length > png.Length) return (null, $"chunk at {at} runs past the end");
            var type = System.Text.Encoding.ASCII.GetString(png, at + 4, 4);
            var data = png.AsSpan(at + 8, length);
            if (Crc32.HashToUInt32(png.AsSpan(at + 4, 4 + length)) != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(at + 8 + length)))
                return (null, $"{type} chunk at {at}: CRC mismatch");
            switch (type)
            {
                case "IHDR":
                    if (length != 13) return (null, "IHDR isn't 13 bytes");
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    depth = data[8]; colour = data[9];
                    if (data[10] != 0 || data[11] != 0) return (null, "unknown compression or filter method");
                    if (data[12] != 0) return (null, "interlaced PNG: re-save it without interlacing");
                    if (width <= 0 || height <= 0 || width > MaxSide || height > MaxSide) return (null, $"size {width}x{height} out of range");
                    if (depth == 16) return (null, "16-bit PNG: re-save it as 8-bit");
                    var ok = colour switch { 0 or 3 => depth is 1 or 2 or 4 or 8, 2 or 4 or 6 => depth == 8, _ => false };
                    if (!ok) return (null, $"colour type {colour} at {depth} bits isn't supported");
                    break;
                case "PLTE":
                    if (length % 3 != 0 || length == 0 || length > 768) return (null, "bad PLTE length");
                    palette = data.ToArray();
                    break;
                case "tRNS": trns = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
                case "IEND": sawEnd = true; break;
                default:
                    if ((type[0] & 0x20) == 0) return (null, $"critical chunk {type} isn't supported");
                    break; // ancillary: skipped
            }
            at += 12 + length;
            if (sawEnd) break;
        }
        if (colour < 0) return (null, "no IHDR");
        if (!sawEnd) return (null, "no IEND");
        if (colour == 3 && palette == null) return (null, "palette image without PLTE");

        int channels = colour switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        int bpp = Math.Max(1, channels * depth / 8);
        int stride = (width * channels * depth + 7) / 8;
        byte[] raw;
        try
        {
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            using var output = new MemoryStream();
            z.CopyTo(output);
            raw = output.ToArray();
        }
        catch (InvalidDataException e) { return (null, "IDAT doesn't inflate: " + e.Message); }
        if (raw.Length != (long) (stride + 1) * height) return (null, $"pixel data is {raw.Length} bytes, expected {(stride + 1) * height}");

        var rows = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            int filter = raw[y * (stride + 1)];
            var src = raw.AsSpan(y * (stride + 1) + 1, stride);
            var cur = rows.AsSpan(y * stride, stride);
            var prev = y > 0 ? rows.AsSpan((y - 1) * stride, stride) : Span<byte>.Empty;
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? cur[i - bpp] : 0, b = y > 0 ? prev[i] : 0, c = i >= bpp && y > 0 ? prev[i - bpp] : 0;
                int predicted = filter switch
                {
                    0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, 4 => Paeth(a, b, c),
                    _ => -1,
                };
                if (predicted < 0) return (null, $"row {y}: unknown filter {filter}");
                cur[i] = (byte) (src[i] + predicted);
            }
        }

        var bgra = new byte[width * height * 4];
        int Sample(ReadOnlySpan<byte> row, int index) => depth switch
        {
            8 => row[index],
            _ => (row[index * depth / 8] >> (8 - depth - index * depth % 8)) & ((1 << depth) - 1),
        };
        for (int y = 0; y < height; y++)
        {
            var row = rows.AsSpan(y * stride, stride);
            for (int x = 0; x < width; x++)
            {
                int d = (y * width + x) * 4;
                byte r, g, b, al = 255;
                switch (colour)
                {
                    case 0:
                    {
                        int v = Sample(row, x);
                        if (trns is { Length: >= 2 } && BinaryPrimitives.ReadUInt16BigEndian(trns) == v) al = 0;
                        r = g = b = (byte) (v * 255 / ((1 << depth) - 1));
                        break;
                    }
                    case 3:
                    {
                        int i = Sample(row, x);
                        if (i * 3 + 2 >= palette!.Length) return (null, $"pixel {x},{y}: palette index {i} past the palette");
                        r = palette[i * 3]; g = palette[i * 3 + 1]; b = palette[i * 3 + 2];
                        if (trns != null && i < trns.Length) al = trns[i];
                        break;
                    }
                    case 4: r = g = b = row[x * 2]; al = row[x * 2 + 1]; break;
                    case 2:
                        r = row[x * 3]; g = row[x * 3 + 1]; b = row[x * 3 + 2];
                        if (trns is { Length: 6 } && BinaryPrimitives.ReadUInt16BigEndian(trns) == r
                            && BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(2)) == g && BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(4)) == b) al = 0;
                        break;
                    default: r = row[x * 4]; g = row[x * 4 + 1]; b = row[x * 4 + 2]; al = row[x * 4 + 3]; break;
                }
                bgra[d] = b; bgra[d + 1] = g; bgra[d + 2] = r; bgra[d + 3] = al;
            }
        }
        var kind = colour switch { 0 => "grey", 2 => "RGB", 3 => "palette", 4 => "grey+alpha", _ => "RGBA" };
        return (new DecodedImage(width, height, bgra, $"PNG {kind} {depth}-bit"), null);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
