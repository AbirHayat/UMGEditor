using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.UE4.Assets;
using UmgEditor.Core.Animation;
using UmgEditor.Core.Editing;

namespace UmgEditor.Core.Patching;

/// <summary>Outcome of PatchSession.Save. When refused, nothing was written and the previous save is untouched.</summary>
public sealed record SaveResult(bool Saved, BytePlan Plan, IReadOnlyList<PatchRefusal> Refusals, string? OutputPath, string? Sha256, IReadOnlyList<int> ChangedOffsets)
{
    public string Summary => Saved
        ? $"saved {Plan.ValueCount} value{(Plan.ValueCount == 1 ? "" : "s")}{(Plan.ChangesSize ? $" ({Plan.Insertions.Count} as new tag{(Plan.Insertions.Count == 1 ? "" : "s")})" : "")}, " +
          $"{ChangedOffsets.Count} byte{(ChangedOffsets.Count == 1 ? "" : "s")} differ from stock{(Plan.ChangesSize ? " besides the inserted tags" : "")}, {Plan.SizeText}"
        : "refused: " + string.Join(" | ", Refusals.Select(r => $"[{r.Rule}] {r.Message}"));
}

/// <summary>
/// Editing state of one package: a verified stock copy of its raw Zen chunk under work\edits, the current EditSet with
/// undo/redo, dirty state against the last save, and Save. Every save is rebuilt from the stock copy plus the full
/// EditSet (never from a previous save), checked in memory, written to a temp file, reloaded from disk, diffed against
/// stock and re-parsed with CUE4Parse, and only then moved over the previous save. All writes go through WriteGuard.
/// Files: &lt;Name&gt;.stock.uasset (stock copy), &lt;Name&gt;.edited.uasset (last save), &lt;Name&gt;.edits.json (edits + byte plan).
/// </summary>
public sealed class PatchSession
{
    private readonly GameSource _source;
    private readonly WriteGuard _guard;
    private readonly byte[] _stock;
    private readonly IoPackage _stockPackage;
    private readonly ZenPackageLayout _layout;
    private readonly Dictionary<int, Dictionary<string, string>> _stockValues = [];
    private readonly Dictionary<int, Dictionary<string, string>> _stockFlat = [];
    private readonly Dictionary<FieldKey, string?> _insertRefusals = [];
    private readonly Stack<EditSet> _undo = new(), _redo = new();

    public string PackagePath { get; }
    public string FileKey { get; }
    /// <summary>24 hex digits, the chunk file name for `retoc pack-raw`.</summary>
    public string ChunkIdHex { get; }
    public string Folder { get; }
    public string StockCopyPath { get; }
    public string EditedPath { get; }
    public string ManifestPath { get; }
    public string StockSha256 { get; }
    public int StockSize => _stock.Length;

    public EditSet Current { get; private set; } = EditSet.Empty;
    /// <summary>The EditSet the file at EditedPath was built from (Empty when nothing was saved).</summary>
    public EditSet Saved { get; private set; } = EditSet.Empty;
    public bool IsDirty => !Current.ContentEquals(Saved);
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    /// <summary>Set when Open found something to report (e.g. a previous save that no longer matches its manifest).</summary>
    public string? OpenNote { get; private set; }

    public event EventHandler? StateChanged;

    /// <summary>Test seam: transforms the rebuilt bytes before verification (simulates a faulty patch builder).</summary>
    internal Func<byte[], byte[]>? BeforeVerify { get; set; }

    private PatchSession(GameSource source, WriteGuard guard, string packagePath, string fileKey, string folder, byte[] stock, IoPackage package)
    {
        _source = source;
        _guard = guard;
        PackagePath = packagePath;
        FileKey = fileKey;
        ChunkIdHex = source.ChunkIdHex(fileKey);
        Folder = folder;
        var name = GamePaths.ShortName(packagePath);
        StockCopyPath = Path.Combine(folder, name + ".stock.uasset");
        EditedPath = Path.Combine(folder, name + ".edited.uasset");
        ManifestPath = Path.Combine(folder, name + ".edits.json");
        _stock = stock;
        StockSha256 = Sha256(stock);
        _stockPackage = package;
        _layout = ZenPackageLayout.Read(stock, package);
    }

    /// <summary>Default working folder: work\edits\&lt;package path below /Game/&gt;.</summary>
    /// <param name="editSet">Optional named set (e.g. "C1"): work\edits\&lt;set&gt;\&lt;package path&gt;, so several independent edits of one package can coexist</param>
    public static string DefaultFolder(WriteGuard guard, string packagePath, string? editSet = null)
    {
        var relative = packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ? packagePath["/Game/".Length..] : packagePath.TrimStart('/');
        var root = editSet == null ? guard.Root : Path.Combine(guard.Root, editSet);
        return Path.Combine(root, Path.Combine(relative.Split('/', StringSplitOptions.RemoveEmptyEntries)));
    }

    /// <summary>
    /// Opens (or resumes) the working copy of a package. The stock copy is created on first open and must hash to the
    /// game's current file on every later open. Saved edits are restored from the manifest.
    /// </summary>
    /// <param name="folder">Working folder; must be under work\edits (default: <see cref="DefaultFolder"/>)</param>
    public static PatchSession Open(GameSource source, string packagePath, string? folder = null)
    {
        var guard = new WriteGuard(source.Config);
        folder = guard.Check(folder ?? DefaultFolder(guard, packagePath));
        var (fileKey, stock) = source.ReadPackageBytes(packagePath);
        var package = (IoPackage) source.LoadPackage(packagePath);
        var session = new PatchSession(source, guard, packagePath, fileKey, folder, stock, package);

        if (File.Exists(session.StockCopyPath))
        {
            var copy = File.ReadAllBytes(session.StockCopyPath);
            if (Sha256(copy) != session.StockSha256)
                throw new InvalidDataException($"{session.StockCopyPath} no longer matches the game's {fileKey} (sha256 {Sha256(copy)[..16]}… vs {session.StockSha256[..16]}…). " +
                                               "The game was updated or the copy was changed; move the folder away to start over.");
        }
        else
        {
            var tmp = session.StockCopyPath + ".tmp";
            guard.WriteAllBytes(tmp, stock);
            if (Sha256(File.ReadAllBytes(tmp)) != session.StockSha256) { guard.Delete(tmp); throw new IOException($"stock copy {tmp} didn't read back identically"); }
            guard.Move(tmp, session.StockCopyPath);
        }
        session.RestoreFromManifest();
        return session;
    }

    // ---- values ----

    private AnimKeyIndex? _animIndex;

    /// <summary>Every animation key of the package (read once, from the stock package).</summary>
    public AnimKeyIndex AnimKeys => _animIndex ??= AnimKeyIndex.Read(_source, PackagePath);

    /// <summary>Test seam: transforms an animation key's location before planning (simulates a wrong lookup).</summary>
    internal Func<AnimKeyRef?, AnimKeyRef?>? AnimKeyTransform { get; set; }

    /// <summary>Where an animation key edit writes (section and compiled copy), or null when it isn't an animation key of this package.</summary>
    public AnimKeyRef? AnimKey(FieldKey key)
    {
        if (!AnimKeyPath.Is(key) || !key.Package.Equals(PackagePath, StringComparison.OrdinalIgnoreCase)) return null;
        var found = AnimKeys.Find(key);
        return AnimKeyTransform != null ? AnimKeyTransform(found) : found;
    }

    private readonly Dictionary<FieldKey, (AnimRangeRef? Ref, string? Why)> _ranges = [];

    /// <summary>Test seam: transforms a range edit's located bounds before planning (simulates a wrong lookup).</summary>
    internal Func<AnimRangeRef?, AnimRangeRef?>? AnimRangeTransform { get; set; }

    /// <summary>Where a range edit writes (every closed bound of its export at its stock frame), or why it can't.</summary>
    public (AnimRangeRef? Ref, string? Why) AnimRange(FieldKey key)
    {
        if (!AnimRangePath.Is(key) || !key.Package.Equals(PackagePath, StringComparison.OrdinalIgnoreCase)) return (null, $"{key.Path} is not a range edit of {GamePaths.ShortName(PackagePath)}");
        if (!_ranges.TryGetValue(key, out var found))
        {
            if (key.SlotExport < 0 || key.SlotExport >= _stockPackage.ExportsLazy.Length) found = (null, $"export {key.SlotExport} doesn't exist");
            else
            {
                var json = Newtonsoft.Json.Linq.JObject.Parse(Newtonsoft.Json.JsonConvert.SerializeObject(_stockPackage.ExportsLazy[key.SlotExport].Value));
                var r = AnimRanges.Locate(_stock, _layout, key.SlotExport, SlotName(key.SlotExport), json, AnimRangePath.Parse(key.Path), out var why);
                found = (r, why);
            }
            _ranges[key] = found;
        }
        return AnimRangeTransform != null ? (AnimRangeTransform(found.Ref), found.Why) : found;
    }

    /// <summary>
    /// C9's range edit of one animation as edits: in every export of the animation (MovieScene, sections, compiled data),
    /// every closed range bound at <paramref name="oldEnd"/> moves to <paramref name="newEnd"/>, and the MovieScene's
    /// playback bound at oldEnd + 1 (its exclusive end) to newEnd + 1. Exports without such a bound are left out.
    /// </summary>
    public IReadOnlyList<FieldChange> AnimationEndChanges(string animation, int oldEnd, int newEnd)
    {
        var changes = new List<FieldChange>();
        foreach (var (i, type) in AnimRanges.ExportsOf(_stockPackage, animation))
        {
            var (from, to) = type == "MovieScene" ? (oldEnd + 1, newEnd + 1)
                : type.EndsWith("Section", StringComparison.Ordinal) || type == "MovieSceneCompiledData" ? (oldEnd, newEnd) : (int.MinValue, 0);
            if (from == int.MinValue) continue;
            var key = new FieldKey(PackagePath, i, AnimRangePath.Make(from));
            if (AnimRange(key).Ref != null) changes.Add(new FieldChange(key, from, to));
        }
        return changes;
    }

    /// <summary>The stock value of a stored field (or animation key / range bound) as CUE4Parse parsed it, or null when the field isn't stored.</summary>
    public double? StockValue(FieldKey key)
    {
        if (AnimKeyPath.Is(key)) return key.Package.Equals(PackagePath, StringComparison.OrdinalIgnoreCase) && AnimKeys.Find(key) is { } a ? a.Stock(AnimKeyPath.Parse(key.Path).Field) : null;
        if (AnimRangePath.Is(key)) return AnimRange(key).Ref != null ? AnimRangePath.Parse(key.Path) : null;
        if (!key.Package.Equals(PackagePath, StringComparison.OrdinalIgnoreCase) || key.SlotExport < 0 || key.SlotExport >= _stockPackage.ExportsLazy.Length) return null;
        if (!_stockValues.TryGetValue(key.SlotExport, out var values))
            _stockValues[key.SlotExport] = values = WidgetTreeReader.StoredValues(_stockPackage.ExportsLazy[key.SlotExport].Value);
        return values.TryGetValue(key.Path, out var text) && TryParse(text, out var v) ? v : null;
    }

    /// <summary>The engine default of a slot field that isn't stored (the slot class's default), null when stored or unknown.</summary>
    public double? DefaultValue(FieldKey key)
    {
        if (StockValue(key) != null || !key.Package.Equals(PackagePath, StringComparison.OrdinalIgnoreCase) || key.SlotExport < 0 || key.SlotExport >= _stockPackage.ExportsLazy.Length) return null;
        return WidgetTreeReader.DefaultOf(_stockPackage.ExportsLazy[key.SlotExport].Value.ExportType, key.Path) is { } text && TryParse(text, out var v) ? v : null;
    }

    /// <summary>What the field holds without edits: the stored value, or the engine default when it isn't stored.</summary>
    public double? BaseValue(FieldKey key) => StockValue(key) ?? DefaultValue(key);

    /// <summary>
    /// Why a value that isn't stored can't be added as a new tag in this package, or null when it can (branch experiment,
    /// canary C10): only a missing Offsets member of a stored Offsets struct, with its name already in the name map and a
    /// unique ±32-byte stock context at the insertion point.
    /// </summary>
    public string? InsertRefusal(FieldKey key)
    {
        if (_insertRefusals.TryGetValue(key, out var cached)) return cached;
        string? why;
        if (TagInserter.Insertable(key.Path) is not { } at)
            why = $"{SlotName(key.SlotExport)}.{key.Path} is not stored in {GamePaths.ShortName(PackagePath)}; only a missing Offsets value can be added as a new tag.";
        else
        {
            var (ins, refusal) = TagInserter.PlanFloat(_stock, _layout, key.SlotExport, at.StructPath, at.Field, 0f, at.Order);
            why = ins == null ? $"{SlotName(key.SlotExport)}.{key.Path} is not stored in {GamePaths.ShortName(PackagePath)} and can't be added: {refusal}"
                : ins.ContextMatches != 1 && BytePlanner.UniqueWindow(_stock, ins.InsertAt, 0) == null
                    ? $"{SlotName(key.SlotExport)}.{key.Path} can't be added: the ±{BytePlanner.Context}-byte stock context at its insertion point matches {ins.ContextMatches} times and no window up to ±{BytePlanner.MaxAnimWindow} bytes occurs only once"
                : null;
        }
        _insertRefusals[key] = why;
        return why;
    }

    public string SlotName(int exportIndex) =>
        exportIndex >= 0 && exportIndex < _stockPackage.ExportsLazy.Length ? _stockPackage.ExportsLazy[exportIndex].Value.Name : $"export {exportIndex}";

    // ---- edits, undo/redo ----

    /// <summary>Applies an edit from SlotEditor. Returns why it was refused, or null when applied (or a no-op).</summary>
    public string? Apply(EditResult edit)
    {
        if (!edit.Allowed) return edit.Reason ?? "edit not allowed";
        foreach (var c in edit.Changes)
        {
            if (!c.Key.Package.Equals(PackagePath, StringComparison.OrdinalIgnoreCase))
                return $"{c.Key.Path} belongs to {GamePaths.ShortName(c.Key.Package)}, not {GamePaths.ShortName(PackagePath)}; open that package to edit it.";
            if (AnimKeyPath.Is(c.Key))
            {
                if (AnimKeys.Find(c.Key) is not { } k) return $"{c.Key.Path} (section export {c.Key.SlotExport}) is not an animation key of {GamePaths.ShortName(PackagePath)}.";
                if (k.Stock(AnimKeyPath.Parse(c.Key.Path).Field) == null) return $"{c.Key.Path}: a {k.Kind} key has no interp mode.";
                continue;
            }
            if (AnimRangePath.Is(c.Key))
            {
                if (AnimRange(c.Key) is { Ref: null, Why: var rangeWhy }) return rangeWhy;
                continue;
            }
            if (StockValue(c.Key) == null && InsertRefusal(c.Key) is { } why)
                return why;
        }
        var next = Current.With(edit.Changes, BaseValue);
        if (next.ContentEquals(Current)) return null;
        _undo.Push(Current);
        _redo.Clear();
        Current = next;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return null;
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        _redo.Push(Current);
        Current = _undo.Pop();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        _undo.Push(Current);
        Current = _redo.Pop();
        StateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // ---- bytes ----

    /// <summary>The "bytes to patch" for the current edits (offset, old/new value, hex, context), or why they'd be refused.</summary>
    public BytePlan Plan() => BytePlanner.Plan(_stock, _layout, PackagePath, Current, StockValue, SlotName, AnimKey, AnimRange);

    /// <summary>Rebuilds the edited chunk from the stock copy + the full current EditSet and writes it if every check passes.</summary>
    public SaveResult Save()
    {
        var refusals = new List<PatchRefusal>();
        byte[] stock;
        if (!File.Exists(StockCopyPath))
        {
            refusals.Add(new PatchRefusal(RefusalRule.StockCopyChanged, $"the stock copy {StockCopyPath} is missing"));
            return Refused(Plan(), refusals);
        }
        stock = File.ReadAllBytes(StockCopyPath);
        if (Sha256(stock) != StockSha256)
            refusals.Add(new PatchRefusal(RefusalRule.StockCopyChanged, $"the stock copy {StockCopyPath} no longer hashes to the game's file ({Sha256(stock)[..16]}… vs {StockSha256[..16]}…)"));

        var plan = BytePlanner.Plan(stock, _layout, PackagePath, Current, StockValue, SlotName, AnimKey, AnimRange);
        refusals.AddRange(plan.Refusals);
        if (refusals.Count > 0) return Refused(plan, refusals);

        var candidate = BytePlanner.Apply(stock, plan);
        if (BeforeVerify != null) candidate = BeforeVerify(candidate);
        refusals.AddRange(BytePlanner.Verify(stock, candidate, plan));
        if (refusals.Count > 0) return Refused(plan, refusals);

        // write to a temp file, reload it from disk, and check it again before it replaces the previous save
        var tmp = EditedPath + ".tmp";
        _guard.WriteAllBytes(tmp, candidate);
        var reloaded = File.ReadAllBytes(tmp);
        if (!reloaded.AsSpan().SequenceEqual(candidate))
            refusals.Add(new PatchRefusal(RefusalRule.ReloadMismatch, $"{tmp} read back differently from what was written"));
        else
            refusals.AddRange(BytePlanner.Verify(stock, reloaded, plan).Concat(ReparseCheck(reloaded, plan)));
        if (refusals.Count > 0)
        {
            _guard.Delete(tmp);
            return Refused(plan, refusals);
        }

        var changed = BytePlanner.ChangedOffsets(stock, reloaded, plan);
        var sha = Sha256(reloaded);
        _guard.Move(tmp, EditedPath);
        WriteManifest(plan, sha, changed);
        Saved = Current;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return new SaveResult(true, plan, [], EditedPath, sha, changed);
    }

    private static SaveResult Refused(BytePlan plan, List<PatchRefusal> refusals) => new(false, plan, refusals, null, null, []);

    /// <summary>
    /// The saved chunk, for packing: only when there are saved edits, nothing unsaved, and the file on disk equals a
    /// fresh rebuild of the stock copy + the saved EditSet (so a file changed after saving is never shipped).
    /// </summary>
    public (byte[]? Chunk, string? Refusal) ReadSavedChunk()
    {
        var name = GamePaths.ShortName(PackagePath);
        if (OpenNote != null) return (null, $"{name}: {OpenNote}");
        if (IsDirty) return (null, $"{name} has unsaved edits; save (or undo) them first.");
        if (Saved.Count == 0 || !File.Exists(EditedPath)) return (null, $"{name} has no saved edits in {Folder}.");
        var stock = File.ReadAllBytes(StockCopyPath);
        if (Sha256(stock) != StockSha256) return (null, $"{StockCopyPath} no longer matches the game's file.");
        var plan = BytePlanner.Plan(stock, _layout, PackagePath, Saved, StockValue, SlotName, AnimKey, AnimRange);
        if (!plan.Ok) return (null, $"{name}: the saved edits no longer plan: {string.Join(" | ", plan.Refusals.Select(r => r.Message))}");
        var rebuilt = BytePlanner.Apply(stock, plan);
        if (BytePlanner.Verify(stock, rebuilt, plan) is { Count: > 0 } bad) return (null, $"{name}: {string.Join(" | ", bad.Select(r => r.Message))}");
        var onDisk = File.ReadAllBytes(EditedPath);
        if (!onDisk.AsSpan().SequenceEqual(rebuilt)) return (null, $"{EditedPath} differs from a rebuild of its saved edits; save again.");
        return (onDisk, null);
    }

    /// <summary>
    /// Re-parses the written bytes with CUE4Parse and walks them again: in every edited slot export each stored value is
    /// unchanged from stock except the edited ones, which read back as the new values, and every value sits where stock
    /// had it (moved by the tags inserted before it). With inserted tags every export of the package is also compared
    /// with stock: only the edited values may differ.
    /// </summary>
    private IEnumerable<PatchRefusal> ReparseCheck(byte[] written, BytePlan plan)
    {
        var refusals = new List<PatchRefusal>();
        IoPackage reparsed;
        ZenPackageLayout layout;
        try
        {
            reparsed = _source.ParsePackage(FileKey, written);
            layout = ZenPackageLayout.Read(written, reparsed);
        }
        catch (Exception e)
        {
            refusals.Add(new PatchRefusal(RefusalRule.ReloadMismatch, $"the written file doesn't parse: {e.Message}"));
            return refusals;
        }
        var walker = new TaggedPropertyWalker(layout.NameMap);
        var stockWalker = new TaggedPropertyWalker(_layout.NameMap);
        var values = plan.Entries.Select(e => (Export: e.Key.SlotExport, e.Key.Path, Value: e.NewValue))
            .Concat(plan.Insertions.Select(i => (Export: i.ExportIndex, i.Path, Value: (double) i.Value))).ToList();
        foreach (var slot in values.GroupBy(v => v.Export))
        {
            var edited = slot.ToDictionary(v => v.Path, v => v.Value);
            var inserted = plan.Insertions.Where(i => i.ExportIndex == slot.Key).ToDictionary(i => i.Path);
            var stockParsed = WidgetTreeReader.StoredValues(_stockPackage.ExportsLazy[slot.Key].Value);
            var nowParsed = WidgetTreeReader.StoredValues(reparsed.ExportsLazy[slot.Key].Value);
            if (!stockParsed.Keys.Concat(inserted.Keys).ToHashSet().SetEquals(nowParsed.Keys))
                refusals.Add(new PatchRefusal(RefusalRule.ReloadMismatch, $"{SlotName(slot.Key)}: the stored properties changed after reload ({string.Join(", ", nowParsed.Keys.Except(stockParsed.Keys).Except(inserted.Keys).Concat(stockParsed.Keys.Concat(inserted.Keys).Except(nowParsed.Keys)))})"));
            foreach (var (path, text) in nowParsed)
            {
                if (edited.TryGetValue(path, out var want))
                {
                    if (!TryParse(text, out var got) || (float) got != (float) want)
                        refusals.Add(new PatchRefusal(RefusalRule.ReloadMismatch, $"{SlotName(slot.Key)}.{path} reloads as {text}, saved {want}"));
                }
                else if (stockParsed.GetValueOrDefault(path) != text)
                    refusals.Add(new PatchRefusal(RefusalRule.ReloadMismatch, $"{SlotName(slot.Key)}.{path} reloads as {text}, stock is {stockParsed.GetValueOrDefault(path)}"));
            }

            var stockWalk = stockWalker.Walk(_stock, _layout.SpanOf(slot.Key)).ToDictionary(f => f.Path);
            var walked = walker.Walk(written, layout.SpanOf(slot.Key));
            foreach (var f in walked)
            {
                var want = edited.TryGetValue(f.Path, out var v) ? v : stockWalk.TryGetValue(f.Path, out var s) ? s.Value : double.NaN;
                int? where = stockWalk.TryGetValue(f.Path, out var sf) ? TagInserter.Shifted(plan.Insertions, sf.Offset)
                    : inserted.TryGetValue(f.Path, out var ins) ? TagInserter.NewTagOffset(plan.Insertions, ins) + TagInserter.FloatTagSize - 4 : null;
                if (f.Value != want || where != f.Offset)
                    refusals.Add(new PatchRefusal(RefusalRule.ReloadMismatch, $"{SlotName(slot.Key)}.{f.Path} at 0x{f.Offset:X} walks as {f.Value}, expected {want}{(where is { } w ? $" at 0x{w:X}" : " (not in stock and not inserted)")}"));
            }
            foreach (var missing in inserted.Keys.Where(p => walked.All(f => f.Path != p)))
                refusals.Add(new PatchRefusal(RefusalRule.ReloadMismatch, $"{SlotName(slot.Key)}.{missing} was inserted but isn't found by the walk"));
        }

        if (plan.ChangesSize || plan.AnimWrites.Count > 0)
        {
            // every export compared with stock: only the edited values may differ; for animation keys, exactly the key's
            // value in its own section and in the compiled template that points back at that section
            var byKey = values.ToDictionary(v => (v.Export, "Properties." + v.Path), v => v.Value);
            // (frame: its Times entry; interp: its InterpMode); for range edits, exactly the planned bound paths
            var anim = plan.AnimWrites.Select(w => (w, Ref: AnimKeyPath.Is(w.Key) ? AnimKey(w.Key) : null)).Where(x => x.Ref != null || x.w.ReparsePath != null).ToList();
            var seen = new HashSet<AnimWrite>();
            bool AnimAllowed(int export, string key, string? now)
            {
                foreach (var (w, r) in anim)
                {
                    if (w.Export != export) continue;
                    bool match, isFloat;
                    if (r == null) { match = key == w.ReparsePath; isFloat = false; }
                    else
                    {
                        var field = AnimKeyPath.Parse(w.Key.Path).Field;
                        isFloat = field == AnimKeyField.Value && r.Kind == Animation.ChannelKind.Float;
                        var suffix = field switch
                        {
                            AnimKeyField.Frame => $"{r.Channel}.Times[{r.KeyIndex}].Value",
                            AnimKeyField.Interp => $"{r.Channel}.Values[{r.KeyIndex}].InterpMode",
                            _ => $"{r.Channel}.Values[{r.KeyIndex}]{(r.Kind == Animation.ChannelKind.Float ? ".Value" : "")}",
                        };
                        if (w.Copy == "section") match = key == "Properties." + suffix;
                        else
                        {
                            match = key.EndsWith(".Data." + suffix, StringComparison.Ordinal)
                                    && StockFlat(export).GetValueOrDefault(key[..^suffix.Length] + "SourceSectionPtr.ObjectPath") is { } source
                                    && source.Trim('"').EndsWith("." + w.Key.SlotExport, StringComparison.Ordinal);
                        }
                    }
                    if (!match) continue;
                    var ok = now != null && TryParse(now.Trim('"'), out var got) && (isFloat ? (float) got == (float) w.NewValue : got == w.NewValue);
                    if (ok) seen.Add(w);
                    return ok;
                }
                return false;
            }
            var (_, problems, _) = TagInserter.ReparseDiff(_source, FileKey, _stockPackage, written, StockFlat, (export, key, _, now) =>
                byKey.TryGetValue((export, key), out var want) ? now != null && TryParse(now, out var got) && (float) got == (float) want : AnimAllowed(export, key, now));
            refusals.AddRange(problems.Select(p => new PatchRefusal(RefusalRule.ReloadMismatch, p)));
            foreach (var (w, _) in anim.Where(x => !seen.Contains(x.w)))
                refusals.Add(new PatchRefusal(RefusalRule.ReloadMismatch, $"{w.Description} ({w.Copy} copy, export {w.Export}) doesn't re-parse as {w.NewValue}", w.Key));
            foreach (var w in plan.AnimWrites.Where(w => AnimKeyPath.Is(w.Key) ? AnimKey(w.Key) == null : w.ReparsePath == null))
                refusals.Add(new PatchRefusal(RefusalRule.NotLocated, $"{w.Key.Path}: no longer an animation key or range of the package", w.Key));
        }
        return refusals;
    }

    private Dictionary<string, string> StockFlat(int export)
    {
        if (!_stockFlat.TryGetValue(export, out var flat)) _stockFlat[export] = flat = TagInserter.Flatten(_stockPackage.ExportsLazy[export].Value);
        return flat;
    }

    // ---- manifest ----

    /// <param name="Stock">The stored value, or the engine default when Inserted (not stored: saving adds it as a new tag)</param>
    private sealed record ManifestEdit(int SlotExport, string Slot, string Path, double Stock, double Value, bool Inserted = false);
    private sealed record ManifestEntry(string Slot, string Path, int Offset, string OffsetHex, string Type, double Old, double New, string OldHex, string NewHex, string ContextHex, int ContextMatches, int Window = BytePlanner.Context);
    /// <param name="InsertAt">Stock offset the 29-byte tag goes in front of</param>
    private sealed record ManifestInsertion(string Slot, string Path, int InsertAt, string InsertAtHex, string Before, float Value, string TagHex, int ContextMatches, int Window = BytePlanner.Context);
    /// <param name="ChangedOffsets">Stock offsets that differ, with any inserted tags cut out</param>
    /// <param name="Window">Half-width of the smallest stock window around the write that occurs exactly once (animation keys only)</param>
    private sealed record ManifestAnimWrite(string Key, string Description, string Copy, int Export, int Offset, string OffsetHex, double Old, double New,
        string OldHex, string NewHex, int Window, int Context32Matches);
    private sealed record Manifest(string Package, string FileKey, string ChunkId, int StockSize, string StockSha256, string EditedSha256, DateTime SavedUtc,
        List<ManifestEdit> Edits, List<ManifestEntry> Plan, List<int> ChangedOffsets, int EditedSize = 0, List<ManifestInsertion>? Insertions = null,
        List<ManifestAnimWrite>? AnimWrites = null);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private void WriteManifest(BytePlan plan, string editedSha, IReadOnlyList<int> changed)
    {
        var manifest = new Manifest(PackagePath, FileKey, ChunkIdHex, StockSize, StockSha256, editedSha, DateTime.UtcNow,
            Current.Values.OrderBy(kv => kv.Key.SlotExport).ThenBy(kv => kv.Key.Path, StringComparer.Ordinal)
                .Select(kv => new ManifestEdit(kv.Key.SlotExport, SlotName(kv.Key.SlotExport), kv.Key.Path, BaseValue(kv.Key) ?? 0, kv.Value, StockValue(kv.Key) == null)).ToList(),
            plan.Entries.Select(e => new ManifestEntry(e.SlotName, e.Key.Path, e.Offset, e.OffsetHex, e.Type.ToString(), e.OldValue, e.NewValue, e.OldHex, e.NewHex, e.ContextHex, e.ContextMatches, e.Window)).ToList(),
            changed.ToList(), plan.EditedSize,
            plan.Insertions.Select(i => new ManifestInsertion(i.SlotName, i.Path, i.InsertAt, i.InsertAtHex, i.Before, i.Value, i.TagHex, i.ContextMatches, i.Window)).ToList(),
            plan.AnimWrites.Select(w => new ManifestAnimWrite($"{w.Key.SlotExport}:{w.Key.Path}", w.Description, w.Copy, w.Export, w.Offset, w.OffsetHex, w.OldValue, w.NewValue,
                w.OldHex, w.NewHex, w.Window, w.Context32Matches)).ToList());
        var tmp = ManifestPath + ".tmp";
        _guard.WriteAllText(tmp, JsonSerializer.Serialize(manifest, JsonOptions));
        _guard.Move(tmp, ManifestPath);
    }

    /// <summary>Resumes saved edits: Current = Saved = the manifest's edits when the saved file still matches it.</summary>
    private void RestoreFromManifest()
    {
        if (!File.Exists(ManifestPath)) return;
        Manifest? m;
        try { m = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(ManifestPath), JsonOptions); }
        catch (JsonException e) { OpenNote = $"{ManifestPath} can't be read ({e.Message}); starting without saved edits."; return; }
        if (m == null || !m.Package.Equals(PackagePath, StringComparison.OrdinalIgnoreCase) || m.StockSha256 != StockSha256)
        {
            OpenNote = $"{ManifestPath} belongs to another package or stock version; starting without saved edits.";
            return;
        }
        var edits = EditSet.Empty.With(m.Edits.Select(e => new FieldChange(new FieldKey(PackagePath, e.SlotExport, e.Path), e.Stock, e.Value)), BaseValue);
        Current = edits;
        if (File.Exists(EditedPath) && Sha256(File.ReadAllBytes(EditedPath)) == m.EditedSha256) Saved = edits;
        else OpenNote = $"{EditedPath} doesn't match its manifest; the edits are restored but unsaved.";
    }

    // ---- helpers ----

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool TryParse(string text, out double value)
    {
        try { value = EditSet.Parse(text); return true; }
        catch (FormatException) { value = 0; return false; }
    }
}
