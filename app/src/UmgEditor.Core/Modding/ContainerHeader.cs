using System.Buffers.Binary;

namespace UmgEditor.Core.Modding;

/// <summary>One package's store entry: export counts plus the ids of the packages it imports.</summary>
public sealed record StoreEntry(int ExportCount, int ExportBundleCount, IReadOnlyList<ulong> ImportedPackages)
{
    public bool SameAs(StoreEntry o) => ExportCount == o.ExportCount && ExportBundleCount == o.ExportBundleCount && ImportedPackages.SequenceEqual(o.ImportedPackages);
}

/// <summary>
/// eFootball (UE 4.26, pre-version) IoStore container header chunk; C# port of work/scripts/hdr.py (parse) and of the
/// header assembly in make_mod.py (build). Layout: ContainerId u64, PackageCount u32, Names (i32 size + bytes),
/// NameHashes (i32 size + bytes), PackageIds (i32 count + u64[]), StoreEntries (i32 size + blob of 16-byte entries
/// {ExportCount, ExportBundleCount, CArrayView imports (count, offset relative to the view)} followed by the import
/// arrays), then CulturePackageMap and PackageRedirects (both empty here), zero-padded to 16 bytes.
/// </summary>
public sealed record ContainerHeader(ulong ContainerId, uint PackageCount, byte[] Names, byte[] NameHashes, IReadOnlyList<ulong> PackageIds, IReadOnlyList<StoreEntry> Entries)
{
    /// <summary>Throws InvalidDataException when the bytes aren't a readable header (hdr.parse would fail or read garbage).</summary>
    public static ContainerHeader Parse(ReadOnlySpan<byte> b)
    {
        try
        {
            int o = 0;
            var cid = BinaryPrimitives.ReadUInt64LittleEndian(b[o..]); o += 8;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(b[o..]); o += 4;
            var names = Sized(b, ref o);
            var hashes = Sized(b, ref o);
            var idCount = Count(b, ref o, 8);
            var ids = new ulong[idCount];
            for (int i = 0; i < idCount; i++, o += 8) ids[i] = BinaryPrimitives.ReadUInt64LittleEndian(b[o..]);
            var se = Sized(b, ref o);
            var entries = new List<StoreEntry>(idCount);
            for (int i = 0; i < idCount; i++)
            {
                int p = 16 * i;
                var ec = BinaryPrimitives.ReadInt32LittleEndian(se.AsSpan(p));
                var ebc = BinaryPrimitives.ReadInt32LittleEndian(se.AsSpan(p + 4));
                var impCount = BinaryPrimitives.ReadUInt32LittleEndian(se.AsSpan(p + 8));
                var impOffset = BinaryPrimitives.ReadUInt32LittleEndian(se.AsSpan(p + 12));
                var imports = new ulong[impCount];
                for (int k = 0; k < impCount; k++) imports[k] = BinaryPrimitives.ReadUInt64LittleEndian(se.AsSpan(checked(p + 8 + (int) impOffset + 8 * k)));
                entries.Add(new StoreEntry(ec, ebc, imports));
            }
            return new ContainerHeader(cid, count, names, hashes, ids, entries);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException or InvalidDataException)
        {
            throw new InvalidDataException($"not a readable eFootball container header ({b.Length} bytes): {e.Message}", e);
        }
    }

    /// <summary>
    /// A mod header the way make_mod.py builds it: the given packages in order, no names, the source NameHashes,
    /// each entry's imports right after the entry table, empty culture map and redirects, padded to 16.
    /// </summary>
    public static byte[] Build(ulong containerId, byte[] nameHashes, IReadOnlyList<(ulong PackageId, StoreEntry Entry)> packages)
    {
        var se = new List<byte>();
        var table = new byte[16 * packages.Count];
        var data = new List<byte>();
        for (int i = 0; i < packages.Count; i++)
        {
            var e = packages[i].Entry;
            var view = 16 * i + 8;
            var at = table.Length + data.Count;
            BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(16 * i), e.ExportCount);
            BinaryPrimitives.WriteInt32LittleEndian(table.AsSpan(16 * i + 4), e.ExportBundleCount);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(16 * i + 8), (uint) e.ImportedPackages.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(16 * i + 12), (uint) (at - view)); // CArrayView offset is relative to the view
            foreach (var id in e.ImportedPackages) data.AddRange(U64(id));
        }
        se.AddRange(table);
        se.AddRange(data);

        var b = new List<byte>();
        b.AddRange(U64(containerId));
        b.AddRange(U32((uint) packages.Count));
        b.AddRange(I32(0));
        b.AddRange(I32(nameHashes.Length)); b.AddRange(nameHashes);
        b.AddRange(I32(packages.Count));
        foreach (var (id, _) in packages) b.AddRange(U64(id));
        b.AddRange(I32(se.Count)); b.AddRange(se);
        b.AddRange(I32(0)); b.AddRange(I32(0));
        while (b.Count % 16 != 0) b.Add(0);
        return [.. b];
    }

    private static byte[] Sized(ReadOnlySpan<byte> b, ref int o)
    {
        var n = Count(b, ref o, 1);
        var bytes = b.Slice(o, n).ToArray();
        o += n;
        return bytes;
    }

    private static int Count(ReadOnlySpan<byte> b, ref int o, int elementSize)
    {
        var n = BinaryPrimitives.ReadInt32LittleEndian(b[o..]);
        o += 4;
        if (n < 0 || (long) n * elementSize > b.Length - o) throw new InvalidDataException($"count {n} at {o - 4} runs past the end");
        return n;
    }

    private static byte[] U64(ulong v) { var a = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(a, v); return a; }
    private static byte[] U32(uint v) { var a = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(a, v); return a; }
    private static byte[] I32(int v) { var a = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(a, v); return a; }
}
