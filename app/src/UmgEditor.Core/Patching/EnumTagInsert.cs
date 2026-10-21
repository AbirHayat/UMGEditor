using System.Buffers.Binary;

namespace UmgEditor.Core.Patching;

/// <summary>
/// Adds one top-level enum property to an export (branch experiment, canary C16b: uiText Justification =
/// ETextJustify::Center on the goal and card banners' names, which store none and so use the default Left). The tag is
/// cooked UE 4.26 layout, 41 bytes: Name, Type (ByteProperty for a TEnumAsByte like Justification, EnumProperty for an
/// enum class), Size 8, ArrayIndex 0, EnumName, HasPropertyGuid 0, then the value FName; <see cref="Build"/> must reproduce a stock tag of the same property byte for byte (the anchor,
/// <see cref="CheckAgainst"/>). All four names must already be in the name map (<see cref="ZenNameMap.Append"/>). It goes
/// right after an existing top-level tag (declaration order). The mirror of <see cref="TagRemover"/>: CookedSerialSize
/// grows by 41 and every later CookedSerialOffset moves by it. Checks: cut out again (tag and header values) the result
/// is the input; the re-parse differs only by the new property, with the value; the walk shows exactly one more tag.
/// </summary>
public static class EnumTagInsert
{
    public sealed record Result(byte[]? Chunk, IReadOnlyList<string> Refusals, IReadOnlyList<string> Log);

    public const int TagLength = 41;

    /// <summary>The tag bytes with this name map's indices (number 0 everywhere), or why not.</summary>
    public static (byte[]? Tag, string? Why) Build(IReadOnlyList<string> nameMap, string tagName, string propertyType, string enumType, string value)
    {
        var tag = new byte[TagLength];
        foreach (var (at, text) in new[] { (0, tagName), (8, propertyType), (24, enumType), (33, value) })
        {
            var hits = Enumerable.Range(0, nameMap.Count).Where(i => nameMap[i] == text).ToList();
            if (hits.Count != 1) return (null, $"\"{text}\" occurs {hits.Count} times in the name map (needs exactly 1)");
            BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(at), hits[0]);
        }
        BinaryPrimitives.WriteInt32LittleEndian(tag.AsSpan(16), 8);
        return (tag, null);
    }

    /// <summary>The anchor: Build on a stock package's name map gives exactly the bytes of its stored tag.</summary>
    public static string? CheckAgainst(GameSource source, string packagePath, string exportName, string tagName, string propertyType, string enumType, string value)
    {
        var (fileKey, bytes) = source.ReadPackageBytes(packagePath);
        var parsed = source.ParsePackage(fileKey, bytes);
        var layout = ZenPackageLayout.Read(bytes, parsed);
        var index = parsed.ExportsLazy.Select(e => e.Value.Name).ToList().IndexOf(exportName);
        if (index < 0) return $"{packagePath} has no export {exportName}";
        var tags = new TaggedPropertyWalker(layout.NameMap).TopLevelTags(bytes, layout.SpanOf(index)).Where(t => t.Name == tagName).ToList();
        if (tags.Count != 1 || tags[0].NameValue != value) return $"{exportName}.{tagName} isn't stored once as {value}";
        var (tag, why) = Build(layout.NameMap, tagName, propertyType, enumType, value);
        if (tag == null) return why;
        return bytes.AsSpan(tags[0].TagStart, tags[0].End - tags[0].TagStart).SequenceEqual(tag) ? null
            : $"the built tag isn't the stored one: {Convert.ToHexString(tag)} vs {Convert.ToHexString(bytes.AsSpan(tags[0].TagStart, tags[0].End - tags[0].TagStart))}";
    }

    public static Result Insert(GameSource source, string fileKey, byte[] chunk, string exportName, string afterTag, string tagName, string propertyType, string enumType, string value)
    {
        var log = new List<string>();
        Result Refuse(string why) => new(null, [why], log);

        var parsed = source.ParsePackage(fileKey, chunk);
        var exports = parsed.ExportsLazy.Select(e => e.Value).ToList();
        if (exports.Count(e => e.Name == exportName) != 1) return Refuse($"{exports.Count(e => e.Name == exportName)} exports named {exportName}");
        var exportIndex = exports.FindIndex(e => e.Name == exportName);
        var layout = ZenPackageLayout.Read(chunk, parsed);
        var span = layout.SpanOf(exportIndex);
        var walker = new TaggedPropertyWalker(layout.NameMap);
        List<TagLocation> tags;
        try { tags = walker.TopLevelTags(chunk, span); }
        catch (InvalidDataException e) { return Refuse($"{exportName}: the tags can't be walked ({e.Message})"); }
        if (tags.Any(t => t.Name == tagName)) return Refuse($"{exportName} already stores {tagName}");
        var anchor = tags.Where(t => t.Name == afterTag).ToList();
        if (anchor.Count != 1) return Refuse($"{exportName}.{afterTag} occurs {anchor.Count} times among the top-level tags (needs exactly 1)");
        var (tag, why) = Build(layout.NameMap, tagName, propertyType, enumType, value);
        if (tag == null) return Refuse(why!);
        var at = anchor[0].End;
        log.Add($"{exportName}.{tagName} = {value}: {TagLength}-byte {propertyType} tag inserted @0x{at:X} (after {afterTag}) in export {exportIndex}: {Convert.ToHexString(tag)}");

        var header = new List<(int Offset, ulong Old, ulong New, string What)>();
        var offsets = layout.CookedSerialOffsets;
        for (int i = 0; i < offsets.Count; i++)
        {
            var entry = layout.ExportMapOffset + i * ZenPackageLayout.ExportMapEntrySize;
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(chunk.AsSpan(entry));
            var size = BinaryPrimitives.ReadUInt64LittleEndian(chunk.AsSpan(entry + 8));
            if (offset != offsets[i]) return Refuse($"export map entry {i} holds CookedSerialOffset {offset}, the parse {offsets[i]}");
            if (i == exportIndex)
            {
                if (size != (ulong) span.Size) return Refuse($"CookedSerialSize {size} isn't the export's size {span.Size}");
                header.Add((entry + 8, size, size + TagLength, $"export {i} CookedSerialSize"));
            }
            else if (offset > offsets[exportIndex]) header.Add((entry, offset, offset + TagLength, $"export {i} CookedSerialOffset"));
        }
        var withHeader = chunk.ToArray();
        foreach (var (o, _, now, _) in header) BinaryPrimitives.WriteUInt64LittleEndian(withHeader.AsSpan(o), now);
        var result = new byte[chunk.Length + TagLength];
        withHeader.AsSpan(0, at).CopyTo(result);
        tag.CopyTo(result, at);
        withHeader.AsSpan(at).CopyTo(result.AsSpan(at + TagLength));
        log.Add($"header: 1 CookedSerialSize, {header.Count - 1} later CookedSerialOffsets (+{TagLength}); chunk {chunk.Length} → {result.Length}");

        // check 1: cut the tag out and put the header values back → the input again
        var back = new byte[chunk.Length];
        result.AsSpan(0, at).CopyTo(back);
        result.AsSpan(at + TagLength).CopyTo(back.AsSpan(at));
        foreach (var (o, old, _, _) in header) BinaryPrimitives.WriteUInt64LittleEndian(back.AsSpan(o), old);
        if (!back.AsSpan().SequenceEqual(chunk)) return Refuse("with the tag cut out and the header values put back, the result isn't the input");

        // check 2: the re-parse differs only by the new property, holding the value
        bool seen = false;
        var (after, problems, _) = TagInserter.ReparseDiff(source, fileKey, parsed, result, null, (export, key, old, now) =>
        {
            var ok = export == exportIndex && key == "Properties." + tagName && old == null && now?.Trim('"') == value;
            seen |= ok;
            return ok;
        });
        if (problems.Count > 0) return new Result(null, problems, log);
        if (!seen) return Refuse($"the re-parse doesn't show {exportName}.{tagName} = {value}");

        // check 3: the walk shows exactly one more tag, the new one, right after the anchor
        var newLayout = ZenPackageLayout.Read(result, after!);
        var newTags = new TaggedPropertyWalker(newLayout.NameMap).TopLevelTags(result, newLayout.SpanOf(exportIndex));
        var k = newTags.FindIndex(t => t.Name == tagName);
        if (newTags.Count != tags.Count + 1 || k < 1 || newTags[k - 1].Name != afterTag || newTags[k].NameValue != value) return Refuse("the walk of the edited export doesn't show exactly the one new tag after the anchor");
        log.Add($"re-parse: only {exportName}.{tagName} is new ({value}); the walk of export {exportIndex} ends at its None tag");
        return new Result(result, [], log);
    }

    /// <summary>
    /// The verified name append: <see cref="ZenNameMap.Append"/>, then the bytes before the name map's end and after the
    /// shifted header are the input's (only the 7 header fields differ), the name map reads as the old one plus the new
    /// names with every old hash kept, and the re-parse of every export is identical.
    /// </summary>
    public static Result AppendNames(GameSource source, string fileKey, byte[] chunk, IReadOnlyList<string> newNames)
    {
        var log = new List<string>();
        Result Refuse(string why) => new(null, [why], log);
        var (result, shift, why) = ZenNameMap.Append(chunk, newNames);
        if (result == null) return Refuse(why!);
        var oldNames = ZenNameMap.Read(chunk);
        var newMap = ZenNameMap.Read(result);
        if (newMap.Count != oldNames.Count + newNames.Count || !newMap.Take(oldNames.Count).Select(n => (n.Text, n.Hash)).SequenceEqual(oldNames.Select(n => (n.Text, n.Hash)))
            || !newMap.Skip(oldNames.Count).Select(n => n.Text).SequenceEqual(newNames))
            return Refuse("the new name map isn't the old one plus the new names");
        int namesEnd = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(24)) + BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(28));
        int importAt = BinaryPrimitives.ReadInt32LittleEndian(chunk.AsSpan(40));
        int[] fields = [28, 32, 36, 40, 44, 48, 52];
        for (int i = 0; i < namesEnd; i++)
            if (chunk[i] != result[i] && !fields.Any(f => i >= f && i < f + 4)) return Refuse($"byte 0x{i:X} before the name map's end changed");
        if (!chunk.AsSpan(importAt).SequenceEqual(result.AsSpan(importAt + shift))) return Refuse("the header after the name map didn't move as one piece");
        var parsed = source.ParsePackage(fileKey, chunk);
        var (_, problems, _) = TagInserter.ReparseDiff(source, fileKey, parsed, result, null, (_, _, _, _) => false);
        if (problems.Count > 0) return new Result(null, problems, log);
        log.Add($"name map {oldNames.Count} → {newMap.Count} names (+{string.Join(", ", newNames.Select(n => $"\"{n}\" {ZenNameMap.Hash(n, false):x16}"))}); header after it +{shift} bytes; all {oldNames.Count} stored hashes reproduced; every export re-parses identically");
        return new Result(result, [], log);
    }
}
