using System.Buffers.Binary;
using System.Text;
using CUE4Parse.Utils;

namespace UmgEditor.Core.Patching;

/// <summary>One name of a Zen package's name map: its text, and where its string bytes and its hash are in the chunk.</summary>
public sealed record ZenName(int Index, string Text, bool Wide, int StringAt, int ByteLength, int HashAt, ulong Hash);

/// <summary>
/// The name map of a UE 4.26 Zen package chunk. NameMapNamesOffset @24 / NameMapNamesSize @28: one
/// FSerializedNameHeader per name (2 bytes: bit 7 of the first = UTF-16, then the length in 15 bits) followed by its
/// characters (ANSI, or UTF-16 LE). NameMapHashesOffset @32 / NameMapHashesSize @36: the hash algorithm id
/// (0xC1640000), then one uint64 per name = CityHash64 of the lower-cased string as stored. Soft object paths (e.g.
/// a uiImage's texture) live here as plain names, so a same-length rename here re-points them (branch experiment, C13).
/// </summary>
public static class ZenNameMap
{
    public const ulong HashAlgorithmId = 0xC1640000;

    public static IReadOnlyList<ZenName> Read(ReadOnlySpan<byte> chunk)
    {
        int namesAt = I32(chunk, 24), namesSize = I32(chunk, 28), hashesAt = I32(chunk, 32), hashesSize = I32(chunk, 36);
        if (namesAt < 0 || hashesAt < 0 || namesAt + namesSize > chunk.Length || hashesAt + hashesSize > chunk.Length || hashesSize < 8 || hashesSize % 8 != 0)
            throw new InvalidDataException($"name map offsets out of range (names @{namesAt}+{namesSize}, hashes @{hashesAt}+{hashesSize}, chunk {chunk.Length})");
        if (BinaryPrimitives.ReadUInt64LittleEndian(chunk[hashesAt..]) != HashAlgorithmId)
            throw new InvalidDataException($"unknown name hash algorithm 0x{BinaryPrimitives.ReadUInt64LittleEndian(chunk[hashesAt..]):X}");
        var count = hashesSize / 8 - 1;
        var names = new List<ZenName>(count);
        int pos = namesAt;
        for (int i = 0; i < count; i++)
        {
            if (pos + 2 > namesAt + namesSize) throw new InvalidDataException($"name {i} runs past the names block");
            bool wide = (chunk[pos] & 0x80) != 0;
            int length = ((chunk[pos] & 0x7F) << 8) | chunk[pos + 1], start = pos + 2, bytes = wide ? 2 * length : length;
            if (start + bytes > namesAt + namesSize) throw new InvalidDataException($"name {i} runs past the names block");
            var text = wide ? Encoding.Unicode.GetString(chunk.Slice(start, bytes)) : Encoding.Latin1.GetString(chunk.Slice(start, bytes));
            int hashAt = hashesAt + 8 + 8 * i;
            names.Add(new ZenName(i, text, wide, start, bytes, hashAt, BinaryPrimitives.ReadUInt64LittleEndian(chunk[hashAt..])));
            pos = start + bytes;
        }
        if (pos != namesAt + namesSize) throw new InvalidDataException($"{namesAt + namesSize - pos} bytes left after the last name");
        return names;
    }

    /// <summary>The name hash: CityHash64 of the lower-cased string (ASCII lower-casing, as FName does), as stored.</summary>
    public static ulong Hash(string text, bool wide)
    {
        var lower = string.Create(text.Length, text, (span, s) => { for (int i = 0; i < s.Length; i++) span[i] = s[i] is >= 'A' and <= 'Z' ? (char) (s[i] + 32) : s[i]; });
        return CityHash.CityHash64(wide ? Encoding.Unicode.GetBytes(lower) : Encoding.Latin1.GetBytes(lower));
    }

    /// <summary>
    /// Same-length renames of whole names. Each "from" must be exactly one ANSI name whose stored hash this code
    /// reproduces, "to" must be ASCII, as long as "from", and not already a name. Returns the new chunk (string bytes and
    /// hashes rewritten, nothing else), or why not.
    /// </summary>
    public static (byte[]? Chunk, string? Why) Rename(ReadOnlySpan<byte> chunk, IReadOnlyList<(string From, string To)> renames)
    {
        IReadOnlyList<ZenName> names;
        try { names = Read(chunk); }
        catch (InvalidDataException e) { return (null, e.Message); }
        var result = chunk.ToArray();
        foreach (var (from, to) in renames)
        {
            var hits = names.Where(n => n.Text == from).ToList();
            if (hits.Count != 1) return (null, $"\"{from}\" occurs {hits.Count} times in the name map (needs exactly 1)");
            var n = hits[0];
            if (n.Wide) return (null, $"\"{from}\" is a UTF-16 name (only ANSI names are renamed)");
            if (to.Length != from.Length) return (null, $"\"{to}\" is {to.Length} characters, \"{from}\" {from.Length} (a rename must keep the length)");
            if (to.Any(c => c is < (char) 0x20 or > (char) 0x7E)) return (null, $"\"{to}\" isn't plain ASCII");
            if (names.Any(m => m.Text == to)) return (null, $"\"{to}\" is already in the name map");
            if (Hash(from, false) != n.Hash) return (null, $"the stored hash of \"{from}\" ({n.Hash:x16}) isn't reproduced ({Hash(from, false):x16}); the hash rule doesn't hold for this package");
            Encoding.ASCII.GetBytes(to).CopyTo(result, n.StringAt);
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(n.HashAt), Hash(to, false));
        }
        return (result, null);
    }

    private static int I32(ReadOnlySpan<byte> c, int at) => BinaryPrimitives.ReadInt32LittleEndian(c[at..]);
}
