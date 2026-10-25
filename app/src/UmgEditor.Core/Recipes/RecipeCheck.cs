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
    /// <summary>New texture packages from Clones and the mip 0 each must decode to once mounted</summary>
    public List<(string PackagePath, byte[] Mip0)> CloneMips { get; } = [];
    /// <summary>Packages re-pointed by Repoints and the name-map path each must name once mounted</summary>
    public List<(string PackagePath, string Names)> RepointNames { get; } = [];
    /// <summary>The loose-file paks, built and read back</summary>
    public List<LoosePak> LoosePaks { get; } = [];
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
        (string Sha, string? Why) StockSha(string package)
        {
            try { return (Sha256(source.ReadPackageBytes(package).Bytes), null); }
            catch (Exception e) { return ("", $"{package} isn't in the game ({e.Message})"); }
        }

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

        // stored values, inserted tags, animation keys and ranges: one in-memory session per package (layer 1); then
        // the package's Layers, each by its own tool on the one before; then the union check against stock
        var allEdits = EditSet.Empty;
        var layered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renamedTo = new List<(string Head, string Package, string Names)>();
        foreach (var p in recipe.PackagesOrEmpty)
        {
            var head = GamePaths.ShortName(p.Package);
            byte[] stock;
            try { stock = source.ReadPackageBytes(p.Package).Bytes; }
            catch (Exception e) { Refuse($"{head}: not in the game ({e.Message})"); continue; }
            var stockSha = Sha256(stock);
            if (stockSha != p.StockSha256) { Refuse($"{head}: the stock chunk is sha256 {stockSha[..12]}…, the recipe was made on {p.StockSha256[..12]}… (game updated?)"); continue; }

            var session = PatchSession.InMemory(source, p.Package);
            var chunk = stock;
            var layers = new List<byte[]>();
            if (p.Values.Count > 0)
            {
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
                layers.Add(chunk = rebuilt.Chunk!);
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

            var swaps = new List<(ulong Old, ulong New)>();
            var failed = false;
            foreach (var (layer, n) in p.LayersOrEmpty.Select((l, i) => (l, i + (p.Values.Count > 0 ? 2 : 1))))
            {
                var what = $"{head}: layer {n} ({layer.Op})";
                LayerResult applied;
                // a tool that throws on the recipe's input refuses the layer (the recipe is wrong, not the check)
                try { applied = ApplyLayer(source, session.FileKey, chunk, layer); }
                catch (Exception e) { applied = new(null, "", [$"{e.GetType().Name}: {e.Message}"], []); }
                foreach (var r in applied.Refusals) Refuse($"{what}: {r}");
                if (applied.Chunk == null) { failed = true; break; }
                layers.Add(chunk = applied.Chunk);
                if (applied.Swap is { } swap) swaps.Add(swap);
                foreach (var to in applied.RenamedTo) renamedTo.Add((head, session.PackagePath, to));
                result.Log.Add($"{what}: {applied.What}; checked; chunk sha256 {Sha256(chunk)}");
                foreach (var l in applied.Log) result.Log.Add("    " + l);
            }
            if (failed) continue;
            if (p.LayersOrEmpty.Count > 0)
            {
                layered.Add(session.PackagePath);
                var (problems, changes) = LayerUnion.Check(source, session.FileKey, stock, layers);
                foreach (var problem in problems) Refuse($"{head}: union of the layers against stock: {problem}");
                if (problems.Count > 0) continue;
                result.Log.Add($"{head}: final re-parse against stock = the union of {layers.Count} layer(s) ({changes} values)");
            }
            result.Packages[session.PackagePath] = new ModPackage(session.PackagePath, session.FileKey, session.ChunkIdHex, chunk) { ImportedPackageSwaps = swaps };
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
            foreach (var p in textures.Packages.Where(p => layered.Contains(p.PackagePath)))
                Refuse($"{GamePaths.ShortName(p.PackagePath)}: a texture edit re-points a package with Layers; recipe format 1 doesn't layer them");
            result.Textures = textures;
            if (!result.Ok) return result;
            foreach (var l in textures.Log.Where(l => !l.StartsWith("    ", StringComparison.Ordinal))) result.Log.Add(l);
            foreach (var p in textures.All)
            {
                result.Packages[p.PackagePath] = p;
                result.Log.Add($"{GamePaths.ShortName(p.PackagePath)}: {(p.CloneOf != null ? "new texture package" : "re-pointed")}, chunk sha256 {Sha256(p.Chunk)}");
            }
        }

        // clones: a new texture package from one stock texture each (TextureClone's own checks: format, names, the
        // rename, the payloads, re-parse)
        foreach (var c in recipe.ClonesOrEmpty)
        {
            var what = $"clone {c.NewPath}";
            if (result.Packages.ContainsKey(c.NewPath)) { Refuse($"{what}: the recipe makes this package already"); continue; }
            var (stockSha, why) = StockSha(c.From);
            if (why != null) { Refuse($"{what}: {why}"); continue; }
            if (stockSha != c.StockSha256) { Refuse($"{what}: {GamePaths.ShortName(c.From)}'s stock chunk is sha256 {stockSha[..12]}…, the recipe was made on {c.StockSha256[..12]}… (game updated?)"); continue; }
            TextureClone.Result made;
            byte[]? mip0 = null;
            if (c.Solid is { } solid)
            {
                var (r, g, b) = RecipeReader.ParseSolid(solid);
                var pixels = TextureClone.Solid(r, g, b);
                // the largest mip asked for is mip 0
                made = TextureClone.Build(source, c.From, c.NewName, (w, h) => { var px = pixels(w, h); if (mip0 == null || px.Length > mip0.Length) mip0 = px; return px; });
            }
            else if (c.White)
                (made, mip0) = TextureEdits.CloneWhite(source, c.From, c.NewName);
            else
            {
                var file = Path.Combine(root, recipe.InputsOrEmpty[c.Input!].Path);
                TextureClone.Mip art;
                if (c.Width is { } w && c.Height is { } h)
                {
                    var bgra = File.ReadAllBytes(file);
                    if (bgra.Length != (long) w * h * 4) { Refuse($"{what}: input {c.Input} is {bgra.Length} bytes, not {w}x{h} BGRA ({(long) w * h * 4})"); continue; }
                    art = new TextureClone.Mip(w, h, bgra);
                }
                else
                {
                    var (image, bad) = PngReader.Read(file);
                    if (image == null) { Refuse($"{what}: input {c.Input}: {bad}"); continue; }
                    art = new TextureClone.Mip(image.Width, image.Height, image.Bgra);
                }
                made = TextureEdits.CloneImage(source, c.From, c.NewName, art);
                mip0 = art.Bgra;
            }
            foreach (var r in made.Refusals) Refuse($"{what}: {r}");
            if (made.Package == null || mip0 == null) continue;
            result.Packages[made.Package.PackagePath] = made.Package;
            result.CloneMips.Add((made.Package.PackagePath, mip0));
            result.Log.Add($"{what}: new texture package from {c.From} ({c.Solid ?? (c.White ? "white" : "input " + c.Input)}), chunk sha256 {Sha256(made.Package.Chunk)}");
            foreach (var l in made.Log) result.Log.Add("    " + l);
        }

        // re-points: a same-length soft path rename in a stock package, stored floats alongside (SoftPathRepoint:
        // BytePlanner for the values, NameRenamePatch's exact diff and re-parse)
        foreach (var p in recipe.RepointsOrEmpty)
        {
            var head = GamePaths.ShortName(p.Package);
            if (result.Packages.ContainsKey(p.Package)) { Refuse($"{head}: the recipe changes it already (Textures); recipe format 1 doesn't layer a re-point on that"); continue; }
            var (stockSha, why) = StockSha(p.Package);
            if (why != null) { Refuse($"{head}: {why}"); continue; }
            if (stockSha != p.StockSha256) { Refuse($"{head}: the stock chunk is sha256 {stockSha[..12]}…, the recipe was made on {p.StockSha256[..12]}… (game updated?)"); continue; }
            var toPackage = p.To[..p.To.LastIndexOf('.')];
            if (!result.Packages.ContainsKey(toPackage) && StockSha(toPackage).Why != null) { Refuse($"{head}: To names {toPackage}, which neither the recipe makes nor the game has"); continue; }

            var (fileKey, stock) = source.ReadPackageBytes(p.Package);
            var exports = source.ParsePackage(fileKey, stock).ExportsLazy.Select(e => e.Value).ToList();
            var before = result.Refusals.Count;
            foreach (var v in p.ValuesOrEmpty)
            {
                var what = $"{head}: {v.Export}.{v.Path}";
                var matching = exports.Where(e => e.Name == v.Export).ToList();
                if (matching.Count != 1) { Refuse($"{what}: {matching.Count} exports are named {v.Export}"); continue; }
                if (!WidgetTreeReader.StoredValues(matching[0]).TryGetValue(v.Path, out var text)
                    || !double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var stored))
                { Refuse($"{what}: stock doesn't store it (a re-point changes stored values only)"); continue; }
                if ((float) stored != (float) v.Stock) Refuse($"{what}: stock is {text}, the recipe says {v.Stock:R}");
            }
            if (result.Refusals.Count > before) continue;
            var repointed = SoftPathRepoint.Build(source, p.Package, p.From, p.To, p.ValuesOrEmpty.Count > 0 ? p.ValuesOrEmpty.Select(v => (v.Export, v.Path, v.Value)).ToList() : null);
            foreach (var r in repointed.Refusals) Refuse($"{head}: {r}");
            if (repointed.Package == null) continue;
            var (union, _) = LayerUnion.Check(source, fileKey, stock, [repointed.Package.Chunk]);
            foreach (var problem in union) Refuse($"{head}: re-parse against stock: {problem}");
            if (union.Count > 0) continue;
            result.Packages[repointed.Package.PackagePath] = repointed.Package;
            result.RepointNames.Add((repointed.Package.PackagePath, p.To));
            result.Log.Add($"{head}: re-pointed {p.From} → {p.To}{(p.ValuesOrEmpty.Count > 0 ? $", {p.ValuesOrEmpty.Count} stored value{(p.ValuesOrEmpty.Count == 1 ? "" : "s")}" : "")}; " +
                           $"exact diff and re-parse passed; chunk sha256 {Sha256(repointed.Package.Chunk)}");
            foreach (var l in repointed.Log) result.Log.Add("    " + l);
        }
        // a rename in a layer must point at a package the recipe makes or the game has, as a re-point must
        foreach (var (head, package, names) in renamedTo)
        {
            var toPackage = names[..names.LastIndexOf('.')];
            if (!result.Packages.ContainsKey(toPackage) && StockSha(toPackage).Why != null) Refuse($"{head}: a rename names {toPackage}, which neither the recipe makes nor the game has");
            else result.RepointNames.Add((package, names));
        }
        if (!result.Ok) return result;

        // loose-file paks: each file replaces a stock loose file; LegacyPak writes the pak (twice: the same bytes), its
        // reader parses it back (version, encrypted index, every SHA-1 and path hash) and must give exactly the files
        var aesKey = LegacyPak.KeyBytes(source.Config.AesKey);
        foreach (var pak in recipe.LoosePaksOrEmpty)
        {
            var what = $"pak {pak.Pak}";
            if (Directory.Exists(source.Config.PaksDir) && File.Exists(Path.Combine(source.Config.PaksDir, pak.Pak))) { Refuse($"{what}: a stock file has that name"); continue; }
            var folder = pak.MountPoint["../../../".Length..];
            var pakFiles = new List<PakFile>();
            foreach (var f in pak.Files)
            {
                if (!source.Provider.Files.TryGetValue(folder + f.Path, out var stockEntry) || stockEntry is not CUE4Parse.UE4.Pak.Objects.FPakEntry)
                { Refuse($"{what}: {folder + f.Path} isn't a loose file of a stock pak (a loose-file pak only replaces those)"); continue; }
                pakFiles.Add(new PakFile(f.Path, File.ReadAllBytes(Path.Combine(root, recipe.InputsOrEmpty[f.Input].Path))));
            }
            if (pakFiles.Count != pak.Files.Count) continue;
            var contents = new PakContents(pak.MountPoint, LegacyPak.SeedFor(pak.Pak), ["", "", "", "", ""], pakFiles);
            var bytes = LegacyPak.Write(contents, aesKey);
            if (!bytes.AsSpan().SequenceEqual(LegacyPak.Write(contents, aesKey))) { Refuse($"{what}: two builds differ"); continue; }
            var (back, problems) = LegacyPak.Read(bytes, aesKey);
            foreach (var problem in problems) Refuse($"{what}: read back: {problem}");
            if (back == null || problems.Count > 0) continue;
            if (back.MountPoint != pak.MountPoint || back.Files.Count != pakFiles.Count
                || !back.Files.Zip(pakFiles).All(x => x.First.Path == x.Second.Path && x.First.ZlibBlock == null && x.First.Data.AsSpan().SequenceEqual(x.Second.Data)))
            { Refuse($"{what}: read back, its mount point or pakFiles aren't the ones written"); continue; }
            result.LoosePaks.Add(new LoosePak(pak.Pak, bytes, folder, pakFiles));
            result.Log.Add($"{what}: {pakFiles.Count} file(s) under {folder} ({string.Join(", ", pakFiles.Select(f => $"{f.Path} {f.Data.Length:N0} B"))}), {bytes.Length:N0} B, " +
                           $"read back (v{LegacyPak.Version}, encrypted index, hashes); sha256 {Sha256(bytes)}");
        }
        if (!result.Ok) return result;

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

    /// <summary>One layer's outcome: the new chunk (null when refused), what it did, a class swap's imported package swap, the soft paths a rename now names.</summary>
    private sealed record LayerResult(byte[]? Chunk, string What, IReadOnlyList<string> Refusals, IReadOnlyList<string> Log,
        (ulong Old, ulong New)? Swap = null, IReadOnlyList<string>? Renamed = null)
    {
        public IReadOnlyList<string> RenamedTo => Renamed ?? [];
    }

    /// <summary>A layer through its own tool, on <paramref name="chunk"/> (each tool checks itself: exact diff, re-parse, walk).</summary>
    private static LayerResult ApplyLayer(GameSource source, string fileKey, byte[] chunk, RecipeLayer layer)
    {
        switch (layer)
        {
            case RenameLayer l:
            {
                var parsed = source.ParsePackage(fileKey, chunk);
                var names = parsed.ExportsLazy.Select(e => e.Value.Name).ToList();
                var layout = ZenPackageLayout.Read(chunk, parsed);
                var writes = new List<(int Offset, byte[] Bytes, string What)>();
                var expected = new Dictionary<(int Export, string Path), string>();
                foreach (var x in l.EnumSwapsOrEmpty)
                {
                    if (names.Count(n => n == x.Export) != 1) return new(null, "", [$"{names.Count(n => n == x.Export)} exports are named {x.Export}"], []);
                    var index = names.IndexOf(x.Export);
                    var (at, bytes, why) = EnumValueSwap.Plan(chunk, layout, index, x.Property, x.From, x.To);
                    if (why != null) return new(null, "", [$"{x.Export}.{x.Property} {x.From} → {x.To}: {why}"], []);
                    writes.Add((at, bytes, $"{x.Export}.{x.Property} → {x.To}"));
                    expected[(index, "Properties." + x.Property)] = x.To;
                }
                var r = NameRenamePatch.Apply(source, fileKey, chunk, l.RenamesOrEmpty.Select(x => (x.From, x.To)).ToList(), writes, expected);
                return new(r.Chunk, $"{l.RenamesOrEmpty.Count} rename(s), {l.EnumSwapsOrEmpty.Count} enum swap(s)", r.Refusals, r.Log,
                    Renamed: l.RenamesOrEmpty.Select(x => x.To).ToList());
            }
            case RemoveTagLayer l:
            {
                var r = TagRemover.Remove(source, fileKey, chunk, l.Export, l.Tag, l.Stock);
                return new(r.Chunk, $"{l.Export}.{l.Tag} ({l.Stock}) removed", r.Refusals, r.Log);
            }
            case ClassSwapLayer l:
            {
                try { _ = source.ReadPackageBytes(l.To); }
                catch (Exception e) { return new(null, "", [$"the class package {l.To} isn't in the game ({e.Message})"], []); }
                var r = ClassSwap.Swap(source, fileKey, chunk, l.Export, l.To);
                return new(r.Chunk, $"{l.Export}'s class → {GamePaths.ShortName(l.To)}_C", r.Refusals, r.Log, r.Chunk != null ? r.PackageSwap : null);
            }
            case ColorRowsLayer l:
            {
                var writes = new List<(int Offset, byte[] Bytes, string What)>();
                var expected = new Dictionary<(int Export, string Path), string>();
                var parsed = source.ParsePackage(fileKey, chunk);
                if (parsed.ExportsLazy.Length != 1) return new(null, "", ["not a package with one DataTable export"], []);
                var flat = TagInserter.Flatten(parsed.ExportsLazy[0].Value);
                foreach (var row in l.Rows)
                {
                    var (plan, why) = DataTableColorEdit.Plan(source, fileKey, chunk, row.Row, RecipeReader.ParseRgba(row.Color));
                    if (plan == null) return new(null, "", [$"{row.Row}: {why}"], []);
                    var (sr, sg, sb, sa) = RecipeReader.ParseRgba(row.Stock);
                    string? C(string c) => flat.GetValueOrDefault(plan.Key + "." + c);
                    if (C("R") != sr.ToString() || C("G") != sg.ToString() || C("B") != sb.ToString() || C("A") != sa.ToString())
                        return new(null, "", [$"{row.Row}: stock is R {C("R")} G {C("G")} B {C("B")} A {C("A")}, the recipe says {row.Stock}"], []);
                    writes.Add((plan.Offset, plan.Bgra, plan.What));
                    foreach (var kv in plan.Expected) expected[kv.Key] = kv.Value;
                }
                var r = NameRenamePatch.Apply(source, fileKey, chunk, [], writes, expected);
                return new(r.Chunk, $"{l.Rows.Count} colour row(s)", r.Refusals, r.Log);
            }
            case AppendNamesLayer l:
            {
                var r = EnumTagInsert.AppendNames(source, fileKey, chunk, l.Names);
                return new(r.Chunk, $"{l.Names.Count} name(s) appended", r.Refusals, r.Log);
            }
            case NameTagLayer l:
            {
                // the stock value first: stored or not, as the recipe says
                var export = source.ParsePackage(fileKey, chunk).ExportsLazy.Select(e => e.Value).Where(e => e.Name == l.Export).ToList();
                if (export.Count != 1) return new(null, "", [$"{export.Count} exports are named {l.Export}"], []);
                var stored = TagInserter.Flatten(export[0]).GetValueOrDefault($"Properties.{l.Struct}.{l.Member}")?.Trim('"');
                if (stored != l.Stock) return new(null, "", [$"{l.Export}.{l.Struct}.{l.Member} is {stored ?? "not stored"}, the recipe says {l.Stock ?? "not stored"}"], []);
                if (l.Stock == null)
                {
                    var a = l.Anchor!;
                    if (NameTagEdit.CheckAgainst(source, a.Package, a.Export, l.Struct, l.Member, a.Value) is { } bad)
                        return new(null, "", [$"the {l.Member} tag builder doesn't reproduce stock's ({GamePaths.ShortName(a.Package)} {a.Export}.{l.Struct}): {bad}"], []);
                    var r = NameTagEdit.Insert(source, fileKey, chunk, l.Export, l.Struct, l.Member, l.Value, NameTagEdit.SlateFontInfoOrder);
                    return new(r.Chunk, $"{l.Export}.{l.Struct}.{l.Member} = {l.Value} inserted", r.Refusals, r.Log);
                }
                var swap = NameTagEdit.Swap(source, fileKey, chunk, l.Export, l.Struct, l.Member, l.Stock, l.Value);
                return new(swap.Chunk, $"{l.Export}.{l.Struct}.{l.Member} {l.Stock} → {l.Value} in place", swap.Refusals, swap.Log);
            }
            case FontFaceLayer l:
            {
                var r = FontFaceRepoint.Plan(source, fileKey, chunk, l.Entry, l.Name, l.From, l.To);
                return new(r.Chunk, $"{l.Entry} ({l.Name}) {l.From} → {l.To}", r.Refusals, r.Log);
            }
            case EnumInsertLayer l:
            {
                // the tag builder first reproduces a stock tag of the same kind, byte for byte
                if (EnumTagInsert.CheckAgainst(source, l.Anchor.Package, l.Anchor.Export, l.Tag, l.Type, l.Enum, l.Value) is { } bad)
                    return new(null, "", [$"the {l.Tag} tag builder doesn't reproduce stock's ({GamePaths.ShortName(l.Anchor.Package)} {l.Anchor.Export}): {bad}"], []);
                var r = EnumTagInsert.Insert(source, fileKey, chunk, l.Export, l.After, l.Tag, l.Type, l.Enum, l.Value);
                return new(r.Chunk, $"{l.Export}.{l.Tag} = {l.Value} inserted after {l.After}", r.Refusals, r.Log);
            }
            default:
                return new(null, "", [$"unknown layer {layer.GetType().Name}"], []);
        }
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
