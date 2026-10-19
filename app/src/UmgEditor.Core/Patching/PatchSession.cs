using System.Security.Cryptography;
using System.Text.Json;
using CUE4Parse.UE4.Assets;
using UmgEditor.Core.Editing;

namespace UmgEditor.Core.Patching;

/// <summary>Outcome of PatchSession.Save. When refused, nothing was written and the previous save is untouched.</summary>
public sealed record SaveResult(bool Saved, BytePlan Plan, IReadOnlyList<PatchRefusal> Refusals, string? OutputPath, string? Sha256, IReadOnlyList<int> ChangedOffsets)
{
    public string Summary => Saved
        ? $"saved {Plan.Entries.Count} value{(Plan.Entries.Count == 1 ? "" : "s")}, {ChangedOffsets.Count} byte{(ChangedOffsets.Count == 1 ? "" : "s")} differ from stock, size {Plan.StockSize} unchanged"
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
    public static string DefaultFolder(WriteGuard guard, string packagePath)
    {
        var relative = packagePath.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase) ? packagePath["/Game/".Length..] : packagePath.TrimStart('/');
        return Path.Combine(guard.EditsRoot, Path.Combine(relative.Split('/', StringSplitOptions.RemoveEmptyEntries)));
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

    /// <summary>The stock value of a stored field as CUE4Parse parsed it, or null when the field isn't stored.</summary>
    public double? StockValue(FieldKey key)
    {
        if (!key.Package.Equals(PackagePath, StringComparison.OrdinalIgnoreCase) || key.SlotExport < 0 || key.SlotExport >= _stockPackage.ExportsLazy.Length) return null;
        if (!_stockValues.TryGetValue(key.SlotExport, out var values))
            _stockValues[key.SlotExport] = values = WidgetTreeReader.StoredValues(_stockPackage.ExportsLazy[key.SlotExport].Value);
        return values.TryGetValue(key.Path, out var text) && TryParse(text, out var v) ? v : null;
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
            if (StockValue(c.Key) == null)
                return $"{SlotName(c.Key.SlotExport)}.{c.Key.Path} is not stored in {GamePaths.ShortName(PackagePath)}; only stored values can be patched.";
        }
        var next = Current.With(edit.Changes, StockValue);
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
    public BytePlan Plan() => BytePlanner.Plan(_stock, _layout, PackagePath, Current, StockValue, SlotName);

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

        var plan = BytePlanner.Plan(stock, _layout, PackagePath, Current, StockValue, SlotName);
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

        var changed = BytePlanner.Diff(stock, reloaded);
        var sha = Sha256(reloaded);
        _guard.Move(tmp, EditedPath);
        WriteManifest(plan, sha, changed);
        Saved = Current;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return new SaveResult(true, plan, [], EditedPath, sha, changed);
    }

    private static SaveResult Refused(BytePlan plan, List<PatchRefusal> refusals) => new(false, plan, refusals, null, null, []);

    /// <summary>
    /// Re-parses the written bytes with CUE4Parse and walks them again: in every edited slot export each stored value is
    /// unchanged from stock except the edited ones, which read back as the new values.
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
        foreach (var slot in plan.Entries.GroupBy(e => e.Key.SlotExport))
        {
            var edited = slot.ToDictionary(e => e.Key.Path, e => e.NewValue);
            var stockParsed = WidgetTreeReader.StoredValues(_stockPackage.ExportsLazy[slot.Key].Value);
            var nowParsed = WidgetTreeReader.StoredValues(reparsed.ExportsLazy[slot.Key].Value);
            if (!stockParsed.Keys.ToHashSet().SetEquals(nowParsed.Keys))
                refusals.Add(new PatchRefusal(RefusalRule.ReloadMismatch, $"{SlotName(slot.Key)}: the stored properties changed after reload"));
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
            foreach (var f in walker.Walk(written, layout.SpanOf(slot.Key)))
            {
                var want = edited.TryGetValue(f.Path, out var v) ? v : stockWalk.TryGetValue(f.Path, out var s) ? s.Value : double.NaN;
                if (f.Value != want || !stockWalk.TryGetValue(f.Path, out var sf) || sf.Offset != f.Offset)
                    refusals.Add(new PatchRefusal(RefusalRule.ReloadMismatch, $"{SlotName(slot.Key)}.{f.Path} at 0x{f.Offset:X} walks as {f.Value}, expected {want}"));
            }
        }
        return refusals;
    }

    // ---- manifest ----

    private sealed record ManifestEdit(int SlotExport, string Slot, string Path, double Stock, double Value);
    private sealed record ManifestEntry(string Slot, string Path, int Offset, string OffsetHex, string Type, double Old, double New, string OldHex, string NewHex, string ContextHex, int ContextMatches);
    private sealed record Manifest(string Package, string FileKey, string ChunkId, int StockSize, string StockSha256, string EditedSha256, DateTime SavedUtc,
        List<ManifestEdit> Edits, List<ManifestEntry> Plan, List<int> ChangedOffsets);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private void WriteManifest(BytePlan plan, string editedSha, IReadOnlyList<int> changed)
    {
        var manifest = new Manifest(PackagePath, FileKey, ChunkIdHex, StockSize, StockSha256, editedSha, DateTime.UtcNow,
            Current.Values.OrderBy(kv => kv.Key.SlotExport).ThenBy(kv => kv.Key.Path, StringComparer.Ordinal)
                .Select(kv => new ManifestEdit(kv.Key.SlotExport, SlotName(kv.Key.SlotExport), kv.Key.Path, StockValue(kv.Key) ?? double.NaN, kv.Value)).ToList(),
            plan.Entries.Select(e => new ManifestEntry(e.SlotName, e.Key.Path, e.Offset, e.OffsetHex, e.Type.ToString(), e.OldValue, e.NewValue, e.OldHex, e.NewHex, e.ContextHex, e.ContextMatches)).ToList(),
            changed.ToList());
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
        var edits = EditSet.Empty.With(m.Edits.Select(e => new FieldChange(new FieldKey(PackagePath, e.SlotExport, e.Path), e.Stock, e.Value)), StockValue);
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
