using System.Buffers.Binary;

namespace UmgEditor.Core.Patching;

/// <summary>
/// A NameProperty member of a stored top-level tagged struct (branch experiment, canary C17b: uiText
/// <c>_font.TypefaceFontName</c> = HeadlineBoldSub, the pes typeface entry re-pointed to UCL's font). The tag is cooked
/// UE 4.26 layout, 33 bytes: Name, Type NameProperty, Size 8, ArrayIndex 0, HasPropertyGuid 0, then the value FName;
/// <see cref="Build"/> must reproduce a stock tag of the same member byte for byte (the anchor, <see cref="CheckAgainst"/>).
/// <see cref="Insert"/> adds the member where it isn't stored (declaration order inside the struct; the struct tag's Size,
/// CookedSerialSize +33 and every later CookedSerialOffset +33), the mirror of <see cref="EnumTagInsert"/> one struct
/// deeper. <see cref="Swap"/> changes a stored value in place (8 bytes). Every write is located by the tag walk, and its
/// smallest unique window on the input bytes (±32 first, refused beyond ±512) is logged. Checks: cut out again the
/// result is the input; the re-parse differs only by the member, with the value; the walk shows exactly one more tag.
/// All names must already be in the name map (<see cref="EnumTagInsert.AppendNames"/>).
/// </summary>
public static class NameTagEdit
{
    public sealed record Result(byte[]? Chunk, IReadOnlyList<string> Refusals, IReadOnlyList<string> Log);

    public const int TagLength = 33;

    /// <summary>FSlateFontInfo's tagged members in declaration order (UE 4.26).</summary>
    public static readonly IReadOnlyList<string> SlateFontInfoOrder = ["FontObject", "FontMaterial", "OutlineSettings", "TypefaceFontName", "Size"];

    public static (byte[]? Tag, string? Why) Build(IReadOnlyList<string> nameMap, string tagName, string value)
    {
        var tag = new byte[TagLength];
        foreach (var (at, text) in new[] { (0, tagName), (8, "NameProperty"), (25, value) })
        {
            if (UniqueName(nameMap, text) is not { } index) return (null, $"\"{text}\" occurs {nameMap.Count(n => n == text)} times in the name map (needs exactly 1)");
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(at), index);
        }
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 8);
        return (tag, null);
    }

    /// <summary>The anchor: Build on a stock package's name map gives exactly the bytes of its stored member tag.</summary>
    public static string? CheckAgainst(GameSource source, string packagePath, string exportName, string structTag, string tagName, string value)
    {
        var (fileKey, bytes) = source.ReadPackageBytes(packagePath);
        var parsed = source.ParsePackage(fileKey, bytes);
        var layout = ZenPackageLayout.Read(bytes, parsed);
        var index = parsed.ExportsLazy.Select(e => e.Value.Name).ToList().IndexOf(exportName);
        if (index < 0) return $"{packagePath} has no export {exportName}";
        var (_, members, why) = Members(bytes, layout, index, structTag);
        if (members == null) return why;
        var stored = members.Where(t => t.Name == tagName).ToList();
        if (stored.Count != 1 || NameValue(bytes, layout, stored[0]) != value) return $"{exportName}.{structTag}.{tagName} isn't stored once as {value}";
        var (tag, buildWhy) = Build(layout.NameMap, tagName, value);
        if (tag == null) return buildWhy;
        var actual = bytes.AsSpan(stored[0].TagStart, stored[0].Length);
        return actual.SequenceEqual(tag) ? null : $"the built tag isn't the stored one: {Convert.ToHexString(tag)} vs {Convert.ToHexString(actual)}";
    }

    public static Result Insert(GameSource source, string fileKey, byte[] chunk, string exportName, string structTag, string tagName, string value, IReadOnlyList<string> declarationOrder)
    {
        var log = new List<string>();
        Result Refuse(string why) => new(null, [why], log);
        var what = $"{exportName}.{structTag}.{tagName}";
        if (!declarationOrder.Contains(tagName)) return Refuse($"{what}: {tagName} isn't in the declaration order list");

        var parsed = source.ParsePackage(fileKey, chunk);
        var exports = parsed.ExportsLazy.Select(e => e.Value.Name).ToList();
        if (exports.Count(e => e == exportName) != 1) return Refuse($"{exports.Count(e => e == exportName)} exports named {exportName}");
        var exportIndex = exports.IndexOf(exportName);
        var layout = ZenPackageLayout.Read(chunk, parsed);
        var (structLoc, members, why) = Members(chunk, layout, exportIndex, structTag);
        if (members == null) return Refuse(why!);
        var s = structLoc!;
        if (members.Any(t => t.Name == tagName)) return Refuse($"{what} is already stored (swap the value instead)");
        var rank = declarationOrder.ToList().IndexOf(tagName);
        var before = members.FirstOrDefault(t => declarationOrder.Contains(t.Name) && declarationOrder.ToList().IndexOf(t.Name) > rank);
        var at = before?.TagStart ?? s.End - 8;
        var (tag, buildWhy) = Build(layout.NameMap, tagName, value);
        if (tag == null) return Refuse(buildWhy!);
        if (BytePlanner.UniqueWindow(chunk, at, 0) is not { } window) return Refuse($"{what}: no stock window up to ±{BytePlanner.MaxAnimWindow} around the insertion point occurs exactly once");
        var sizeAt = s.TagStart + 16;
        if (BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(sizeAt)) != s.Size) return Refuse($"{structTag}: the struct tag's Size field doesn't hold {s.Size}");
        log.Add($"{what} = {value}: {TagLength}-byte NameProperty tag inserted @0x{at:X} (before {before?.Name ?? "None"}) in export {exportIndex}, unique window ±{window}: {Convert.ToHexString(tag)}");

        var header = new List<(int Offset, ulong Old, ulong New, int Width)> { (sizeAt, (ulong) s.Size, (ulong) (s.Size + TagLength), 4) };
        var offsets = layout.CookedSerialOffsets;
        var span = layout.SpanOf(exportIndex);
        for (int i = 0; i < offsets.Count; i++)
        {
            var entry = layout.ExportMapOffset + i * ZenPackageLayout.ExportMapEntrySize;
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(chunk.AsSpan(entry));
            var size = BinaryPrimitives.ReadUInt64LittleEndian(chunk.AsSpan(entry + 8));
            if (offset != offsets[i]) return Refuse($"export map entry {i} holds CookedSerialOffset {offset}, the parse {offsets[i]}");
            if (i == exportIndex)
            {
                if (size != (ulong) span.Size) return Refuse($"CookedSerialSize {size} isn't the export's size {span.Size}");
                header.Add((entry + 8, size, size + TagLength, 8));
            }
            else if (offset > offsets[exportIndex]) header.Add((entry, offset, offset + TagLength, 8));
        }
        static void Put(byte[] b, int o, ulong v, int width) { if (width == 4) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(o), (int) v); else BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), v); }
        var withHeader = chunk.ToArray();
        foreach (var (o, _, now, w) in header) Put(withHeader, o, now, w);
        var result = new byte[chunk.Length + TagLength];
        withHeader.AsSpan(0, at).CopyTo(result);
        tag.CopyTo(result, at);
        withHeader.AsSpan(at).CopyTo(result.AsSpan(at + TagLength));
        log.Add($"header: {structTag} Size {s.Size} → {s.Size + TagLength}, 1 CookedSerialSize, {header.Count - 2} later CookedSerialOffsets (+{TagLength}); chunk {chunk.Length} → {result.Length}");

        // check 1: cut the tag out and put the header values back → the input again
        var back = new byte[chunk.Length];
        result.AsSpan(0, at).CopyTo(back);
        result.AsSpan(at + TagLength).CopyTo(back.AsSpan(at));
        foreach (var (o, old, _, w) in header) Put(back, o, old, w);
        if (!back.AsSpan().SequenceEqual(chunk)) return Refuse("with the tag cut out and the header values put back, the result isn't the input");

        // check 2: the re-parse differs only by the new member, holding the value
        var key = $"Properties.{structTag}.{tagName}";
        bool seen = false;
        var (after, problems, _) = TagInserter.ReparseDiff(source, fileKey, parsed, result, null, (export, k, old, now) =>
        {
            var ok = export == exportIndex && k == key && old == null && now?.Trim('"') == value;
            seen |= ok;
            return ok;
        });
        if (problems.Count > 0) return new Result(null, problems, log);
        if (!seen) return Refuse($"the re-parse doesn't show {what} = {value}");

        // check 3: the walk shows exactly one more member, the new one, where it was planned
        var newLayout = ZenPackageLayout.Read(result, after!);
        var (_, newMembers, newWhy) = Members(result, newLayout, exportIndex, structTag);
        if (newMembers == null) return Refuse("the edited struct doesn't walk: " + newWhy);
        var k2 = newMembers.FindIndex(t => t.Name == tagName);
        if (newMembers.Count != members.Count + 1 || k2 < 0 || newMembers[k2].TagStart != at || NameValue(result, newLayout, newMembers[k2]) != value
            || !newMembers.Where((_, i) => i != k2).Select(t => t.Name).SequenceEqual(members.Select(t => t.Name)))
            return Refuse("the walk of the edited struct doesn't show exactly the one new member where it was planned");
        log.Add($"re-parse: only {what} is new ({value}); the walk of {structTag} ends at its None tag");
        return new Result(result, [], log);
    }

    /// <summary>A stored NameProperty member from one name to another (8 bytes in place), checked by the byte diff and re-parse.</summary>
    public static Result Swap(GameSource source, string fileKey, byte[] chunk, string exportName, string structTag, string tagName, string from, string to)
    {
        var log = new List<string>();
        Result Refuse(string why) => new(null, [why], log);
        var what = $"{exportName}.{structTag}.{tagName}";
        var parsed = source.ParsePackage(fileKey, chunk);
        var exports = parsed.ExportsLazy.Select(e => e.Value.Name).ToList();
        if (exports.Count(e => e == exportName) != 1) return Refuse($"{exports.Count(e => e == exportName)} exports named {exportName}");
        var exportIndex = exports.IndexOf(exportName);
        var layout = ZenPackageLayout.Read(chunk, parsed);
        var (_, members, why) = Members(chunk, layout, exportIndex, structTag);
        if (members == null) return Refuse(why!);
        var stored = members.Where(t => t.Name == tagName).ToList();
        if (stored is not [{ Type: "NameProperty", Size: 8 } m]) return Refuse($"{what} isn't stored once as an 8-byte NameProperty");
        if (NameValue(chunk, layout, m) != from) return Refuse($"{what} holds {NameValue(chunk, layout, m)}, not {from}");
        if (UniqueName(layout.NameMap, to) is not { } index) return Refuse($"\"{to}\" isn't in the name map exactly once");
        if (BytePlanner.UniqueWindow(chunk, m.ValueStart, 8) is not { } window) return Refuse($"{what}: no stock window up to ±{BytePlanner.MaxAnimWindow} occurs exactly once");
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, index); // number 0
        log.Add($"{what}: {from} → {to} @0x{m.ValueStart:X} (name {index}), unique window ±{window}");
        var r = NameRenamePatch.Apply(source, fileKey, chunk, [], [(m.ValueStart, bytes, $"{what} → {to}")],
            new Dictionary<(int, string), string> { [(exportIndex, $"Properties.{structTag}.{tagName}")] = to });
        log.AddRange(r.Log);
        return r.Ok ? new Result(r.Chunk, [], log) : new Result(null, r.Refusals, log);
    }

    /// <summary>The member tags of a stored top-level struct; they must end with its None tag exactly at the struct's size.</summary>
    internal static (TagLocation? Struct, List<TagLocation>? Members, string? Why) Members(ReadOnlySpan<byte> chunk, ZenPackageLayout layout, int exportIndex, string structTag)
    {
        var walker = new TaggedPropertyWalker(layout.NameMap);
        try
        {
            var top = walker.TopLevelTags(chunk, layout.SpanOf(exportIndex)).Where(t => t.Name == structTag).ToList();
            if (top is not [{ Type: "StructProperty" } s]) return (null, null, $"{structTag} isn't stored once as a struct in export {exportIndex}");
            var members = walker.TopLevelTags(chunk, new ExportSpan(exportIndex, s.ValueStart, s.Size));
            var none = members.Count == 0 ? s.ValueStart : members[^1].End;
            if (none + 8 != s.End || NameAt(chunk, layout, none) != "None") return (null, null, $"{structTag}'s members don't end with None at its size");
            return (s, members, null);
        }
        catch (InvalidDataException e) { return (null, null, $"export {exportIndex}: the tags can't be walked ({e.Message})"); }
    }

    internal static string? NameValue(ReadOnlySpan<byte> chunk, ZenPackageLayout layout, TagLocation t) =>
        t is { Type: "NameProperty", Size: 8 } ? NameAt(chunk, layout, t.ValueStart) : null;

    internal static string NameAt(ReadOnlySpan<byte> c, ZenPackageLayout layout, int at)
    {
        var index = BinaryPrimitives.ReadInt32LittleEndian(c[at..]);
        var number = BinaryPrimitives.ReadInt32LittleEndian(c[(at + 4)..]);
        if (index < 0 || index >= layout.NameMap.Count) throw new InvalidDataException($"name index {index} at 0x{at:X}");
        return number == 0 ? layout.NameMap[index] : $"{layout.NameMap[index]}_{number - 1}";
    }

    private static int? UniqueName(IReadOnlyList<string> nameMap, string text)
    {
        var hits = Enumerable.Range(0, nameMap.Count).Where(i => nameMap[i] == text).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }
}

/// <summary>
/// One typeface entry of a Font asset re-pointed to another font face the package already imports (branch experiment,
/// canary C17b: pes SubTypefaces[9] = the Latin sub-typeface, HeadlineBoldSub → NotoSansThai-ExtraBold, whose .ufont
/// the mod replaces with UCL's font). FFontData is native when cooked: bIsCooked (uint32 1), LocalFontFaceAsset
/// (FPackageIndex, an import), SubFaceIndex (int32) = 12 bytes; only the 4-byte import index changes. The walk goes
/// through CompositeFont's tagged structs and arrays of structs (count, inner StructProperty tag, tagged elements), and
/// every FontData found is decoded and compared with the parse: each import index must stand for one face, and different
/// indices for different faces. The new index is copied from a stock entry that already uses the new face. Then the
/// exact byte diff and the re-parse (<see cref="NameRenamePatch.Apply"/>): only that entry's face may differ.
/// </summary>
public static class FontFaceRepoint
{
    public sealed record FontEntry(string Path, int ValueStart, int ImportIndex, string Face);

    public static NameTagEdit.Result Plan(GameSource source, string fileKey, byte[] chunk, string entryPath, string entryName, string fromFace, string toFace)
    {
        var log = new List<string>();
        NameTagEdit.Result Refuse(string why) => new(null, [why], log);
        var parsed = source.ParsePackage(fileKey, chunk);
        if (parsed.ExportsLazy.Length != 1) return Refuse($"{parsed.ExportsLazy.Length} exports (a Font package has one)");
        var flat = TagInserter.Flatten(parsed.ExportsLazy[0].Value);
        var layout = ZenPackageLayout.Read(chunk, parsed);
        List<FontEntry> entries;
        try { entries = Entries(chunk, layout, flat); }
        catch (InvalidDataException e) { return Refuse("the font's tags can't be walked: " + e.Message); }
        log.Add($"{entries.Count} FontData entries walked and decoded");

        // the decoding agrees with the parse: one face per import index, one index per face
        foreach (var g in entries.GroupBy(e => e.ImportIndex))
            if (g.Select(e => e.Face).Distinct().Count() != 1) return Refuse($"import index {g.Key} reads as several faces: {string.Join(", ", g.Select(e => e.Face).Distinct())}");
        foreach (var g in entries.GroupBy(e => e.Face))
            if (g.Select(e => e.ImportIndex).Distinct().Count() != 1) return Refuse($"face {g.Key} has several import indices");

        if (flat.GetValueOrDefault($"Properties.{entryPath}.Name")?.Trim('"') != entryName) return Refuse($"{entryPath} isn't the {entryName} entry");
        var target = entries.SingleOrDefault(e => e.Path == entryPath + ".Font");
        if (target == null) return Refuse($"{entryPath}.Font isn't a walked FontData entry");
        if (target.Face != fromFace) return Refuse($"{entryPath} uses {target.Face}, not {fromFace}");
        var source2 = entries.FirstOrDefault(e => e.Face == toFace);
        if (source2 == null) return Refuse($"the package doesn't use {toFace} anywhere (only an existing import can be used)");
        var at = target.ValueStart + 4;
        if (BytePlanner.UniqueWindow(chunk, at, 4) is not { } window) return Refuse($"{entryPath}: no stock window up to ±{BytePlanner.MaxAnimWindow} occurs exactly once");
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, source2.ImportIndex);
        log.Add($"{entryPath} ({entryName}): LocalFontFaceAsset {target.ImportIndex} ({fromFace}) → {source2.ImportIndex} ({toFace}, as {source2.Path}) @0x{at:X}, unique window ±{window}");

        var face = $"Properties.{entryPath}.Font.LocalFontFaceAsset";
        var expected = new Dictionary<(int, string), string>
        {
            [(0, face + ".ObjectName")] = flat[$"Properties.{source2.Path}.LocalFontFaceAsset.ObjectName"].Trim('"'),
            [(0, face + ".ObjectPath")] = flat[$"Properties.{source2.Path}.LocalFontFaceAsset.ObjectPath"].Trim('"'),
        };
        var r = NameRenamePatch.Apply(source, fileKey, chunk, [], [(at, bytes, $"{entryPath}.Font.LocalFontFaceAsset → {toFace}")], expected);
        log.AddRange(r.Log);
        return r.Ok ? new NameTagEdit.Result(r.Chunk, [], log) : new NameTagEdit.Result(null, r.Refusals, log);
    }

    /// <summary>Every FontData under CompositeFont with its flattened path (as <see cref="TagInserter.Flatten"/> names it), import index and face.</summary>
    public static List<FontEntry> Entries(ReadOnlySpan<byte> chunk, ZenPackageLayout layout, IReadOnlyDictionary<string, string> flat)
    {
        var walker = new TaggedPropertyWalker(layout.NameMap);
        var tags = new List<(string Path, TagLocation Tag)>();
        Walk(chunk, layout, walker, layout.SpanOf(0), "", tags);
        var entries = new List<FontEntry>();
        foreach (var (path, t) in tags.Where(x => x.Tag is { Type: "StructProperty", Extra: "FontData" }))
        {
            if (t.Size != 12) throw new InvalidDataException($"{path}: FontData of {t.Size} bytes (only a cooked face reference, 12 bytes, is handled)");
            var cooked = BinaryPrimitives.ReadInt32LittleEndian(chunk[t.ValueStart..]);
            var index = BinaryPrimitives.ReadInt32LittleEndian(chunk[(t.ValueStart + 4)..]);
            var subFace = BinaryPrimitives.ReadInt32LittleEndian(chunk[(t.ValueStart + 8)..]);
            if (cooked != 1 || index >= 0) throw new InvalidDataException($"{path}: bIsCooked {cooked}, LocalFontFaceAsset {index} (expected 1 and an import)");
            if (flat.GetValueOrDefault($"Properties.{path}.SubFaceIndex") != subFace.ToString()) throw new InvalidDataException($"{path}: SubFaceIndex {subFace} isn't the parse's");
            var face = flat.GetValueOrDefault($"Properties.{path}.LocalFontFaceAsset.ObjectName")?.Trim('"');
            if (face is not { } f || !f.StartsWith("FontFace'") || !f.EndsWith('\'')) throw new InvalidDataException($"{path}: the parse shows no font face");
            entries.Add(new FontEntry(path, t.ValueStart, index, f["FontFace'".Length..^1]));
        }
        return entries;
    }

    private static readonly HashSet<string> NativeStructs = ["FontData", "Guid", "Color", "LinearColor", "Vector2D", "Vector", "Vector4", "SoftObjectPath"];

    /// <summary>Tags of [span] (ending with None exactly at its end), recursing into tagged structs and arrays of structs.</summary>
    private static void Walk(ReadOnlySpan<byte> c, ZenPackageLayout layout, TaggedPropertyWalker walker, ExportSpan span, string prefix, List<(string, TagLocation)> into)
    {
        var tags = walker.TopLevelTags(c, span);
        var none = tags.Count == 0 ? span.Start : tags[^1].End;
        if (prefix.Length > 0 && (none + 8 != span.Start + span.Size || NameTagEdit.NameAt(c, layout, none) != "None"))
            throw new InvalidDataException($"{prefix}: the tags don't end with None at the declared size");
        foreach (var t in tags)
        {
            var path = prefix + t.Name + (t.ArrayIndex > 0 ? $"[{t.ArrayIndex}]" : "");
            into.Add((path, t));
            if (t.Type == "StructProperty" && !NativeStructs.Contains(t.Extra ?? ""))
                Walk(c, layout, walker, new ExportSpan(span.ExportIndex, t.ValueStart, t.Size), path + ".", into);
            else if (t is { Type: "ArrayProperty", Extra: "StructProperty" })
            {
                var count = BinaryPrimitives.ReadInt32LittleEndian(c[t.ValueStart..]);
                int pos = t.ValueStart + 4;
                if (NameTagEdit.NameAt(c, layout, pos) != t.Name || NameTagEdit.NameAt(c, layout, pos + 8) != "StructProperty")
                    throw new InvalidDataException($"{path}: the array's inner tag isn't {t.Name} StructProperty");
                var innerSize = BinaryPrimitives.ReadInt32LittleEndian(c[(pos + 16)..]);
                if (c[pos + 48] != 0) throw new InvalidDataException($"{path}: the inner tag has a property guid");
                pos += 49;
                if (pos + innerSize != t.End) throw new InvalidDataException($"{path}: the inner tag's size doesn't reach the array's end");
                for (int i = 0; i < count; i++)
                {
                    var elementTags = walker.TopLevelTags(c, new ExportSpan(span.ExportIndex, pos, t.End - pos));
                    var end = (elementTags.Count == 0 ? pos : elementTags[^1].End) + 8;
                    Walk(c, layout, walker, new ExportSpan(span.ExportIndex, pos, end - pos), $"{path}[{i}].", into);
                    pos = end;
                }
                if (pos != t.End) throw new InvalidDataException($"{path}: {count} elements end at 0x{pos:X}, the array at 0x{t.End:X}");
            }
        }
    }
}
