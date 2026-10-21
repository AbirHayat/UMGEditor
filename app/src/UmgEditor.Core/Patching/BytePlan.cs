using System.Buffers.Binary;
using UmgEditor.Core.Animation;
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
/// One animation key value to write (stock offset), in the section ("section") or the compiled copy ("compiled"), with its
/// location proof: the smallest window around it, computed on the stock bytes, that occurs exactly once in the chunk.
/// </summary>
/// <param name="Window">Half-width of that window in bytes (32…512); the window is [Offset − Window, Offset + length + Window)</param>
/// <param name="Context32Matches">How often the plain ±32 window occurs (the rule for layout values; usually &gt; 1 for keys)</param>
public sealed record AnimWrite(FieldKey Key, string Description, string Copy, int Export, int Offset, byte[] OldBytes, byte[] NewBytes,
    double OldValue, double NewValue, int Window, int Context32Matches)
{
    public string OffsetHex => $"0x{Offset:X}";
    public string OldHex => PatchEntry.Hex(OldBytes);
    public string NewHex => PatchEntry.Hex(NewBytes);
    public IEnumerable<int> ChangedOffsets => Enumerable.Range(0, OldBytes.Length).Where(i => OldBytes[i] != NewBytes[i]).Select(i => Offset + i);
    /// <summary>"unique stock window ±240 (±32 occurs 66×)"</summary>
    public string WindowText => $"unique stock window ±{Window}" + (Context32Matches == 1 ? "" : $" (±32 occurs {Context32Matches}×)");
    /// <summary>Range bounds: the bound's path in the export's flattened parse (the re-parse must show exactly it changed).</summary>
    public string? ReparsePath { get; init; }
}

/// <summary>
/// All writes for one save, or the reasons it is refused. Entries (in-place values) are sorted by offset. Insertions
/// (values that aren't stored, added as new tags) and the header values they change (Derived) are in stock offsets too.
/// </summary>
public sealed record BytePlan(string PackagePath, int StockSize, IReadOnlyList<PatchEntry> Entries, IReadOnlyList<PatchRefusal> Refusals)
{
    public IReadOnlyList<TagInsertion> Insertions { get; init; } = [];
    public IReadOnlyList<DerivedWrite> Derived { get; init; } = [];
    /// <summary>Animation key values (same size; section and compiled copy), sorted by offset.</summary>
    public IReadOnlyList<AnimWrite> AnimWrites { get; init; } = [];

    public bool Ok => Refusals.Count == 0;
    /// <summary>Animation keys edited (one key can have two writes: section and compiled copy).</summary>
    public int AnimKeyCount => AnimWrites.Select(w => w.Key).Distinct().Count();
    public int ValueCount => Entries.Count + Insertions.Count + AnimKeyCount;
    public bool ChangesSize => Insertions.Count > 0;
    /// <summary>The rebuilt chunk's size: stock + 29 bytes per inserted tag.</summary>
    public int EditedSize => StockSize + TagInserter.FloatTagSize * Insertions.Count;
    /// <summary>Stock offsets that change (values, animation keys and derived header values), not counting the inserted tags.</summary>
    public IReadOnlyList<int> ExpectedChangedOffsets => Entries.SelectMany(e => e.ChangedOffsets).Concat(AnimWrites.SelectMany(w => w.ChangedOffsets))
        .Concat(Derived.SelectMany(d => d.ChangedOffsets)).Order().ToList();

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
/// Branch experiment, animation keys only (<see cref="AnimKeyPath"/>): the ±32-exactly-once rule is replaced by
/// <see cref="UniqueWindow"/> (the smallest stock window that occurs exactly once, refused beyond ±512), because key
/// blocks repeat across widgets and a key's two copies are byte-identical. Their location must also be re-derived from
/// the section's own tags, the compiled copy must sit in the animation's compiled export with the same stock bytes, and
/// the stock bytes must decode to the parsed value. The exact byte diff and the full re-parse (PatchSession) stay hard
/// refusals.
/// </summary>
public static class BytePlanner
{
    public const int Context = 32;
    /// <summary>Animation keys: the widest stock window searched for uniqueness (half-width).</summary>
    public const int MaxAnimWindow = 512;
    private const int WindowStep = 16;

    /// <param name="stock">The stock chunk the plan is built on (always the untouched stock copy)</param>
    /// <param name="expectedStock">Stock value of a field from an independent parse (CUE4Parse), null when unknown</param>
    /// <param name="animKey">Where an animation key is (AnimKeyIndex.Find); required for animation key edits</param>
    /// <param name="animRange">Where an export's range bounds at a frame are, or why not (AnimRanges.Locate); required for range edits</param>
    public static BytePlan Plan(ReadOnlySpan<byte> stock, ZenPackageLayout layout, string packagePath, EditSet edits,
        Func<FieldKey, double?> expectedStock, Func<int, string>? slotName = null, Func<FieldKey, AnimKeyRef?>? animKey = null,
        Func<FieldKey, (AnimRangeRef? Ref, string? Why)>? animRange = null)
    {
        var walker = new TaggedPropertyWalker(layout.NameMap);
        var walked = new Dictionary<int, List<FieldLocation>>();
        var entries = new List<PatchEntry>();
        var insertions = new List<TagInsertion>();
        var animWrites = new List<AnimWrite>();
        var refusals = new List<PatchRefusal>();

        foreach (var (key, newValue) in edits.Values.OrderBy(kv => kv.Key.SlotExport).ThenBy(kv => kv.Key.Path, StringComparer.Ordinal))
        {
            void Refuse(RefusalRule rule, string message) => refusals.Add(new PatchRefusal(rule, $"{Describe(key, slotName)}: {message}", key));

            if (!key.Package.Equals(packagePath, StringComparison.OrdinalIgnoreCase)) { Refuse(RefusalRule.OtherPackage, $"belongs to {key.Package}, not {packagePath}"); continue; }
            if (AnimKeyPath.Is(key))
            {
                PlanAnimKey(stock, layout, key, newValue, animKey?.Invoke(key), expectedStock(key), animWrites, Refuse);
                continue;
            }
            if (AnimRangePath.Is(key))
            {
                PlanAnimRange(stock, key, newValue, animRange?.Invoke(key) ?? (null, "no range lookup"), animWrites, Refuse);
                continue;
            }
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
        var anims = animWrites.OrderBy(w => w.Offset).ToList();
        var spans = sorted.Select(e => (e.Key, e.Offset, Length: e.OldBytes.Length)).Concat(anims.Select(w => (w.Key, w.Offset, Length: w.OldBytes.Length))).OrderBy(s => s.Offset).ToList();
        for (int i = 1; i < spans.Count; i++)
            if (spans[i].Offset < spans[i - 1].Offset + spans[i - 1].Length)
                refusals.Add(new PatchRefusal(RefusalRule.NotLocated, $"{Describe(spans[i].Key, slotName)} overlaps {Describe(spans[i - 1].Key, slotName)} at 0x{spans[i].Offset:X}", spans[i].Key));

        var derived = TagInserter.Derive(stock, layout, insertions, out var headerProblems);
        refusals.AddRange(headerProblems.Select(p => new PatchRefusal(RefusalRule.NotInsertable, $"{GamePaths.ShortName(packagePath)}: {p}")));
        foreach (var d in derived)
            if (spans.FirstOrDefault(e => e.Offset < d.Offset + d.OldBytes.Length && d.Offset < e.Offset + e.Length) is { Key: not null } clash)
                refusals.Add(new PatchRefusal(RefusalRule.NotLocated, $"{Describe(clash.Key, slotName)} overlaps a header value the inserted tags change at 0x{d.Offset:X}", clash.Key));
        foreach (var ins in insertions)
            if (anims.FirstOrDefault(w => ins.InsertAt > w.Offset && ins.InsertAt < w.Offset + w.OldBytes.Length) is { } split)
                refusals.Add(new PatchRefusal(RefusalRule.NotLocated, $"{Describe(split.Key, slotName)}: an inserted tag would split it at 0x{ins.InsertAt:X}", split.Key));
        return new BytePlan(packagePath, stock.Length, sorted, refusals) { Insertions = TagInserter.InOrder(insertions).ToList(), Derived = derived, AnimWrites = anims };
    }

    /// <summary>
    /// One animation key edit (its value, frame or interp mode) → its writes (section copy, and the compiled copy when there
    /// is one), or refusals: the lookup must match the key, the section's own tags must put the key exactly there
    /// (re-derived here), the compiled copy must sit inside the animation's compiled export and hold the same stock bytes,
    /// the stock bytes must decode to the parsed value, the new value must be representable, and each write needs a
    /// unique stock window within ±<see cref="MaxAnimWindow"/>.
    /// </summary>
    private static void PlanAnimKey(ReadOnlySpan<byte> stock, ZenPackageLayout layout, FieldKey key, double newValue, AnimKeyRef? r, double? expected,
        List<AnimWrite> writes, Action<RefusalRule, string> refuse)
    {
        if (r == null) { refuse(RefusalRule.NotLocated, "no such animation key in the package"); return; }
        var (channel, k, field) = AnimKeyPath.Parse(key.Path);
        if (r.SectionExport != key.SlotExport || r.Channel != channel || r.KeyIndex != k) { refuse(RefusalRule.NotLocated, $"the key lookup returned {r.Description}"); return; }
        if (field == AnimKeyField.Interp && r.Kind != ChannelKind.Float) { refuse(RefusalRule.UnsupportedType, $"only float keys have an interp mode ({channel} is {r.Kind})"); return; }

        // 1. structure: the section's own tags put this key's frame and value at r.FrameAt / r.ValueAt
        ChannelBytes? own;
        try { own = AnimationBytes.Locate(stock, layout, key.SlotExport).GetValueOrDefault(channel); }
        catch (KeyNotFoundException e) { refuse(RefusalRule.NotLocated, e.Message); return; }
        if (own == null || k >= own.ValuesAt.Count || own.Kind != r.Kind || own.ValuesAt[k] != r.ValueAt || (field == AnimKeyField.Frame && own.FramesAt[k] != r.FrameAt))
        {
            var (ownAt, refAt) = field == AnimKeyField.Frame
                ? (own != null && k < own.FramesAt.Count ? own.FramesAt[k] : -1, r.FrameAt)
                : (own != null && k < own.ValuesAt.Count ? own.ValuesAt[k] : -1, r.ValueAt);
            refuse(RefusalRule.NotLocated, $"the section's own tags (export {key.SlotExport}) put key {k} of {channel} at {(ownAt >= 0 ? $"0x{ownAt:X}" : "nowhere")}, not 0x{refAt:X}");
            return;
        }
        var (at, size, compiledAt) = field switch
        {
            AnimKeyField.Frame => (r.FrameAt, 4, r.HasCompiledCopy ? r.CompiledFrameAt : -1),
            AnimKeyField.Interp => (r.ValueAt + 24, 1, r.HasCompiledCopy ? r.CompiledValueAt + 24 : -1),
            _ => (r.ValueAt, r.ValueSize, r.HasCompiledCopy ? r.CompiledValueAt : -1),
        };
        if (r.HasCompiledCopy && compiledAt < 0) { refuse(RefusalRule.NotLocated, $"the compiled copy's frame of key {k} wasn't located"); return; }
        var oldBytes = stock.Slice(at, size).ToArray();
        var copies = new List<(string Copy, int Export, int At)> { ("section", key.SlotExport, at) };
        if (r.HasCompiledCopy)
        {
            ExportSpan span;
            try { span = layout.SpanOf(r.CompiledExport); }
            catch (KeyNotFoundException e) { refuse(RefusalRule.NotLocated, e.Message); return; }
            if (compiledAt < span.Start || compiledAt + size > span.End) { refuse(RefusalRule.NotLocated, $"compiled copy 0x{compiledAt:X} is outside its compiled export {r.CompiledExport}"); return; }
            if (!stock.Slice(compiledAt, size).SequenceEqual(oldBytes)) { refuse(RefusalRule.StockBytesMismatch, $"the compiled copy at 0x{compiledAt:X} holds {PatchEntry.Hex(stock.Slice(compiledAt, size).ToArray())}, the section {PatchEntry.Hex(oldBytes)}"); return; }
            copies.Add(("compiled", r.CompiledExport, compiledAt));
        }

        // 2. the stock bytes decode to the parsed value; the new value fits the type
        double old = field switch
        {
            AnimKeyField.Frame => BinaryPrimitives.ReadInt32LittleEndian(oldBytes),
            AnimKeyField.Interp => oldBytes[0],
            _ => r.Kind switch
            {
                ChannelKind.Float => BinaryPrimitives.ReadSingleLittleEndian(oldBytes),
                ChannelKind.Integer => BinaryPrimitives.ReadInt32LittleEndian(oldBytes),
                _ => oldBytes[0],
            },
        };
        if (expected is not { } ev || (field == AnimKeyField.Value && r.Kind == ChannelKind.Float ? (float) ev != (float) old : ev != old))
        {
            refuse(RefusalRule.StockBytesMismatch, $"stock bytes at 0x{at:X} are {PatchEntry.Hex(oldBytes)} ({old}), the parse says {expected?.ToString() ?? "unknown"}");
            return;
        }
        byte[]? newBytes = field switch
        {
            AnimKeyField.Frame => Encode(FieldType.Int32, newValue),
            AnimKeyField.Interp => newValue is 0 or 1 or 2 ? [(byte) newValue] : null,
            _ => r.Kind switch
            {
                ChannelKind.Float => Encode(FieldType.Float, newValue),
                ChannelKind.Integer => Encode(FieldType.Int32, newValue),
                ChannelKind.Bool => newValue is 0 or 1 ? [(byte) newValue] : null,
                _ => newValue == Math.Round(newValue) && newValue is >= 0 and <= 255 ? [(byte) newValue] : null,
            },
        };
        var what = field switch { AnimKeyField.Frame => "a key frame (int32 ticks)", AnimKeyField.Interp => "an interp mode (0 Linear, 1 Constant, 2 Cubic)", _ => $"a {r.Kind} key" };
        if (newBytes == null) { refuse(RefusalRule.NotRepresentable, $"{newValue} can't be stored exactly as {what}"); return; }

        // 3. location proof: the smallest unique stock window
        var description = r.Description + field switch { AnimKeyField.Frame => " · frame", AnimKeyField.Interp => " · interp", _ => "" };
        foreach (var (copy, export, offset) in copies)
        {
            if (UniqueWindow(stock, offset, size, MaxAnimWindow) is not { } w)
            {
                refuse(RefusalRule.ContextNotUnique, $"{copy} copy at 0x{offset:X}: no window up to ±{MaxAnimWindow} bytes occurs only once in the chunk");
                return;
            }
            int from = Math.Max(0, offset - Context), to = Math.Min(stock.Length, offset + size + Context);
            writes.Add(new AnimWrite(key, description, copy, export, offset, stock.Slice(offset, size).ToArray(), newBytes, old, newValue, w, CountOccurrences(stock, stock[from..to])));
        }
    }

    /// <summary>
    /// One range edit (every closed bound of an export at a stock frame → a new frame) → one write per bound, or refusals:
    /// the bounds must be located (AnimRanges.Locate: as often in the bytes as in the parse), each must hold the old
    /// frame in the stock bytes, the new frame must be an int32, and each write needs a unique stock window within
    /// ±<see cref="MaxAnimWindow"/>. The re-parse (PatchSession) then requires exactly those bound paths to change.
    /// </summary>
    private static void PlanAnimRange(ReadOnlySpan<byte> stock, FieldKey key, double newValue, (AnimRangeRef? Ref, string? Why) found,
        List<AnimWrite> writes, Action<RefusalRule, string> refuse)
    {
        var frame = AnimRangePath.Parse(key.Path);
        if (found.Ref is not { } r) { refuse(RefusalRule.NotLocated, found.Why ?? $"no range bound at frame {frame}"); return; }
        if (r.Export != key.SlotExport || r.Frame != frame) { refuse(RefusalRule.NotLocated, $"the range lookup returned {r.Description}"); return; }
        if (Encode(FieldType.Int32, newValue) is not { } newBytes) { refuse(RefusalRule.NotRepresentable, $"{newValue} can't be stored exactly as a frame (int32 ticks)"); return; }
        var oldBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(oldBytes, frame);
        var planned = new List<AnimWrite>();
        foreach (var b in r.Bounds)
        {
            if (b.Offset < 0 || b.Offset + 4 > stock.Length || !stock.Slice(b.Offset, 4).SequenceEqual(oldBytes))
            {
                refuse(RefusalRule.StockBytesMismatch, $"{b.Path} at 0x{b.Offset:X} doesn't hold frame {frame} in the stock bytes");
                return;
            }
            if (UniqueWindow(stock, b.Offset, 4, MaxAnimWindow) is not { } w)
            {
                refuse(RefusalRule.ContextNotUnique, $"{b.Path} at 0x{b.Offset:X}: no window up to ±{MaxAnimWindow} bytes occurs only once in the chunk");
                return;
            }
            int from = Math.Max(0, b.Offset - Context), to = Math.Min(stock.Length, b.Offset + 4 + Context);
            planned.Add(new AnimWrite(key, $"{r.Description} · {b.Path.Replace("Properties.", "")}", "range", r.Export, b.Offset, oldBytes, newBytes, frame, newValue, w,
                CountOccurrences(stock, stock[from..to])) { ReparsePath = b.Path });
        }
        writes.AddRange(planned);
    }

    /// <summary>
    /// The smallest half-width W (32, 48, … up to <paramref name="max"/>) such that the stock bytes
    /// [at − W, at + length + W) occur exactly once in the chunk (overlapping occurrences count), or null. Computed on
    /// the stock bytes only, so windows of nearby writes may overlap each other without affecting each other.
    /// </summary>
    public static int? UniqueWindow(ReadOnlySpan<byte> stock, int at, int length, int max = MaxAnimWindow)
    {
        for (int w = Context; w <= max; w += WindowStep)
        {
            int from = Math.Max(0, at - w), to = Math.Min(stock.Length, at + length + w);
            if (CountOccurrences(stock, stock[from..to], stopAt: 2) == 1) return w;
            if (from == 0 && to == stock.Length) break;
        }
        return null;
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
        foreach (var w in plan.AnimWrites) w.NewBytes.CopyTo(result, w.Offset);
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
        foreach (var w in plan.AnimWrites)
            if (!candidate.Slice(w.Offset, w.NewBytes.Length).SequenceEqual(w.NewBytes))
                refusals.Add(new PatchRefusal(RefusalRule.UnexpectedByteDiff, $"{w.Description} ({w.Copy}) at 0x{w.Offset:X} is {PatchEntry.Hex(candidate.Slice(w.Offset, w.NewBytes.Length).ToArray())}, planned {w.NewHex}", w.Key));
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

    /// <summary>Occurrences of needle in haystack, overlapping ones included (stops counting at <paramref name="stopAt"/>).</summary>
    public static int CountOccurrences(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle, int stopAt = int.MaxValue)
    {
        int count = 0;
        for (int start = 0; start <= haystack.Length - needle.Length && count < stopAt;)
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
