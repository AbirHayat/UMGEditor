using System.Buffers.Binary;
using UmgEditor.Core.Editing;

namespace UmgEditor.Core.Patching;

public enum RefusalRule
{
    /// <summary>The edit belongs to another package than the one being saved.</summary>
    OtherPackage,
    /// <summary>The field isn't found exactly once among the stored values of its slot export.</summary>
    NotLocated,
    /// <summary>Only float and int32 values are patched in this version.</summary>
    UnsupportedType,
    /// <summary>The new value can't be written exactly in the field's type.</summary>
    NotRepresentable,
    /// <summary>The stock bytes at the offset don't encode the stock value CUE4Parse read.</summary>
    StockBytesMismatch,
    /// <summary>The ±32-byte stock context isn't found exactly once in the chunk.</summary>
    ContextNotUnique,
    /// <summary>The stock copy in work\edits no longer hashes to the game file it was taken from.</summary>
    StockCopyChanged,
    /// <summary>The rebuilt file doesn't have the planned size (stock + 29 bytes per inserted tag).</summary>
    SizeChanged,
    /// <summary>A byte outside the plan differs from stock, or a planned byte didn't change.</summary>
    UnexpectedByteDiff,
    /// <summary>Reloading the written file gave different bytes or values than planned.</summary>
    ReloadMismatch,
    /// <summary>A value that isn't stored can't be added as a new tag (branch experiment: only a missing Offsets member of a stored Offsets struct).</summary>
    NotInsertable,
}

public sealed record PatchRefusal(RefusalRule Rule, string Message, FieldKey? Key = null);

/// <summary>
/// One value to write: where it is in the raw Zen chunk, what is there now, what goes there, and the ±32-byte stock
/// context that proves the location (the "bytes to patch" model data).
/// </summary>
public sealed record PatchEntry(
    FieldKey Key, string SlotName, int Offset, FieldType Type,
    double OldValue, double NewValue, byte[] OldBytes, byte[] NewBytes,
    byte[] ContextBefore, byte[] ContextAfter, int ContextMatches)
{
    public string OffsetHex => $"0x{Offset:X}";
    public string OldHex => Hex(OldBytes);
    public string NewHex => Hex(NewBytes);
    /// <summary>The full matched window: before · [value] · after.</summary>
    public string ContextHex => $"{Hex(ContextBefore)} [{OldHex}] {Hex(ContextAfter)}";

    /// <summary>Absolute offsets of the bytes this entry actually changes.</summary>
    public IEnumerable<int> ChangedOffsets => Enumerable.Range(0, OldBytes.Length).Where(i => OldBytes[i] != NewBytes[i]).Select(i => Offset + i);

    public static string Hex(byte[] bytes) => string.Join(" ", bytes.Select(b => b.ToString("X2")));
}

/// <summary>
/// All writes for one save, or the reasons it is refused. Entries (in-place values) are sorted by offset. Insertions
/// (values that aren't stored, added as new tags) and the header values they change (Derived) are in stock offsets too.
/// </summary>
public sealed record BytePlan(string PackagePath, int StockSize, IReadOnlyList<PatchEntry> Entries, IReadOnlyList<PatchRefusal> Refusals)
{
    public IReadOnlyList<TagInsertion> Insertions { get; init; } = [];
    public IReadOnlyList<DerivedWrite> Derived { get; init; } = [];

    public bool Ok => Refusals.Count == 0;
    public int ValueCount => Entries.Count + Insertions.Count;
    public bool ChangesSize => Insertions.Count > 0;
    /// <summary>The rebuilt chunk's size: stock + 29 bytes per inserted tag.</summary>
    public int EditedSize => StockSize + TagInserter.FloatTagSize * Insertions.Count;
    /// <summary>Stock offsets that change (values and derived header values), not counting the inserted tags.</summary>
    public IReadOnlyList<int> ExpectedChangedOffsets => Entries.SelectMany(e => e.ChangedOffsets).Concat(Derived.SelectMany(d => d.ChangedOffsets)).Order().ToList();

    /// <summary>"size 1269266 unchanged" or "size 1269266 → 1269295 (+29: 1 inserted tag)".</summary>
    public string SizeText => ChangesSize
        ? $"size {StockSize} → {EditedSize} (+{EditedSize - StockSize}: {Insertions.Count} inserted tag{(Insertions.Count == 1 ? "" : "s")})"
        : $"size {StockSize} unchanged";
}

/// <summary>
/// Turns an EditSet into in-place byte writes on the stock Zen chunk and checks the result. Rules (any failure refuses
/// the whole save): every field is located exactly once by the tagged-property walk of its slot export, the stock bytes
/// there encode the stock value CUE4Parse parsed, the ±32-byte stock context occurs exactly once in the chunk, and the
/// rebuilt chunk has the planned size and differs from stock in exactly the planned bytes.
/// Branch experiment: a value that isn't stored (no stock value, not walked) is added as a new tag when
/// <see cref="TagInserter.Insertable"/> allows it; its insertion point's ±32-byte stock context must be unique too, and
/// with the inserted tags cut out again the rebuilt chunk differs from stock only in the planned values and the struct
/// sizes and export map entries the tags change.
/// </summary>
public static class BytePlanner
{
    public const int Context = 32;

    /// <param name="stock">The stock chunk the plan is built on (always the untouched stock copy)</param>
    /// <param name="expectedStock">Stock value of a field from an independent parse (CUE4Parse), null when unknown</param>
    public static BytePlan Plan(ReadOnlySpan<byte> stock, ZenPackageLayout layout, string packagePath, EditSet edits,
        Func<FieldKey, double?> expectedStock, Func<int, string>? slotName = null)
    {
        var walker = new TaggedPropertyWalker(layout.NameMap);
        var walked = new Dictionary<int, List<FieldLocation>>();
        var entries = new List<PatchEntry>();
        var insertions = new List<TagInsertion>();
        var refusals = new List<PatchRefusal>();

        foreach (var (key, newValue) in edits.Values.OrderBy(kv => kv.Key.SlotExport).ThenBy(kv => kv.Key.Path, StringComparer.Ordinal))
        {
            void Refuse(RefusalRule rule, string message) => refusals.Add(new PatchRefusal(rule, $"{Describe(key, slotName)}: {message}", key));

            if (!key.Package.Equals(packagePath, StringComparison.OrdinalIgnoreCase)) { Refuse(RefusalRule.OtherPackage, $"belongs to {key.Package}, not {packagePath}"); continue; }
            if (!walked.TryGetValue(key.SlotExport, out var fields))
            {
                try { fields = walker.Walk(stock, layout.SpanOf(key.SlotExport)); }
                catch (Exception e) when (e is KeyNotFoundException or InvalidDataException or ArgumentOutOfRangeException) { Refuse(RefusalRule.NotLocated, $"slot export can't be walked ({e.Message})"); continue; }
                walked[key.SlotExport] = fields;
            }
            var hits = fields.Where(f => f.Path == key.Path).ToList();
            if (hits.Count == 0 && expectedStock(key) == null && TagInserter.Insertable(key.Path) is { } at)
            {
                if (Encode(FieldType.Float, newValue) == null) { Refuse(RefusalRule.NotRepresentable, $"{newValue} can't be stored exactly as Float"); continue; }
                var (ins, why) = TagInserter.PlanFloat(stock, layout, key.SlotExport, at.StructPath, at.Field, (float) newValue, at.Order);
                if (ins == null) Refuse(RefusalRule.NotInsertable, why!);
                else if (ins.ContextMatches != 1) Refuse(RefusalRule.ContextNotUnique, $"the ±{Context}-byte context at the insertion point 0x{ins.InsertAt:X} matches {ins.ContextMatches} times in the chunk (must be exactly 1)");
                else insertions.Add(ins with { Key = key, SlotName = slotName?.Invoke(key.SlotExport) ?? $"export {key.SlotExport}" });
                continue;
            }
            if (hits.Count != 1) { Refuse(RefusalRule.NotLocated, $"found {hits.Count} times among the stored values of the slot export (must be exactly 1)"); continue; }
            var loc = hits[0];

            if (loc.Type == FieldType.Bool) { Refuse(RefusalRule.UnsupportedType, "boolean fields aren't patched in this version"); continue; }
            if (Encode(loc.Type, newValue) is not { } newBytes) { Refuse(RefusalRule.NotRepresentable, $"{newValue} can't be stored exactly as {loc.Type}"); continue; }

            var oldBytes = stock.Slice(loc.Offset, loc.Length).ToArray();
            var expected = expectedStock(key);
            if (expected is not { } ev || Encode(loc.Type, ev) is not { } expectedBytes || !expectedBytes.AsSpan().SequenceEqual(oldBytes))
            {
                Refuse(RefusalRule.StockBytesMismatch, $"stock bytes at 0x{loc.Offset:X} are {PatchEntry.Hex(oldBytes)} ({Decode(loc.Type, oldBytes)}), expected the stock value {(expected?.ToString() ?? "unknown")}");
                continue;
            }

            int from = loc.Offset - Context, to = loc.Offset + loc.Length + Context;
            if (from < 0 || to > stock.Length) { Refuse(RefusalRule.ContextNotUnique, $"the ±{Context}-byte context at 0x{loc.Offset:X} runs past the chunk bounds (0..{stock.Length})"); continue; }
            var matches = CountOccurrences(stock, stock[from..to]);
            if (matches != 1) { Refuse(RefusalRule.ContextNotUnique, $"the ±{Context}-byte context at 0x{loc.Offset:X} matches {matches} times in the chunk (must be exactly 1)"); continue; }

            entries.Add(new PatchEntry(key, slotName?.Invoke(key.SlotExport) ?? $"export {key.SlotExport}", loc.Offset, loc.Type,
                Decode(loc.Type, oldBytes), newValue, oldBytes, newBytes,
                stock[from..loc.Offset].ToArray(), stock[(loc.Offset + loc.Length)..to].ToArray(), matches));
        }

        var sorted = entries.OrderBy(e => e.Offset).ToList();
        for (int i = 1; i < sorted.Count; i++)
            if (sorted[i].Offset < sorted[i - 1].Offset + sorted[i - 1].OldBytes.Length)
                refusals.Add(new PatchRefusal(RefusalRule.NotLocated, $"{Describe(sorted[i].Key, slotName)} overlaps {Describe(sorted[i - 1].Key, slotName)} at 0x{sorted[i].Offset:X}", sorted[i].Key));

        var derived = TagInserter.Derive(stock, layout, insertions, out var headerProblems);
        refusals.AddRange(headerProblems.Select(p => new PatchRefusal(RefusalRule.NotInsertable, $"{GamePaths.ShortName(packagePath)}: {p}")));
        foreach (var d in derived)
            if (sorted.FirstOrDefault(e => e.Offset < d.Offset + d.OldBytes.Length && d.Offset < e.Offset + e.OldBytes.Length) is { } clash)
                refusals.Add(new PatchRefusal(RefusalRule.NotLocated, $"{Describe(clash.Key, slotName)} overlaps a header value the inserted tags change at 0x{d.Offset:X}", clash.Key));
        return new BytePlan(packagePath, stock.Length, sorted, refusals) { Insertions = TagInserter.InOrder(insertions).ToList(), Derived = derived };
    }

    /// <summary>
    /// Stock bytes with the planned values written over them, then (when the plan inserts tags) the derived header values
    /// and the tags spliced in. A new array; stock is not touched.
    /// </summary>
    public static byte[] Apply(ReadOnlySpan<byte> stock, BytePlan plan)
    {
        if (!plan.Ok) throw new InvalidOperationException("can't apply a refused plan");
        var result = stock.ToArray();
        foreach (var e in plan.Entries) e.NewBytes.CopyTo(result, e.Offset);
        return plan.ChangesSize ? TagInserter.Apply(result, plan.Insertions, plan.Derived) : result;
    }

    /// <summary>
    /// Size and byte diff of a candidate against stock: it must have the planned size, hold every inserted tag where it
    /// was planned and, with those cut out, differ from stock in exactly the planned bytes.
    /// </summary>
    public static List<PatchRefusal> Verify(ReadOnlySpan<byte> stock, ReadOnlySpan<byte> candidate, BytePlan plan)
    {
        if (candidate.Length != plan.EditedSize)
            return [new PatchRefusal(RefusalRule.SizeChanged, $"the rebuilt file is {candidate.Length} bytes, planned {plan.EditedSize} (stock {stock.Length}" +
                                                              (plan.ChangesSize ? $" + {plan.EditedSize - stock.Length} for {plan.Insertions.Count} inserted tags)" : ")"))];
        if (!plan.ChangesSize) return VerifySameSize(stock, candidate, plan);

        var (cut, problem) = TagInserter.RemoveInserted(candidate, plan.Insertions);
        if (cut == null) return [new PatchRefusal(RefusalRule.UnexpectedByteDiff, problem!)];
        var refusals = VerifySameSize(stock, cut, plan);
        foreach (var d in plan.Derived)
            if (!cut.AsSpan(d.Offset, d.NewBytes.Length).SequenceEqual(d.NewBytes))
                refusals.Add(new PatchRefusal(RefusalRule.UnexpectedByteDiff, $"{d.Kind} of export {d.ExportIndex} at 0x{d.Offset:X} is {PatchEntry.Hex(cut.AsSpan(d.Offset, d.NewBytes.Length).ToArray())}, planned {PatchEntry.Hex(d.NewBytes)}"));
        return refusals;
    }

    /// <summary>Stock offsets where the rebuilt chunk differs from stock, not counting the inserted tags (cut out first).</summary>
    public static List<int> ChangedOffsets(ReadOnlySpan<byte> stock, ReadOnlySpan<byte> candidate, BytePlan plan)
    {
        if (!plan.ChangesSize) return Diff(stock, candidate);
        var (cut, problem) = TagInserter.RemoveInserted(candidate, plan.Insertions);
        return cut == null ? throw new InvalidDataException(problem) : Diff(stock, cut);
    }

    /// <summary>A same-size candidate (or one with its inserted tags cut out) against stock: exactly the planned bytes differ.</summary>
    private static List<PatchRefusal> VerifySameSize(ReadOnlySpan<byte> stock, ReadOnlySpan<byte> candidate, BytePlan plan)
    {
        var refusals = new List<PatchRefusal>();
        var expected = plan.ExpectedChangedOffsets.ToHashSet();
        var actual = Diff(stock, candidate);
        var extra = actual.Where(o => !expected.Contains(o)).ToList();
        var missing = expected.Where(o => !actual.Contains(o)).Order().ToList();
        if (extra.Count > 0)
            refusals.Add(new PatchRefusal(RefusalRule.UnexpectedByteDiff, $"{extra.Count} byte{(extra.Count == 1 ? "" : "s")} outside the plan differ from stock: {Offsets(extra)}"));
        if (missing.Count > 0)
            refusals.Add(new PatchRefusal(RefusalRule.UnexpectedByteDiff, $"{missing.Count} planned byte{(missing.Count == 1 ? "" : "s")} didn't change: {Offsets(missing)}"));
        foreach (var e in plan.Entries)
            if (!candidate.Slice(e.Offset, e.NewBytes.Length).SequenceEqual(e.NewBytes))
                refusals.Add(new PatchRefusal(RefusalRule.UnexpectedByteDiff, $"{e.Key.Path} at 0x{e.Offset:X} is {PatchEntry.Hex(candidate.Slice(e.Offset, e.NewBytes.Length).ToArray())}, planned {e.NewHex}", e.Key));
        return refusals;
    }

    /// <summary>Offsets where the two equal-length spans differ, ascending.</summary>
    public static List<int> Diff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var result = new List<int>();
        for (int start = 0; start < a.Length;)
        {
            var at = a[start..].CommonPrefixLength(b[start..]);
            if (start + at >= a.Length) break;
            result.Add(start + at);
            start += at + 1;
        }
        return result;
    }

    public static int CountOccurrences(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        int count = 0;
        for (int start = 0; start <= haystack.Length - needle.Length;)
        {
            var at = haystack[start..].IndexOf(needle);
            if (at < 0) break;
            count++;
            start += at + 1;
        }
        return count;
    }

    public static byte[]? Encode(FieldType type, double value)
    {
        var bytes = new byte[4];
        switch (type)
        {
            case FieldType.Float:
                var f = (float) value;
                if ((double) f != value) return null;
                BinaryPrimitives.WriteSingleLittleEndian(bytes, f);
                return bytes;
            case FieldType.Int32:
                if (value != Math.Round(value) || value < int.MinValue || value > int.MaxValue) return null;
                BinaryPrimitives.WriteInt32LittleEndian(bytes, (int) value);
                return bytes;
            default:
                return null;
        }
    }

    private static double Decode(FieldType type, byte[] bytes) => type switch
    {
        FieldType.Float => BinaryPrimitives.ReadSingleLittleEndian(bytes),
        FieldType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
        _ => bytes[0],
    };

    private static string Describe(FieldKey key, Func<int, string>? slotName) =>
        $"{GamePaths.ShortName(key.Package)} {slotName?.Invoke(key.SlotExport) ?? $"export {key.SlotExport}"}.{key.Path}";

    private static string Offsets(IEnumerable<int> offsets)
    {
        var list = offsets.ToList();
        return string.Join(", ", list.Take(8).Select(o => $"0x{o:X}")) + (list.Count > 8 ? $" … (+{list.Count - 8})" : "");
    }
}
