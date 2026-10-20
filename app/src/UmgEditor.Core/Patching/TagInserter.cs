using System.Buffers.Binary;
using CUE4Parse.UE4.Assets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UmgEditor.Core.Editing;

namespace UmgEditor.Core.Patching;

/// <summary>
/// One FloatProperty tag to add, planned on the stock chunk: every offset is a stock offset. The tag goes at InsertAt
/// (before the struct's first later member, or before its None tag); SizeFields are the Size of every enclosing struct tag.
/// </summary>
/// <param name="Rank">The field's place in the struct's declaration order (orders two insertions at the same point)</param>
/// <param name="Before">The tag it goes in front of ("Right", "None")</param>
public sealed record TagInsertion(int ExportIndex, string Path, float Value, int InsertAt, int Rank, string Before,
    IReadOnlyList<int> SizeFields, byte[] TagBytes, byte[] ContextBefore, byte[] ContextAfter, int ContextMatches)
{
    public FieldKey? Key { get; init; }
    public string SlotName { get; init; } = $"export {ExportIndex}";
    public string InsertAtHex => $"0x{InsertAt:X}";
    public string TagHex => PatchEntry.Hex(TagBytes);
}

public enum DerivedKind { StructSize, ExportSize, ExportOffset }

/// <summary>A header value the insertions change (stock offset): a struct tag's Size, or an export map CookedSerialSize / CookedSerialOffset.</summary>
public sealed record DerivedWrite(int Offset, byte[] OldBytes, byte[] NewBytes, DerivedKind Kind, int ExportIndex, long Old, long New)
{
    public IEnumerable<int> ChangedOffsets => Enumerable.Range(0, OldBytes.Length).Where(i => OldBytes[i] != NewBytes[i]).Select(i => Offset + i);
}

/// <summary>
/// Adds FloatProperty tags that aren't stored (the value has its class default) to tagged structs of a raw Zen chunk:
/// each 4.26 tag (Name, Type, Size 4, ArrayIndex 0, HasPropertyGuid 0, value = 29 bytes) goes in declaration order inside
/// its struct, the enclosing struct tags' Size grow by 29, the export's CookedSerialSize grows by 29 and every export
/// stored after it moves its CookedSerialOffset by 29. Nothing else in the chunk changes. The container header is
/// unaffected (eFootball store entries carry no sizes). Proven on the device by canary C10.
/// Everything is planned in stock offsets (<see cref="PlanFloat"/>, <see cref="Derive"/>) and applied in one splice
/// (<see cref="Apply"/>), so in-place value patches planned on the same stock chunk can be written first.
/// Only a re-parse proves the result: see <see cref="Verify"/> and <see cref="ReparseDiff"/>.
/// </summary>
public sealed class TagInserter
{
    public const int FloatTagSize = 29;
    /// <summary>FMargin's members in declaration order.</summary>
    public static readonly IReadOnlyList<string> MarginOrder = ["Left", "Top", "Right", "Bottom"];

    private readonly GameSource _source;
    private readonly IoPackage _stock;
    private readonly byte[] _stockBytes;
    private readonly ZenPackageLayout _layout;
    private readonly List<TagInsertion> _planned = [];

    public string PackagePath { get; }
    public string FileKey { get; }
    public byte[] Bytes { get; private set; }
    public List<string> Log { get; } = [];
    public List<string> Refusals { get; } = [];

    public TagInserter(GameSource source, string packagePath)
    {
        _source = source;
        PackagePath = packagePath;
        (FileKey, _stockBytes) = source.ReadPackageBytes(packagePath);
        Bytes = (byte[]) _stockBytes.Clone();
        _stock = source.ParsePackage(FileKey, _stockBytes);
        _layout = ZenPackageLayout.Read(_stockBytes, _stock);
    }

    /// <summary>The struct and member a slot path can be inserted as (an FMargin member of LayoutData.Offsets), or null.</summary>
    public static (string StructPath, string Field, IReadOnlyList<string> Order)? Insertable(string path) =>
        path.StartsWith("LayoutData.Offsets.", StringComparison.Ordinal) && MarginOrder.Contains(path["LayoutData.Offsets.".Length..])
            ? ("LayoutData.Offsets", path["LayoutData.Offsets.".Length..], MarginOrder) : null;

    /// <param name="structPath">Tagged struct chain inside the export, e.g. "LayoutData.Offsets"</param>
    /// <param name="declarationOrder">The struct's members in declaration order (FMargin: Left, Top, Right, Bottom)</param>
    public bool InsertFloat(int exportIndex, string structPath, string field, float value, IReadOnlyList<string> declarationOrder)
    {
        var (ins, why) = PlanFloat(_stockBytes, _layout, exportIndex, structPath, field, value, declarationOrder);
        if (ins == null) return Refuse(why!);
        if (ins.ContextMatches != 1) return Refuse($"export {exportIndex} {ins.Path}: the ±{BytePlanner.Context}-byte stock context at the insertion point matches {ins.ContextMatches} times (must be exactly 1)");
        if (_planned.Any(p => p.ExportIndex == exportIndex && p.Path == ins.Path)) return Refuse($"export {exportIndex} {ins.Path}: inserted twice");
        var derived = Derive(_stockBytes, _layout, [.. _planned, ins], out var problems);
        if (problems.Count > 0) { Refusals.AddRange(problems); return false; }
        _planned.Add(ins);
        Bytes = Apply(_stockBytes, _planned, derived);
        foreach (var d in derived.Where(d => d.Kind != DerivedKind.ExportOffset))
            Log.Add(d.Kind == DerivedKind.StructSize ? $"struct tag Size @0x{d.Offset:x}: {d.Old} → {d.New}" : $"export {d.ExportIndex} CookedSerialSize {d.Old} → {d.New}");
        Log.Add($"{derived.Count(d => d.Kind == DerivedKind.ExportOffset)} later exports' CookedSerialOffset moved");
        Log.Add($"inserted {ins.Path} = {value} (29-byte tag) at 0x{ins.InsertAt:x} in export {exportIndex} (before {ins.Before}); chunk {_stockBytes.Length} → {Bytes.Length}");
        return true;
    }

    // ---- planning (stock offsets) ----

    /// <summary>
    /// Where a missing float member of a stored tagged struct goes, or why it can't be added: the struct chain must be
    /// stored, the field must not be, and its name and "FloatProperty" must already be in the package's name map.
    /// </summary>
    public static (TagInsertion? Insertion, string? Refusal) PlanFloat(ReadOnlySpan<byte> stock, ZenPackageLayout layout, int exportIndex,
        string structPath, string field, float value, IReadOnlyList<string> declarationOrder)
    {
        var what = $"export {exportIndex} {structPath}.{field}";
        if (!declarationOrder.Contains(field)) return (null, $"{what}: {field} isn't in the declaration order list");
        var nameIndex = layout.NameIndex(field);
        var typeIndex = layout.NameIndex("FloatProperty");
        if (nameIndex < 0 || typeIndex < 0) return (null, $"{what}: \"{field}\" or \"FloatProperty\" is not in the package's name map (adding names is not supported)");

        ExportSpan span;
        try { span = layout.SpanOf(exportIndex); }
        catch (KeyNotFoundException e) { return (null, $"{what}: {e.Message}"); }
        var sizeFields = new List<int>();
        int start = span.Start, end = span.End;
        foreach (var member in structPath.Split('.'))
        {
            var tag = Tags(stock, layout, start, end)?.FirstOrDefault(t => t.Name == member);
            if (tag is not { Name: not null } t) return (null, $"{what}: struct {member} is not stored in the export (inserting a whole struct isn't supported)");
            if (t.Type != "StructProperty") return (null, $"{what}: {member} is a {t.Type}, not a struct");
            sizeFields.Add(t.SizeAt);
            (start, end) = (t.ValueStart, t.ValueStart + t.Size);
        }

        // inside the innermost struct: refuse if stored, else insert before the first later member (or before None)
        var tags = Tags(stock, layout, start, end);
        if (tags == null) return (null, $"{what}: the struct's tags don't parse up to its declared size");
        if (tags.Any(t => t.Name == field)) return (null, $"{what}: already stored (patch the value instead)");
        var order = declarationOrder.ToList();
        var rank = order.IndexOf(field);
        var before = tags.FirstOrDefault(t => order.Contains(t.Name) && order.IndexOf(t.Name) > rank);
        var insertAt = before.Name != null ? before.TagStart : tags.Count == 0 ? start : tags[^1].ValueStart + tags[^1].Size;
        if (before.Name == null && NameAt(stock, layout, insertAt) != "None")
            return (null, $"{what}: expected the struct's None tag at 0x{insertAt:x}");

        var tagBytes = new byte[FloatTagSize];
        BinaryPrimitives.WriteInt32LittleEndian(tagBytes.AsSpan(0), nameIndex);
        BinaryPrimitives.WriteInt32LittleEndian(tagBytes.AsSpan(8), typeIndex);
        BinaryPrimitives.WriteInt32LittleEndian(tagBytes.AsSpan(16), 4);
        BinaryPrimitives.WriteSingleLittleEndian(tagBytes.AsSpan(25), value); // ArrayIndex @20 = 0, HasPropertyGuid @24 = 0

        int from = Math.Max(0, insertAt - BytePlanner.Context), to = Math.Min(stock.Length, insertAt + BytePlanner.Context);
        var matches = from == insertAt - BytePlanner.Context && to == insertAt + BytePlanner.Context ? BytePlanner.CountOccurrences(stock, stock[from..to]) : 0;
        return (new TagInsertion(exportIndex, structPath + "." + field, value, insertAt, rank, before.Name ?? "None", sizeFields, tagBytes,
            stock[from..insertAt].ToArray(), stock[insertAt..to].ToArray(), matches), null);
    }

    /// <summary>
    /// The header values the insertions change, in stock offsets: each enclosing struct tag's Size (+29 per tag inside it),
    /// each edited export's CookedSerialSize (+29 per tag) and every CookedSerialOffset after an edited export (+29 per tag
    /// before it). Problems: an export map entry that doesn't hold CUE4Parse's values.
    /// </summary>
    public static List<DerivedWrite> Derive(ReadOnlySpan<byte> stock, ZenPackageLayout layout, IReadOnlyList<TagInsertion> insertions, out List<string> problems)
    {
        problems = [];
        var writes = new List<DerivedWrite>();
        if (insertions.Count == 0) return writes;
        foreach (var g in insertions.SelectMany(i => i.SizeFields).GroupBy(at => at).OrderBy(g => g.Key))
        {
            var old = BinaryPrimitives.ReadInt32LittleEndian(stock[g.Key..]);
            var now = old + FloatTagSize * g.Count();
            var bytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, now);
            var export = layout.Exports.FirstOrDefault(s => g.Key >= s.Start && g.Key < s.End).ExportIndex;
            writes.Add(new DerivedWrite(g.Key, stock.Slice(g.Key, 4).ToArray(), bytes, DerivedKind.StructSize, export, old, now));
        }

        var offsets = layout.CookedSerialOffsets;
        for (int i = 0; i < offsets.Count; i++)
        {
            var entry = layout.ExportMapOffset + i * ZenPackageLayout.ExportMapEntrySize;
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(stock[entry..]);
            var size = BinaryPrimitives.ReadUInt64LittleEndian(stock[(entry + 8)..]);
            if (offset != offsets[i])
            {
                problems.Add($"export map entry {i} at 0x{entry:x} holds CookedSerialOffset {offset}, CUE4Parse read {offsets[i]}");
                continue;
            }
            var grow = (ulong) (FloatTagSize * insertions.Count(x => x.ExportIndex == i));
            var move = (ulong) (FloatTagSize * insertions.Count(x => offsets[x.ExportIndex] < offset));
            if (move > 0) writes.Add(U64(stock, entry, DerivedKind.ExportOffset, i, offset, offset + move));
            if (grow > 0) writes.Add(U64(stock, entry + 8, DerivedKind.ExportSize, i, size, size + grow));
        }
        if (insertions.Any(x => x.InsertAt <= layout.ExportMapOffset + offsets.Count * ZenPackageLayout.ExportMapEntrySize))
            problems.Add("an insertion point lies inside the package header");
        return writes;
    }

    private static DerivedWrite U64(ReadOnlySpan<byte> stock, int at, DerivedKind kind, int export, ulong old, ulong now)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, now);
        return new DerivedWrite(at, stock.Slice(at, 8).ToArray(), bytes, kind, export, (long) old, (long) now);
    }

    /// <summary>Insertions in the order their tags appear in the new chunk.</summary>
    public static IEnumerable<TagInsertion> InOrder(IEnumerable<TagInsertion> insertions) => insertions.OrderBy(i => i.InsertAt).ThenBy(i => i.Rank);

    /// <summary>
    /// The new chunk: the derived header writes on a copy of <paramref name="chunk"/> (stock, or stock with in-place value
    /// patches: same offsets), then every tag spliced in at its stock insertion point.
    /// </summary>
    public static byte[] Apply(ReadOnlySpan<byte> chunk, IReadOnlyList<TagInsertion> insertions, IReadOnlyList<DerivedWrite> derived)
    {
        var copy = chunk.ToArray();
        foreach (var d in derived) d.NewBytes.CopyTo(copy, d.Offset);
        var result = new byte[chunk.Length + FloatTagSize * insertions.Count];
        int src = 0, dst = 0;
        foreach (var ins in InOrder(insertions))
        {
            copy.AsSpan(src, ins.InsertAt - src).CopyTo(result.AsSpan(dst));
            dst += ins.InsertAt - src;
            src = ins.InsertAt;
            ins.TagBytes.CopyTo(result, dst);
            dst += FloatTagSize;
        }
        copy.AsSpan(src).CopyTo(result.AsSpan(dst));
        return result;
    }

    /// <summary>Where a stock byte ends up in the new chunk.</summary>
    public static int Shifted(IReadOnlyList<TagInsertion> insertions, int stockOffset) =>
        stockOffset + FloatTagSize * insertions.Count(i => i.InsertAt <= stockOffset);

    /// <summary>Where an inserted tag starts in the new chunk.</summary>
    public static int NewTagOffset(IReadOnlyList<TagInsertion> insertions, TagInsertion ins) =>
        ins.InsertAt + FloatTagSize * insertions.Count(i => i.InsertAt < ins.InsertAt || i.InsertAt == ins.InsertAt && i.Rank < ins.Rank);

    /// <summary>The new chunk with the inserted tags cut out again (stock length), or null with why when a tag isn't where it was planned.</summary>
    public static (byte[]? Chunk, string? Problem) RemoveInserted(ReadOnlySpan<byte> candidate, IReadOnlyList<TagInsertion> insertions)
    {
        var expected = candidate.Length - FloatTagSize * insertions.Count;
        if (expected < 0) return (null, "the chunk is shorter than its inserted tags");
        var result = new byte[expected];
        int src = 0, dst = 0;
        foreach (var ins in InOrder(insertions))
        {
            var at = NewTagOffset(insertions, ins);
            if (at + FloatTagSize > candidate.Length || !candidate.Slice(at, FloatTagSize).SequenceEqual(ins.TagBytes))
                return (null, $"the inserted {ins.Path} tag isn't at 0x{at:X} of the new chunk");
            candidate[src..at].CopyTo(result.AsSpan(dst));
            dst += at - src;
            src = at + FloatTagSize;
        }
        candidate[src..].CopyTo(result.AsSpan(dst));
        return (result, null);
    }

    // ---- tag walking (stock) ----

    private readonly record struct Tag(string Name, string Type, int TagStart, int SizeAt, int Size, int ValueStart);

    /// <summary>Tags from start up to the None tag (null when they don't parse within limit).</summary>
    private static List<Tag>? Tags(ReadOnlySpan<byte> c, ZenPackageLayout layout, int pos, int limit)
    {
        var tags = new List<Tag>();
        try
        {
            while (pos + 8 <= limit)
            {
                var tagStart = pos;
                var name = NameAt(c, layout, pos); pos += 8;
                if (name == "None") return tags;
                var type = NameAt(c, layout, pos); pos += 8;
                var sizeAt = pos;
                var size = BinaryPrimitives.ReadInt32LittleEndian(c[pos..]); pos += 8; // Size, ArrayIndex
                pos += type switch { "StructProperty" => 24, "BoolProperty" => 1, "ByteProperty" or "EnumProperty" or "ArrayProperty" or "SetProperty" => 8, "MapProperty" => 16, _ => 0 };
                if (c[pos++] != 0) pos += 16;
                tags.Add(new Tag(name, type, tagStart, sizeAt, size, pos));
                pos += size;
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidDataException) { }
        return null;
    }

    private static string NameAt(ReadOnlySpan<byte> c, ZenPackageLayout layout, int at)
    {
        var index = BinaryPrimitives.ReadInt32LittleEndian(c[at..]);
        var number = BinaryPrimitives.ReadInt32LittleEndian(c[(at + 4)..]);
        if (index < 0 || index >= layout.NameMap.Count) throw new InvalidDataException($"name index {index} at 0x{at:x}");
        return number == 0 ? layout.NameMap[index] : $"{layout.NameMap[index]}_{number - 1}";
    }

    private bool Refuse(string why) { Refusals.Add(why); return false; }

    // ---- verification ----

    /// <summary>
    /// Re-parses the new chunk: every export must parse, every export but the edited one must be identical to stock,
    /// and the edited one may differ only by the inserted field (present, with its value).
    /// </summary>
    public IReadOnlyList<string> Verify()
    {
        var problems = new List<string>(Refusals);
        if (_planned.Count == 0) { problems.Add("nothing inserted"); return problems; }
        if (Bytes.Length != _stockBytes.Length + FloatTagSize * _planned.Count) problems.Add($"size {Bytes.Length}, expected stock + {FloatTagSize * _planned.Count}");

        var inserted = _planned.ToDictionary(p => (p.ExportIndex, "Properties." + p.Path), p => p.Value);
        var (patched, diff, changedExports) = ReparseDiff(_source, FileKey, _stock, Bytes, null, (export, key, old, now) =>
            old == null && inserted.TryGetValue((export, key), out var v)
            && float.TryParse(now, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var got) && got == v);
        problems.AddRange(diff);
        if (patched == null) return problems;

        // the walker sees the new tag where the game will read it
        var layout = ZenPackageLayout.Read(Bytes, patched);
        foreach (var p in _planned)
        {
            var found = new TaggedPropertyWalker(layout.NameMap).Walk(Bytes, layout.SpanOf(p.ExportIndex)).FirstOrDefault(f => f.Path == p.Path);
            if (found == null || found.Value != p.Value) problems.Add($"walker: {p.Path} in export {p.ExportIndex} is {(found == null ? "missing" : found.Value.ToString())}");
            else Log.Add($"walker: {p.Path} = {found.Value} @0x{found.Offset:x}");
        }
        Log.Add($"re-parse: {patched.ExportMap.Length} exports parse, {changedExports} differ from stock, {problems.Count} problems");
        return problems;
    }

    /// <summary>
    /// Re-parses a changed chunk and compares every export with stock (as flattened JSON values). Every difference must
    /// be allowed by <paramref name="allowed"/> (export index, "Properties.…" key, stock text or null when absent, new
    /// text or null). Returns the parsed package (null when it doesn't parse), the problems and how many exports differ.
    /// </summary>
    /// <param name="stockFlat">Cached flattened stock exports (optional)</param>
    public static (IoPackage? Parsed, List<string> Problems, int ChangedExports) ReparseDiff(GameSource source, string fileKey, IoPackage stock, byte[] bytes,
        Func<int, Dictionary<string, string>>? stockFlat, Func<int, string, string?, string?, bool> allowed)
    {
        var problems = new List<string>();
        IoPackage patched;
        try { patched = source.ParsePackage(fileKey, bytes); }
        catch (Exception e) { problems.Add("the new chunk doesn't parse: " + e.Message); return (null, problems, 0); }
        if (patched.ExportMap.Length != stock.ExportMap.Length) problems.Add("export count changed");

        int changedExports = 0;
        for (int i = 0; i < Math.Min(stock.ExportsLazy.Length, patched.ExportsLazy.Length); i++)
        {
            Dictionary<string, string> a, b;
            try { a = stockFlat?.Invoke(i) ?? Flatten(stock.ExportsLazy[i].Value); b = Flatten(patched.ExportsLazy[i].Value); }
            catch (Exception e) { problems.Add($"export {i} doesn't parse: {e.Message}"); continue; }
            var diff = a.Keys.Union(b.Keys).Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k)).ToList();
            if (diff.Count == 0) continue;
            changedExports++;
            foreach (var k in diff.Where(k => !allowed(i, k, a.GetValueOrDefault(k), b.GetValueOrDefault(k))))
                problems.Add($"unintended difference in export {i} {k}: {a.GetValueOrDefault(k) ?? "(absent)"} → {b.GetValueOrDefault(k) ?? "(absent)"}");
        }
        return (patched, problems, changedExports);
    }

    public static Dictionary<string, string> Flatten(CUE4Parse.UE4.Assets.Exports.UObject export)
    {
        var map = new Dictionary<string, string>();
        void Walk(JToken t, string p)
        {
            switch (t)
            {
                case JObject j: foreach (var prop in j.Properties()) Walk(prop.Value, p.Length == 0 ? prop.Name : p + "." + prop.Name); break;
                case JArray arr: for (int i = 0; i < arr.Count; i++) Walk(arr[i], $"{p}[{i}]"); break;
                default: map[p] = t.ToString(Formatting.None); break;
            }
        }
        Walk(JObject.Parse(JsonConvert.SerializeObject(export)), "");
        return map;
    }
}
