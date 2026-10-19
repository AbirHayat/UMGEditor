using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace UmgEditor.Core.Textures;

/// <summary>Minimal RGBA8 PNG encoder (no filters) for exporting decoded textures without an imaging library.</summary>
public static class PngWriter
{
    public static void Write(string path, DecodedImage image)
    {
        if (image.IsPlaceholder) throw new ArgumentException("placeholder has no pixels");
        using var file = File.Create(path);
        file.Write([0x89, (byte) 'P', (byte) 'N', (byte) 'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), image.Height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 6;  // colour type RGBA
        Chunk(file, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[1 + image.Width * 4];
            for (int y = 0; y < image.Height; y++)
            {
                row[0] = 0; // filter: none
                for (int x = 0; x < image.Width; x++)
                {
                    int s = (y * image.Width + x) * 4, d = 1 + x * 4;
                    row[d] = image.Bgra[s + 2]; row[d + 1] = image.Bgra[s + 1]; row[d + 2] = image.Bgra[s]; row[d + 3] = image.Bgra[s + 3];
                }
                z.Write(row);
            }
        }
        Chunk(file, "IDAT", raw.ToArray());
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = new Crc32();
        crc.Append(typeBytes);
        crc.Append(data);
        Span<byte> c = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(c, crc.GetCurrentHashAsUInt32());
        s.Write(c);
    }
}
