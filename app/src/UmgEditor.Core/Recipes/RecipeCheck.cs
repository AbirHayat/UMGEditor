using System.Security.Cryptography;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using UmgEditor.Core.Textures;

namespace UmgEditor.Core.Recipes;

/// <summary>What a recipe check needs besides the game: texture usage, and the layout for the impact warnings.</summary>
/// <param name="Root">The workspace root: inputs are relative to it and must stay inside it</param>
public sealed record RecipeContext(GameSource Source, string Root, Func<TextureUsage> TextureUsage, UsageGraph Usage, EditImpact Impact,
    Func<string, WidgetPackageTree> LoadTree, int ViewportWidth = 1786, int ViewportHeight = 1000)
{
    /// <summary>The workspace root for a config: the folder that holds WorkDir.</summary>
    public static string RootOf(GameConfig config) => Path.GetDirectoryName(Path.GetFullPath(config.WorkDir.TrimEnd('\\', '/')))!;
}

/// <summary>The outcome of <see cref="RecipeCheck.Run"/>: the chunks the recipe makes, or why not.</summary>
public sealed class RecipeCheckResult
{
    public List<string> Refusals { get; } = [];
    public List<string> Log { get; } = [];
    /// <summary>Every package the recipe would ship (edited stock packages, re-pointed ones, new texture clones)</summary>
    public Dictionary<string, ModPackage> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, byte[]> Chunks => Packages.ToDictionary(kv => kv.Key, kv => kv.Value.Chunk, StringComparer.OrdinalIgnoreCase);
    /// <summary>The value edits of every package together (what the layout warnings and predictions are made from)</summary>
    public EditSet Edits { get; set; } = EditSet.Empty;
    /// <summary>The instructions' rows for the value edits</summary>
    public List<ExportEdit> ExportEdits { get; } = [];
    /// <summary>The texture edits' plan, when the recipe has any</summary>
    public TexturePlan? Textures { get; set; }
    public List<ExportWarning> Warnings { get; } = [];
    /// <summary>Danger warnings: a build needs --ack-danger for these (a recipe can't acknowledge them)</summary>
    public IReadOnlyList<ExportWarning> Danger => Warnings.Where(w => w.IsDanger).ToList();
    /// <summary>Expect.Chunks was given and every chunk matches it</summary>
    public bool ChunksPinned { get; set; }
    public bool Ok => Refusals.Count == 0;
}

/// <summary>
/// <c>umgmod recipe-check</c>: everything short of packing, read only. The data (<see cref="RecipeReader.Validate"/>),
/// then every input by sha256, every package's stock chunk by sha256, each value against the stock value it names, the
/// edits built and verified in memory (<see cref="PatchSession.Rebuild"/>: exact diff, re-parse), texture edits planned
/// on those chunks (<see cref="TextureEdits.Plan"/>: clones, re-points, their own checks), the layout warnings, and
/// Expect.Chunks. Writes nothing (only the shared caches under work\cache may be built).
/// </summary>
public static class RecipeCheck
{
    public static RecipeCheckResult Run(Recipe recipe, RecipeContext context)
    {
        var result = new RecipeCheckResult();
        void Refuse(string why) => result.Refusals.Add(why);
        var source = context.Source;

        foreach (var why in RecipeReader.Validate(recipe)) Refuse(why);
        if (!result.Ok) return result;

        // inputs: inside the workspace, plain files, the pinned bytes
        var root = Path.GetFullPath(context.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var (id, input) in recipe.InputsOrEmpty)
        {
            var full = Path.GetFullPath(Path.Combine(root, input.Path));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { Refuse($"input {id}: {input.Path} is outside the workspace"); continue; }
            if (!File.Exists(full)) { Refuse($"input {id}: {input.Path} is missing"); continue; }
            if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint)) { Refuse($"input {id}: {input.Path} is a link; give the file itself"); continue; }
            var sha = Sha256(File.ReadAllBytes(full));
            if (sha != input.Sha256) { Refuse($"input {id}: {input.Path} is sha256 {sha[..12]}…, the recipe pins {input.Sha256[..12]}…"); continue; }
            result.Log.Add($"input {id}: {input.Path} sha256 {sha[..12]}… as pinned");
        }

        // the container must not be a stock one
        if (Directory.Exists(source.Config.PaksDir) && Directory.EnumerateFiles(source.Config.PaksDir, recipe.Container + ".*").Any())
            Refuse($"Container {recipe.Container} is a stock container name");

        // stored values, inserted tags, animation keys and ranges: one in-memory session per package
        var allEdits = EditSet.Empty;
        foreach (var p in recipe.PackagesOrEmpty)
        {
            var head = GamePaths.ShortName(p.Package);
            byte[] stock;
            try { stock = source.ReadPackageBytes(p.Package).Bytes; }
            catch (Exception e) { Refuse($"{head}: not in the game ({e.Message})"); continue; }
            var stockSha = Sha256(stock);
            if (stockSha != p.StockSha256) { Refuse($"{head}: the stock chunk is sha256 {stockSha[..12]}…, the recipe was made on {p.StockSha256[..12]}… (game updated?)"); continue; }

            var session = PatchSession.InMemory(source, p.Package);
            var before = result.Refusals.Count;
            var changes = new List<FieldChange>();
            foreach (var v in p.Values)
            {
                var what = $"{head}: {v.Slot}.{v.Path}";
                var key = new FieldKey(session.PackagePath, v.Export, v.Path);
                if (session.SlotName(v.Export) is var name && name != v.Slot) { Refuse($"{what}: export {v.Export} is {name}, not {v.Slot}"); continue; }
                var stored = session.StockValue(key);
                if (session.BaseValue(key) is not { } baseValue) { Refuse($"{what}: not stored in stock and no engine default is known"); continue; }
                if ((float) baseValue != (float) v.Stock) { Refuse($"{what}: stock is {baseValue:R}, the recipe says {v.Stock:R}"); continue; }
                if (v.Inserted != (stored == null)) { Refuse($"{what}: Inserted is {v.Inserted.ToString().ToLowerInvariant()}, but stock {(stored == null ? "doesn't store it (the edit adds a tag)" : "stores it")}"); continue; }
                changes.Add(new FieldChange(key, v.Stock, v.Value));
            }
            if (result.Refusals.Count > before) continue;
            if (session.Apply(EditResult.Of(changes)) is { } refused) { Refuse($"{head}: {refused}"); continue; }
            if (session.Current.Count != p.Values.Count) { Refuse($"{head}: {p.Values.Count} values give {session.Current.Count} edits"); continue; }

            var rebuilt = session.Rebuild();
            foreach (var r in rebuilt.Refusals) Refuse($"{head}: [{r.Rule}] {r.Message}");
            if (!rebuilt.Ok) continue;
            var plan = rebuilt.Plan;
            result.Packages[session.PackagePath] = new ModPackage(session.PackagePath, session.FileKey, session.ChunkIdHex, rebuilt.Chunk!);
            allEdits = allEdits.With(session.Current.Values.Select(kv => new FieldChange(kv.Key, 0, kv.Value)), _ => null);
            var widgets = WidgetNames(context, session.PackagePath);
            result.ExportEdits.AddRange(ExportEdit.Of(plan, (export, slot) => widgets.TryGetValue(export, out var w) ? $"{w} ({slot})" : slot, session.BaseValue));
            result.Log.Add($"{head}: {plan.Entries.Count} in place, {plan.Insertions.Count} new tag{(plan.Insertions.Count == 1 ? "" : "s")}, " +
                           $"{plan.AnimKeyCount} animation edit{(plan.AnimKeyCount == 1 ? "" : "s")} ({plan.AnimWrites.Count} writes); {plan.SizeText}; " +
                           $"exact diff and re-parse passed; chunk sha256 {Sha256(rebuilt.Chunk!)}");
            foreach (var e in plan.Entries) result.Log.Add($"    {e.SlotName}.{e.Key.Path} {e.OldValue:R} → {e.NewValue:R} @{e.OffsetHex} (±{e.Window}, {e.ContextMatches}×)");
            foreach (var i in plan.Insertions) result.Log.Add($"    {i.SlotName}.{i.Path} = {i.Value:R} as a new tag @{i.InsertAtHex} before {i.Before}");
            foreach (var w in plan.AnimWrites) result.Log.Add($"    {w.Description} ({w.Copy}) {w.OldValue:R} → {w.NewValue:R} @{w.OffsetHex}, {w.WindowText}");
        }
        if (!result.Ok) return result;

        // texture edits, on the chunks above where a package has both
        if (recipe.TexturesOrEmpty.Count > 0)
        {
            var inputs = recipe.InputsOrEmpty;
            var edits = recipe.TexturesOrEmpty.Select(t => new TextureEdit(t.Package, t.Widget, t.Texture, t.Mode, t.NewName, t.Scope,
                t.Input is { } id ? inputs[id].Path : null, t.Input is { } id2 ? inputs[id2].Sha256 : null, t.Source, t.MarginsZero)).ToList();
            var valueChunks = result.Chunks;
            var textures = TextureEdits.Plan(source, context.TextureUsage(), root, edits, p => valueChunks.GetValueOrDefault(p), p => context.Usage.AllUsers(p));
            foreach (var r in textures.Refusals) Refuse(r);
            result.Textures = textures;
            if (!result.Ok) return result;
            foreach (var l in textures.Log.Where(l => !l.StartsWith("    ", StringComparison.Ordinal))) result.Log.Add(l);
            foreach (var p in textures.All)
            {
                result.Packages[p.PackagePath] = p;
                result.Log.Add($"{GamePaths.ShortName(p.PackagePath)}: {(p.CloneOf != null ? "new texture package" : "re-pointed")}, chunk sha256 {Sha256(p.Chunk)}");
            }
        }

        // layout warnings of the value edits (as the export dialog and umgmod build compute them)
        result.Edits = allEdits;
        if (allEdits.Count > 0)
        {
            var impact = ImpactReport.Build(recipe.Name, context.ViewportWidth, context.ViewportHeight, context.Impact.Predict(allEdits, context.LoadTree));
            result.Warnings.AddRange(ExportWarning.From(impact));
        }

        // pins
        if (recipe.Expect?.Chunks is { } pinned)
        {
            var bad = 0;
            foreach (var (package, sha) in pinned)
                if (!result.Chunks.TryGetValue(package, out var chunk)) { Refuse($"Expect.Chunks: {package} isn't made by the recipe"); bad++; }
                else if (Sha256(chunk) != sha) { Refuse($"Expect.Chunks: {GamePaths.ShortName(package)} is sha256 {Sha256(chunk)[..12]}…, pinned {sha[..12]}…"); bad++; }
            foreach (var package in result.Chunks.Keys.Where(k => !pinned.Keys.Contains(k, StringComparer.OrdinalIgnoreCase)))
            { Refuse($"Expect.Chunks: {package} is made but not pinned"); bad++; }
            result.ChunksPinned = bad == 0;
            if (bad == 0) result.Log.Add($"Expect.Chunks: all {pinned.Count} chunks as pinned");
        }
        if (recipe.Expect?.Files is { Count: > 0 } files)
            result.Log.Add($"Expect.Files: {files.Count} device file{(files.Count == 1 ? "" : "s")} pinned; checked when the recipe is built");
        return result;
    }

    /// <summary>Slot export → the widget it holds (for the instructions); empty when the tree doesn't load.</summary>
    private static Dictionary<int, string> WidgetNames(RecipeContext context, string package)
    {
        try
        {
            return context.LoadTree(package).All().Where(n => n.Slot != null && !n.IsReadOnly)
                .GroupBy(n => n.Slot!.ExportIndex).ToDictionary(g => g.Key, g => g.First().Name);
        }
        catch (Exception) { return []; }
    }

    public static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
