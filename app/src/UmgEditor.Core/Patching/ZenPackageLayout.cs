using System.Buffers.Binary;
using CUE4Parse.UE4.Assets;

namespace UmgEditor.Core.Patching;

/// <summary>Where one export's serialized data sits inside the raw Zen package chunk.</summary>
public readonly record struct ExportSpan(int ExportIndex, int Start, int Size)
{
    public int End => Start + Size;
}

/// <summary>
/// Byte layout of a UE 4.26 Zen package chunk (the ExportBundleData chunk = what the provider returns for the .uasset).
/// Export data is laid out in export-bundle order starting at GraphDataOffset + GraphDataSize, each Serialize entry
/// taking its export's CookedSerialSize: the same rule as CUE4Parse IoPackage.PopulateExports (whose realm is private).
/// Name map and export map are taken from CUE4Parse's parse of the same bytes.
/// </summary>
public sealed class ZenPackageLayout
{
    private const int SummarySize = 64; // FMappedName×2, PackageFlags, CookedHeaderSize, 9×int32 offsets/sizes, pad

    public const int ExportMapEntrySize = 72; // CookedSerialOffset @0, CookedSerialSize @8 (uint64 each), …

    public IReadOnlyList<string> NameMap { get; }
    public IReadOnlyList<ExportSpan> Exports { get; }
    public int ExportDataStart { get; }
    /// <summary>Where the export map starts in the chunk (summary @44).</summary>
    public int ExportMapOffset { get; }
    /// <summary>Each export's CookedSerialOffset as CUE4Parse read it (by export index).</summary>
    public IReadOnlyList<ulong> CookedSerialOffsets { get; }

    private ZenPackageLayout(IReadOnlyList<string> names, IReadOnlyList<ExportSpan> exports, int exportDataStart, int exportMapOffset, IReadOnlyList<ulong> cookedOffsets)
    {
        NameMap = names;
        Exports = exports;
        ExportDataStart = exportDataStart;
        ExportMapOffset = exportMapOffset;
        CookedSerialOffsets = cookedOffsets;
    }

    public static ZenPackageLayout Read(ReadOnlySpan<byte> chunk, IoPackage parsed)
    {
        if (chunk.Length < SummarySize) throw new InvalidDataException("chunk too small for a Zen package summary");

        // FPackageSummary (4.26): Name @0 (8) SourceName @8 (8) PackageFlags @16 CookedHeaderSize @20 NameMapNamesOffset @24
        // NameMapNamesSize @28 NameMapHashesOffset @32 NameMapHashesSize @36 ImportMapOffset @40 ExportMapOffset @44
        // ExportBundlesOffset @48 GraphDataOffset @52 GraphDataSize @56 Pad @60
        int exportMapOffset = I32(chunk, 44), exportBundlesOffset = I32(chunk, 48), graphDataOffset = I32(chunk, 52), graphDataSize = I32(chunk, 56);
        var exportCount = parsed.ExportMap.Length;
        if ((exportBundlesOffset - exportMapOffset) / ExportMapEntrySize != exportCount)
            throw new InvalidDataException($"export map size mismatch: summary implies {(exportBundlesOffset - exportMapOffset) / ExportMapEntrySize}, CUE4Parse read {exportCount}");

        // export bundles: headers (FirstEntryIndex, EntryCount) until the remaining space holds exactly their entries
        var bundles = new List<(uint First, uint Count)>();
        int remaining = (graphDataOffset - exportBundlesOffset) / 8, found = 0, pos = exportBundlesOffset;
        while (found < remaining)
        {
            remaining--;
            var header = (U32(chunk, pos), U32(chunk, pos + 4));
            bundles.Add(header);
            found += (int) header.Item2;
            pos += 8;
        }
        if (found != remaining) throw new InvalidDataException($"export bundle count mismatch ({found} != {remaining})");
        int entriesStart = pos;

        var spans = new List<ExportSpan>(exportCount);
        int dataStart = graphDataOffset + graphDataSize, cursor = dataStart;
        foreach (var (first, count) in bundles)
        {
            for (uint i = 0; i < count; i++)
            {
                int e = entriesStart + (int) (first + i) * 8;
                var exportIndex = (int) U32(chunk, e);
                var command = U32(chunk, e + 4);
                if (command != 1) continue; // ExportCommandType_Create carries no data
                var size = (int) parsed.ExportMap[exportIndex].CookedSerialSize;
                spans.Add(new ExportSpan(exportIndex, cursor, size));
                cursor += size;
            }
        }
        if (cursor > chunk.Length) throw new InvalidDataException($"export data runs past the chunk end ({cursor} > {chunk.Length})");

        var names = parsed.NameMap.Select(n => n.Name ?? "").ToArray();
        return new ZenPackageLayout(names, spans.OrderBy(s => s.ExportIndex).ToList(), dataStart, exportMapOffset,
            parsed.ExportMap.Select(e => e.CookedSerialOffset).ToArray());
    }

    private static int I32(ReadOnlySpan<byte> c, int at) => BinaryPrimitives.ReadInt32LittleEndian(c[at..]);
    private static uint U32(ReadOnlySpan<byte> c, int at) => BinaryPrimitives.ReadUInt32LittleEndian(c[at..]);

    public int NameIndex(string name)
    {
        for (int i = 0; i < NameMap.Count; i++) if (NameMap[i] == name) return i;
        return -1;
    }

    public ExportSpan SpanOf(int exportIndex) =>
        Exports.FirstOrDefault(s => s.ExportIndex == exportIndex) is { Size: > 0 } s ? s : throw new KeyNotFoundException($"export {exportIndex} has no serialized data");
}
