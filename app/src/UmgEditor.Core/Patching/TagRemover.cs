using System.Buffers.Binary;

namespace UmgEditor.Core.Patching;

/// <summary>
/// Removes one top-level tagged property from an export, so the property falls back to its class default (branch
/// experiment, canary C15: Normal_Display's Clipping = ClipToBounds → the default Inherit, whose name isn't in the
/// package). The mirror of <see cref="TagInserter"/>: the export's CookedSerialSize shrinks by the tag's length and
/// every CookedSerialOffset after the export moves back by it; a top-level tag has no enclosing struct size, and the
/// container header carries no sizes. Checks: the tag is found exactly once by the walk and holds the expected value;
/// with the tag and the header values put back the result is the input again; the re-parse differs only in that
/// property (now absent); the walk of the edited export still ends at its None tag.
/// </summary>
public static class TagRemover
{
    public sealed record Result(byte[]? Chunk, IReadOnlyList<string> Refusals, IReadOnlyList<string> Log);

    /// <param name="chunk">The package bytes to edit (stock, or an already verified edit of it)</param>
    /// <param name="expectedValue">The FName the property must hold now (enum properties), e.g. "EWidgetClipping::ClipToBounds"</param>
    public static Result Remove(GameSource source, string fileKey, byte[] chunk, string exportName, string tagName, string expectedValue)
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
        var hits = tags.Where(t => t.Name == tagName).ToList();
        if (hits.Count != 1) return Refuse($"{exportName}.{tagName} occurs {hits.Count} times among the top-level tags (needs exactly 1)");
        var tag = hits[0];
        if (tag.NameValue != expectedValue) return Refuse($"{exportName}.{tagName} holds {tag.NameValue ?? "(not an enum value)"}, expected {expectedValue}");
        var length = tag.Length;
        log.Add($"{exportName}.{tagName} = {tag.NameValue}: {tag.Type} tag of {length} bytes @0x{tag.TagStart:X}..0x{tag.End:X} in export {exportIndex}");

        // header: this export's size, and the offsets of the exports stored after it
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
                header.Add((entry + 8, size, size - (ulong) length, $"export {i} CookedSerialSize"));
            }
            else if (offset > offsets[exportIndex]) header.Add((entry, offset, offset - (ulong) length, $"export {i} CookedSerialOffset"));
        }
        if (tag.TagStart <= layout.ExportMapOffset + offsets.Count * ZenPackageLayout.ExportMapEntrySize) return Refuse("the tag lies inside the package header");

        var withHeader = chunk.ToArray();
        foreach (var (at, _, now, _) in header) BinaryPrimitives.WriteUInt64LittleEndian(withHeader.AsSpan(at), now);
        var result = new byte[chunk.Length - length];
        withHeader.AsSpan(0, tag.TagStart).CopyTo(result);
        withHeader.AsSpan(tag.End).CopyTo(result.AsSpan(tag.TagStart));
        log.Add($"removed {length} bytes; header: {header.Count(h => h.What.EndsWith("Size"))} CookedSerialSize, {header.Count(h => h.What.EndsWith("Offset"))} later CookedSerialOffsets (−{length}); chunk {chunk.Length} → {result.Length}");

        // check 1: put the tag and the header values back → the input again
        var back = new byte[chunk.Length];
        result.AsSpan(0, tag.TagStart).CopyTo(back);
        chunk.AsSpan(tag.TagStart, length).CopyTo(back.AsSpan(tag.TagStart));
        result.AsSpan(tag.TagStart).CopyTo(back.AsSpan(tag.End));
        foreach (var (at, old, _, _) in header) BinaryPrimitives.WriteUInt64LittleEndian(back.AsSpan(at), old);
        if (!back.AsSpan().SequenceEqual(chunk)) return Refuse("with the tag and the header values put back, the result isn't the input");

        // check 2: the re-parse differs only in the removed property
        bool seen = false;
        var (after, problems, _) = TagInserter.ReparseDiff(source, fileKey, parsed, result, null, (export, key, old, now) =>
        {
            var ok = export == exportIndex && key == "Properties." + tagName && now == null && old != null;
            seen |= ok;
            return ok;
        });
        if (problems.Count > 0) return new Result(null, problems, log);
        if (!seen) return Refuse($"the re-parse doesn't show {exportName}.{tagName} gone");

        // check 3: the walk of the edited export still ends at its None tag, without the property
        var newLayout = ZenPackageLayout.Read(result, after!);
        var newTags = new TaggedPropertyWalker(newLayout.NameMap).TopLevelTags(result, newLayout.SpanOf(exportIndex));
        if (newTags.Any(t => t.Name == tagName) || newTags.Count != tags.Count - 1) return Refuse("the walk of the edited export doesn't show exactly the one tag gone");
        log.Add($"re-parse: only {exportName}.{tagName} is gone (now the class default); the walk of export {exportIndex} ends at its None tag");
        return new Result(result, [], log);
    }
}

/// <summary>
/// The same-size write that changes an enum property's value (an FName: name index + number) to another name that's
/// already in the name map (branch experiment, C15: the scores' colour NewEnumerator87 → 86). Located by the walk of the
/// export's top-level tags; the old value must be the expected one. Apply through NameRenamePatch (writes + expected).
/// </summary>
public static class EnumValueSwap
{
    public static (int Offset, byte[] Bytes, string? Why) Plan(ReadOnlySpan<byte> chunk, ZenPackageLayout layout, int exportIndex, string tagName, string from, string to)
    {
        var tags = new TaggedPropertyWalker(layout.NameMap).TopLevelTags(chunk, layout.SpanOf(exportIndex));
        var hits = tags.Where(t => t.Name == tagName).ToList();
        if (hits.Count != 1) return (0, [], $"{tagName} occurs {hits.Count} times among the top-level tags of export {exportIndex}");
        if (hits[0].NameValue != from) return (0, [], $"{tagName} holds {hits[0].NameValue ?? "(not an enum value)"}, not {from}");
        var index = layout.NameIndex(to);
        if (index < 0) return (0, [], $"\"{to}\" isn't in the name map (a swap can only use an existing name)");
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, index); // number 0
        return (hits[0].ValueStart, bytes, null);
    }
}
