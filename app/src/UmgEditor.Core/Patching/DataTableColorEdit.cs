using System.Buffers.Binary;

namespace UmgEditor.Core.Patching;

/// <summary>
/// One colour of a cooked DataTable row (branch experiment, canary C16: ColorTable's STR_SWITCH_BRAND_1 → white,
/// STR_SWITCH_BRAND_2 → black, the text colours of the TV graphics). Layout after the export's tagged properties and
/// their None: the object's bHasGuid (int32, + 16-byte GUID when set), NumRows (int32), then per row its FName and the
/// row struct's tagged properties up to None. The colour is the row's StructProperty "colorValue…" of struct Color
/// (4 bytes: B, G, R, A). Found by that walk, checked against the parse, applied through NameRenamePatch (writes +
/// expected B/G/R/A/Hex values) so the byte diff and the re-parse are checked as for every other edit.
/// </summary>
public static class DataTableColorEdit
{
    /// <param name="Key">The colour's flattened path, e.g. "Rows.STR_SWITCH_BRAND_1.colorValue_…" (+ ".R" … ".Hex")</param>
    public sealed record Planned(int Offset, byte[] Bgra, Dictionary<(int Export, string Path), string> Expected, string What, string Key);

    /// <param name="rgba">The new colour as R, G, B, A</param>
    public static (Planned? Plan, string? Why) Plan(GameSource source, string fileKey, byte[] chunk, string rowName, (byte R, byte G, byte B, byte A) rgba)
    {
        var parsed = source.ParsePackage(fileKey, chunk);
        if (parsed.ExportsLazy.Length != 1 || parsed.ExportsLazy[0].Value.ExportType != "DataTable") return (null, "not a package with one DataTable export");
        var layout = ZenPackageLayout.Read(chunk, parsed);
        var span = layout.SpanOf(0);
        var walker = new TaggedPropertyWalker(layout.NameMap);
        var top = walker.TopLevelTags(chunk, span);
        int pos = top.Count > 0 ? top[^1].End : span.Start;
        if (layout.NameMap[I32(chunk, pos)] != "None") return (null, "no None after the DataTable's properties");
        pos += 8;
        var hasGuid = I32(chunk, pos); pos += 4;
        if (hasGuid is not (0 or 1)) return (null, $"bHasGuid is {hasGuid}");
        if (hasGuid == 1) pos += 16;
        var rows = I32(chunk, pos); pos += 4;
        if (rows <= 0 || rows > 100000) return (null, $"NumRows {rows}");
        var flat = TagInserter.Flatten(parsed.ExportsLazy[0].Value);
        for (int r = 0; r < rows; r++)
        {
            var nameIndex = I32(chunk, pos);
            var number = I32(chunk, pos + 4);
            var name = layout.NameMap[nameIndex] + (number == 0 ? "" : $"_{number - 1}");
            pos += 8;
            var rowTags = walker.TopLevelTags(chunk, new ExportSpan(0, pos, span.End - pos));
            int rowEnd = (rowTags.Count > 0 ? rowTags[^1].End : pos) + 8;
            if (name == rowName)
            {
                var colours = rowTags.Where(t => t.Name.StartsWith("colorValue", StringComparison.Ordinal) && t.Type == "StructProperty" && t.Extra == "Color" && t.Size == 4).ToList();
                if (colours.Count != 1) return (null, $"row {rowName} has {colours.Count} Color colorValue properties");
                var c = colours[0];
                var key = $"Rows.{rowName}.{c.Name}";
                byte b0 = chunk[c.ValueStart], g0 = chunk[c.ValueStart + 1], r0 = chunk[c.ValueStart + 2], a0 = chunk[c.ValueStart + 3];
                if (flat.GetValueOrDefault(key + ".B") != b0.ToString() || flat.GetValueOrDefault(key + ".G") != g0.ToString()
                    || flat.GetValueOrDefault(key + ".R") != r0.ToString() || flat.GetValueOrDefault(key + ".A") != a0.ToString())
                    return (null, $"row {rowName}: the bytes {r0},{g0},{b0},{a0} at 0x{c.ValueStart:X} don't match the parse");
                var expected = new Dictionary<(int, string), string>
                {
                    [(0, key + ".R")] = rgba.R.ToString(), [(0, key + ".G")] = rgba.G.ToString(), [(0, key + ".B")] = rgba.B.ToString(),
                    [(0, key + ".Hex")] = $"{rgba.R:X2}{rgba.G:X2}{rgba.B:X2}",
                };
                if (rgba.A != a0) expected[(0, key + ".A")] = rgba.A.ToString();
                foreach (var k in expected.Keys.ToList())
                    if (flat.GetValueOrDefault(k.Item2)?.Trim('"') == expected[k]) expected.Remove(k); // unchanged components
                return (new Planned(c.ValueStart, [rgba.B, rgba.G, rgba.R, rgba.A], expected,
                    $"{rowName} {r0:X2}{g0:X2}{b0:X2} → {rgba.R:X2}{rgba.G:X2}{rgba.B:X2} @0x{c.ValueStart:X}", key), null);
            }
            pos = rowEnd;
        }
        return (null, $"no row {rowName}");
    }

    private static int I32(ReadOnlySpan<byte> c, int at) => BinaryPrimitives.ReadInt32LittleEndian(c[at..]);
}
