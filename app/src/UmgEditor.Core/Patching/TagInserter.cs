using System.Buffers.Binary;
using CUE4Parse.UE4.Assets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UmgEditor.Core.Patching;

/// <summary>
/// Adds a FloatProperty that isn't stored (it has its class default) to a tagged struct inside one export of a raw Zen
/// chunk: the 4.26 tag (Name, Type, Size 4, ArrayIndex 0, HasPropertyGuid 0, value = 29 bytes) goes in declaration
/// order inside the struct, the enclosing struct tags' Size grow by 29, the export's CookedSerialSize grows by 29 and
/// every export stored after it moves its CookedSerialOffset by 29. Nothing else in the chunk changes. The container
/// header is unaffected (eFootball store entries carry no sizes). Only a re-parse proves the result: see Verify.
/// </summary>
public sealed class TagInserter
{
    public const int FloatTagSize = 29;
    private const int ExportMapEntrySize = 72;

    private readonly GameSource _source;
    private readonly IoPackage _stock;
    private readonly byte[] _stockBytes;
    private readonly ZenPackageLayout _layout;
    private readonly List<(int Export, string Path, float Value)> _inserted = [];

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

    /// <param name="structPath">Tagged struct chain inside the export, e.g. "LayoutData.Offsets"</param>
    /// <param name="declarationOrder">The struct's members in declaration order (FMargin: Left, Top, Right, Bottom)</param>
    public bool InsertFloat(int exportIndex, string structPath, string field, float value, IReadOnlyList<string> declarationOrder)
    {
        var what = $"export {exportIndex} {structPath}.{field}";
        if (_inserted.Count > 0) return Refuse($"{what}: one insertion per package for now (offsets of a second one would need re-walking)");
        if (!declarationOrder.Contains(field)) return Refuse($"{what}: {field} isn't in the declaration order list");
        var nameIndex = NameIndex(field);
        var typeIndex = NameIndex("FloatProperty");
        if (nameIndex < 0 || typeIndex < 0) return Refuse($"{what}: \"{field}\" or \"FloatProperty\" is not in the package's name map (adding names is not supported)");

        var span = _layout.SpanOf(exportIndex);
        var levels = new List<(int SizeAt, int ValueStart, int ValueEnd)>();
        int start = span.Start, end = span.End;
        foreach (var member in structPath.Split('.'))
        {
            var tag = FindTag(start, end, member);
            if (tag == null) return Refuse($"{what}: struct {member} is not stored in the export (inserting a whole struct isn't supported)");
            if (tag.Value.Type != "StructProperty") return Refuse($"{what}: {member} is a {tag.Value.Type}, not a struct");
            levels.Add((tag.Value.SizeAt, tag.Value.ValueStart, tag.Value.ValueStart + tag.Value.Size));
            (start, end) = (tag.Value.ValueStart, tag.Value.ValueStart + tag.Value.Size);
        }

        // inside the innermost struct: refuse if stored, else insert before the first later member (or before None)
        var tags = Tags(start, end);
        if (tags == null) return Refuse($"{what}: the struct's tags don't parse up to its declared size");
        if (tags.Any(t => t.Name == field)) return Refuse($"{what}: already stored (patch the value instead)");
        var rank = declarationOrder.ToList().IndexOf(field);
        var before = tags.FirstOrDefault(t => declarationOrder.Contains(t.Name) && declarationOrder.ToList().IndexOf(t.Name) > rank);
        var insertAt = before.Name != null ? before.TagStart : tags.Count == 0 ? start : tags[^1].ValueStart + tags[^1].Size;
        if (before.Name == null && NameAt(insertAt) != "None")
            return Refuse($"{what}: expected the struct's None tag at 0x{insertAt:x}");

        var tagBytes = new byte[FloatTagSize];
        BinaryPrimitives.WriteInt32LittleEndian(tagBytes.AsSpan(0), nameIndex);
        BinaryPrimitives.WriteInt32LittleEndian(tagBytes.AsSpan(8), typeIndex);
        BinaryPrimitives.WriteInt32LittleEndian(tagBytes.AsSpan(16), 4);
        BinaryPrimitives.WriteSingleLittleEndian(tagBytes.AsSpan(25), value); // ArrayIndex @20 = 0, HasPropertyGuid @24 = 0

        var result = new byte[_stockBytes.Length + FloatTagSize];
        _stockBytes.AsSpan(0, insertAt).CopyTo(result);
        tagBytes.CopyTo(result, insertAt);
        _stockBytes.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + FloatTagSize));

        foreach (var (sizeAt, _, _) in levels) // all before insertAt, so their positions are unchanged
        {
            var old = BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(sizeAt));
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(sizeAt), old + FloatTagSize);
            Log.Add($"struct tag Size @0x{sizeAt:x}: {old} → {old + FloatTagSize}");
        }

        int exportMapOffset = BinaryPrimitives.ReadInt32LittleEndian(_stockBytes.AsSpan(44));
        var ownOffset = _stock.ExportMap[exportIndex].CookedSerialOffset;
        for (int i = 0; i < _stock.ExportMap.Length; i++)
        {
            var entry = exportMapOffset + i * ExportMapEntrySize;
            var offset = BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(entry));
            var size = BinaryPrimitives.ReadUInt64LittleEndian(result.AsSpan(entry + 8));
            if (offset != _stock.ExportMap[i].CookedSerialOffset || size != _stock.ExportMap[i].CookedSerialSize)
                return Refuse($"export map entry {i} at 0x{entry:x} doesn't match CUE4Parse's ({offset}/{size})");
            if (i == exportIndex)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(entry + 8), size + FloatTagSize);
                Log.Add($"export {i} CookedSerialSize {size} → {size + FloatTagSize}");
            }
            else if (offset > ownOffset)
                BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(entry), offset + FloatTagSize);
        }
        Log.Add($"{_stock.ExportMap.Count(e => e.CookedSerialOffset > ownOffset)} later exports' CookedSerialOffset +{FloatTagSize}");
        Log.Add($"inserted {structPath}.{field} = {value} (29-byte tag) at 0x{insertAt:x} in export {exportIndex} ({(before.Name != null ? "before " + before.Name : "before None")}); chunk {_stockBytes.Length} → {result.Length}");
        Bytes = result;
        _inserted.Add((exportIndex, structPath + "." + field, value));
        return true;
    }

    private readonly record struct Tag(string Name, string Type, int TagStart, int SizeAt, int Size, int ValueStart);

    private Tag? FindTag(int start, int end, string name) => Tags(start, end)?.FirstOrDefault(t => t.Name == name) is { Name: not null } t ? t : null;

    /// <summary>Tags from start up to the None tag (null when they don't end exactly at end - 8 + 8).</summary>
    private List<Tag>? Tags(int pos, int limit)
    {
        var tags = new List<Tag>();
        try
        {
            while (pos + 8 <= limit)
            {
                var tagStart = pos;
                var name = NameAt(pos); pos += 8;
                if (name == "None") return tags;
                var type = NameAt(pos); pos += 8;
                var sizeAt = pos;
                var size = BinaryPrimitives.ReadInt32LittleEndian(_stockBytes.AsSpan(pos)); pos += 8; // Size, ArrayIndex
                pos += type switch { "StructProperty" => 24, "BoolProperty" => 1, "ByteProperty" or "EnumProperty" or "ArrayProperty" or "SetProperty" => 8, "MapProperty" => 16, _ => 0 };
                if (_stockBytes[pos++] != 0) pos += 16;
                tags.Add(new Tag(name, type, tagStart, sizeAt, size, pos));
                pos += size;
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidDataException) { }
        return null;
    }

    private string NameAt(int at)
    {
        var index = BinaryPrimitives.ReadInt32LittleEndian(_stockBytes.AsSpan(at));
        var number = BinaryPrimitives.ReadInt32LittleEndian(_stockBytes.AsSpan(at + 4));
        if (index < 0 || index >= _layout.NameMap.Count) throw new InvalidDataException($"name index {index} at 0x{at:x}");
        return number == 0 ? _layout.NameMap[index] : $"{_layout.NameMap[index]}_{number - 1}";
    }

    private int NameIndex(string name)
    {
        for (int i = 0; i < _layout.NameMap.Count; i++) if (_layout.NameMap[i] == name) return i;
        return -1;
    }

    private bool Refuse(string why) { Refusals.Add(why); return false; }

    /// <summary>
    /// Re-parses the new chunk: every export must parse, every export but the edited one must be identical to stock,
    /// and the edited one may differ only by the inserted field (present, with its value).
    /// </summary>
    public IReadOnlyList<string> Verify()
    {
        var problems = new List<string>(Refusals);
        if (_inserted.Count == 0) { problems.Add("nothing inserted"); return problems; }
        if (Bytes.Length != _stockBytes.Length + FloatTagSize * _inserted.Count) problems.Add($"size {Bytes.Length}, expected stock + {FloatTagSize * _inserted.Count}");

        IoPackage patched;
        try { patched = _source.ParsePackage(FileKey, Bytes); }
        catch (Exception e) { problems.Add("new chunk doesn't parse: " + e.Message); return problems; }
        if (patched.ExportMap.Length != _stock.ExportMap.Length) problems.Add("export count changed");

        int changedExports = 0;
        for (int i = 0; i < _stock.ExportsLazy.Length; i++)
        {
            Dictionary<string, string> a, b;
            try { a = Flatten(_stock.ExportsLazy[i].Value); b = Flatten(patched.ExportsLazy[i].Value); }
            catch (Exception e) { problems.Add($"export {i} doesn't parse after the insertion: {e.Message}"); continue; }
            var diff = a.Keys.Union(b.Keys).Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k)).ToList();
            if (diff.Count == 0) continue;
            changedExports++;
            var mine = _inserted.Where(x => x.Export == i).ToList();
            foreach (var k in diff)
            {
                var ok = mine.Any(x => k == "Properties." + x.Path && a.GetValueOrDefault(k) == null
                    && float.TryParse(b[k], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v == x.Value);
                if (!ok) problems.Add($"unintended difference in export {i} {k}: {a.GetValueOrDefault(k) ?? "(absent)"} → {b.GetValueOrDefault(k) ?? "(absent)"}");
            }
        }
        // the walker sees the new tag where the game will read it
        var layout = ZenPackageLayout.Read(Bytes, patched);
        foreach (var (export, path, value) in _inserted)
        {
            var found = new TaggedPropertyWalker(layout.NameMap).Walk(Bytes, layout.SpanOf(export)).FirstOrDefault(f => f.Path == path);
            if (found == null || found.Value != value) problems.Add($"walker: {path} in export {export} is {(found == null ? "missing" : found.Value.ToString())}");
            else Log.Add($"walker: {path} = {found.Value} @0x{found.Offset:x}");
        }
        Log.Add($"re-parse: {patched.ExportMap.Length} exports parse, {changedExports} differ from stock, {problems.Count} problems");
        return problems;
    }

    private static Dictionary<string, string> Flatten(CUE4Parse.UE4.Assets.Exports.UObject export)
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
