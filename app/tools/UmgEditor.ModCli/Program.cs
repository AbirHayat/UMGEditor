// umgmod: make and pack edits from the command line (no UI), for device canaries before the editor UI exists.
//   umgmod [--set NAME] edit  <package> <widget> set <field> <value> [--yes]
//   umgmod [--set NAME] edit  <package> <widget> move <dx> <dy> [--yes]
//   umgmod [--set NAME] edit  <package> <widget> resize <Left|Top|Right|Bottom> <d> [--yes]
//   umgmod [--set NAME] show  <package>
//   umgmod [--set NAME] reset <package>
//   umgmod [--set NAME] build <modName> <package>... [--container pc9999_mobile_and_P] [--ack-danger]
//   umgmod [--set NAME] predict <package>...
//   umgmod show-anim <package> [--anim REGEX] [--widget NAME] [--bytes]
//   umgmod [--set NAME] anim-flatten <package> <anim,anim...> [--parts width,visibility,positions] [--normal-from Info_Hide] [--check Show] [--yes]
//   umgmod [--set NAME] anim-key <package> <anim> <widget> <channel> <key#> <value|frame|interp> <new> [--yes]
//   umgmod [--set NAME] anim-end <package> <anim> <oldEndTicks> <newEndTicks> [--yes]
//   umgmod tex-info <package> <widget>          what a uiImage shows, every widget naming that texture, where it's shown
//   umgmod tex-users <texture>                  every widget naming a texture (path or unique suffix)
//   umgmod tex-sources <package> <widget>       B8G8R8A8 textures a clone can come from (best first)
//   umgmod stock-chunk <package> <out.bin>      a stock package's raw chunk (read-only on the game)
//   umgmod tex-coverage [--folder SUBSTR] [--list] plans a white copy for every uiImage (read-only): what can't be replaced and why
//   umgmod tex-preview <package> <widget> <image.png> <out.png> [--in SCREEN] [--margins0] [--scope widget|package|all]
//   umgmod [--set NAME] tex <package> <widget> <image.png> [--name NEW] [--source TEXTURE] [--margins0] [--scope widget|package|all] [--yes]
//   umgmod [--set NAME] tex-white <package> <widget> [--name NEW] [--scope widget|package|all] [--yes]
//   umgmod [--set NAME] tex-show
//   umgmod [--set NAME] tex-reset <package> <widget>
//   umgmod recipe-check <recipe.json> [--ack-danger]   a recipe checked and built in memory (read only): exit 0 ok, 2 refused, 3 Danger
//   umgmod recipe-build <recipe.json> [--name MOD] [--ack-danger]   check, pack, verify, pins: exit 0 / 2 / 3 / 4 = device files aren't the pinned ones
//   umgmod [--set NAME] recipe-from-set [--name RECIPE] [--container C]   the set's saved edits + texture edits as work\out\recipes\<name>.json
//   umgmod disable-script <mod>   writes work\out\mods\<mod>\DISABLE.ps1 (recipe-build writes it too) and prints the files to delete on the phone
// <package> is a /Game/... path or a unique suffix such as MatchTime/MatchTimeBase. Edits are saved PatchSessions in
// work\edits[\NAME]\<package path>; build packs their saved chunks into work\out\mods\<modName>\mod (the export
// dialog's plan and run: predictions\ and TEST_INSTRUCTIONS.md next to it). Exit 0 = done, 2 = refused, 3 = needs
// --yes (first edit of a package: read the warnings first) or --ack-danger (build: a Danger layout warning).
using System.Globalization;
using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Layout;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using UmgEditor.Core.Recipes;
using UmgEditor.Core.Textures;

const int W = 1786, H = 1000; // the validated LDPlayer game view

Console.OutputEncoding = System.Text.Encoding.UTF8;
var argv = args.ToList();
string? Option(string name)
{
    var i = argv.IndexOf(name);
    if (i < 0) return null;
    if (i + 1 >= argv.Count) { Console.Error.WriteLine($"{name} needs a value"); Environment.Exit(1); }
    var v = argv[i + 1];
    argv.RemoveRange(i, 2);
    return v;
}
bool Flag(string name) => argv.Remove(name);

var editSet = Option("--set");
var container = Option("--container") ?? "pc9999_mobile_and_P";
var yes = Flag("--yes");
var ackDanger = Flag(BuildCommand.AckFlag);
var animFilter = Option("--anim") is { } re ? new System.Text.RegularExpressions.Regex(re) : null;
var widgetFilter = Option("--widget");
var withBytes = Flag("--bytes");
var parts = Option("--parts");
var normalFrom = Option("--normal-from") ?? "Info_Hide";
var crossCheck = Option("--check") ?? "Show";
var newName = Option("--name");
var cloneSource = Option("--source");
var screenArg = Option("--in");
var marginsZero = Flag("--margins0");
var allUsers = Flag("--all-users"); // = --scope all
var scopeArg = Option("--scope");
var folderFilter = Option("--folder");
var listAll = Flag("--list");
TextureEditScope scope;
switch (allUsers ? "all" : scopeArg?.ToLowerInvariant() ?? "widget")
{
    case "widget": scope = TextureEditScope.Widget; break;
    case "package": scope = TextureEditScope.Package; break;
    case "all": scope = TextureEditScope.AllUsers; break;
    default: Console.Error.WriteLine($"--scope {scopeArg}: use widget, package or all"); return 1;
}
if (argv.Count == 0 || argv.Count < 2 && argv[0] is not ("tex-show" or "tex-coverage" or "recipe-from-set")) return Usage();

var config = GameConfig.LoadOrDefault(Path.Combine(AppContext.BaseDirectory, "umgeditor.json"));
using var source = GameSource.Open(config);
var index = UiAssetIndex.LoadOrBuild(source);
var guard = new WriteGuard(config);

string ResolvePackage(string arg)
{
    if (arg.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) return arg;
    var hits = index.Where(e => e.Kind == UiAssetKind.Widget)
        .Select(e => GamePaths.ToPackagePath(e.Path))
        .Where(p => p.EndsWith("/" + arg.Trim('/'), StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
    if (hits.Count == 1) return hits[0];
    throw new ArgumentException(hits.Count == 0 ? $"no widget package ends with \"{arg}\"" : $"\"{arg}\" is ambiguous: {string.Join(", ", hits.Take(5))}");
}

PatchSession OpenSession(string package)
{
    var s = PatchSession.Open(source, package, PatchSession.DefaultFolder(guard, package, editSet));
    if (s.OpenNote != null) Console.WriteLine($"note: {s.OpenNote}");
    return s;
}

void PrintPlan(PatchSession s)
{
    Console.WriteLine($"{s.PackagePath}  ({s.FileKey}, chunk {s.ChunkIdHex}, {s.StockSize} bytes)");
    Console.WriteLine($"  folder {s.Folder}");
    Console.WriteLine($"  {s.Current.Count} edit{(s.Current.Count == 1 ? "" : "s")}, {(s.IsDirty ? "UNSAVED changes" : "saved")}");
    var plan = s.Plan();
    foreach (var e in plan.Entries)
        Console.WriteLine($"  {e.SlotName}.{e.Key.Path}  @ {e.OffsetHex}  {Num(e.OldValue)} -> {Num(e.NewValue)}  [{e.OldHex}] -> [{e.NewHex}]\n      context x{e.ContextMatches}{(e.ContextMatches == 1 ? "" : $", unique stock window ±{e.Window}")}: {e.ContextHex}");
    foreach (var i in plan.Insertions)
        Console.WriteLine($"  {i.SlotName}.{i.Path}  NEW TAG before {i.Before} @ {i.InsertAtHex}  {Num(i.Key is { } k ? s.BaseValue(k) ?? 0 : 0)} (default) -> {Num(i.Value)}  [{i.TagHex}]\n      context x{i.ContextMatches}{(i.ContextMatches == 1 ? "" : $", unique stock window ±{i.Window}")}");
    if (plan.ChangesSize) Console.WriteLine($"  {plan.SizeText}; {PackageBytes.HeaderChangesOf(plan)}");
    if (plan.AnimWrites.Count > 0)
        Console.WriteLine($"  {PackageBytes.AnimCounts(plan.AnimWrites)} (location proof: smallest unique stock window, limit ±{BytePlanner.MaxAnimWindow}):");
    foreach (var w in plan.AnimWrites)
        Console.WriteLine($"  {w.OffsetHex,-9} {w.Copy,-8} {w.Description}  {Num(w.OldValue)} -> {Num(w.NewValue)}  [{w.OldHex}] -> [{w.NewHex}]  {w.WindowText}");
    foreach (var r in plan.Refusals) Console.WriteLine($"  REFUSED [{r.Rule}] {r.Message}");
}

LayoutEngine NewEngine()
{
    var textures = new TextureCache(source, new AssetRipperTextureDecoder());
    var embedded = new Dictionary<string, WidgetPackageTree?>(StringComparer.OrdinalIgnoreCase);
    return new LayoutEngine(UiScaleSettings.Load(source),
        p => embedded.TryGetValue(p, out var t) ? t : embedded[p] = Try(() => WidgetTreeReader.Build(source, p, readOnly: true)),
        t => textures.Get(t) is { IsPlaceholder: false } img ? (img.Width, img.Height) : null);
}

static string Num(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

EditorController Controller() =>
    new(source, new UsageGraph(index), NewEngine(), p => WidgetTreeReader.Build(source, p), W, H, editSet ?? EditorController.DefaultProject,
        () => TextureUsage.LoadOrBuild(source, index));

string ResolveTexture(string arg)
{
    if (arg.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) return GamePaths.PackageOf(arg);
    var hits = index.Where(e => e.Kind == UiAssetKind.Texture).Select(e => GamePaths.ToPackagePath(e.Path))
        .Where(p => p.EndsWith("/" + arg.Trim('/'), StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
    // the widgets name the Tex720 copy; its 0164F theme copy matches the same suffix
    if (hits.Count > 1 && !arg.Contains("0164F")) hits = hits.Where(p => !p.Contains("/0164F/")).ToList();
    if (hits.Count == 1) return hits[0];
    throw new ArgumentException(hits.Count == 0 ? $"no texture package ends with \"{arg}\"" : $"\"{arg}\" is ambiguous: {string.Join(", ", hits.Take(5))}");
}

void PrintUsers(EditorController c, string texture, string? package = null, string? widget = null)
{
    var users = c.TextureUsers(texture);
    Console.WriteLine($"named by {users.Count} widget{(users.Count == 1 ? "" : "s")}:");
    foreach (var u in users)
    {
        var shown = c.EmbeddedBy(u.Package);
        var online = u.Package.Contains("/MatchTimeConnection/", StringComparison.OrdinalIgnoreCase) ? " [online]" : "";
        var mark = u.Package == package && u.Export == widget ? "  <- this one" : "";
        Console.WriteLine($"  {GamePaths.ShortName(u.Package)}/{u.Export} ({u.ExportClass}.{u.Property}){online}{mark}" +
                          (shown.Count > 0 ? $"\n      shown in {shown.Count}: {string.Join(", ", shown.Select(GamePaths.ShortName).Order(StringComparer.OrdinalIgnoreCase))}" : ""));
    }
}

void PrintTexturePlan(TexturePlan plan)
{
    foreach (var i in plan.Items)
    {
        var e = i.Edit;
        Console.WriteLine($"{GamePaths.ShortName(e.Package)}/{e.Widget}: {GamePaths.ShortName(e.Texture)} {i.StockWidth}x{i.StockHeight} -> {e.NewName} {i.Width}x{i.Height}" +
                          $"{(e.Mode == TextureEditMode.White ? " (white)" : "")}{(e.MarginsZero ? ", margins 0" : "")}{(e.Source != null ? $", cloned from {GamePaths.ShortName(e.Source)}" : "")}");
        Console.WriteLine($"  path {i.From}\n    -> {i.To}");
        Console.WriteLine($"  scope {e.Scope}; {(i.Pointed.Count == 0 ? "path renamed in place" : $"path appended + pointed in {string.Join(", ", i.Pointed.Select(GamePaths.ShortName))}")}" +
                          (i.Following.Count > 0 ? $"; changes with it: {string.Join(", ", i.Following.Select(f => f.Export))}" : ""));
        Console.WriteLine($"  new packages: {string.Join(", ", i.ClonePaths)}");
        Console.WriteLine($"  re-pointed: {string.Join(", ", i.Storing.Select(GamePaths.ShortName))}");
        foreach (var w in i.Warnings) Console.WriteLine($"  [Info] {w}");
    }
    foreach (var p in plan.Packages) Console.WriteLine($"  package {GamePaths.ShortName(p.PackagePath)}: {p.Chunk.Length:N0} bytes, chunk {p.ChunkIdHex}");
    foreach (var p in plan.Clones) Console.WriteLine($"  clone {p.PackagePath[(p.PackagePath.IndexOf("/Widget/", StringComparison.Ordinal) + 8)..]}: {p.Chunk.Length:N0} bytes, chunk {p.ChunkIdHex}");
    foreach (var r in plan.Refusals) Console.WriteLine($"  REFUSED {r}");
}

int TextureEdit(TextureEditMode mode, string packageArg, string widget, string? png)
{
    var c = Controller();
    var package = ResolvePackage(packageArg);
    var (edit, image, why) = c.PrepareTextureEdit(package, widget, mode, png, newName, cloneSource is { } src ? ResolveTexture(src) : null, marginsZero, scope);
    if (edit == null) { Console.Error.WriteLine($"refused: {why}"); return 2; }
    if (newName == null) Console.WriteLine($"name: {edit.NewName} (suggested; --name to pick another of the same length)");
    if (image != null) Console.WriteLine($"image: {png} ({image.Format}, {image.Width}x{image.Height}) -> {edit.Image}");
    var outcome = c.ApplyTextureEdit(edit, yes);
    if (outcome.Refusal != null) { Console.Error.WriteLine($"refused:\n{outcome.Refusal}"); return 2; }
    if (outcome.NeedsConfirmation)
    {
        Console.WriteLine(outcome.ConfirmationText);
        Console.WriteLine($"First edit of {GamePaths.ShortName(package)}{(editSet != null ? $" in set {editSet}" : "")}: read the above, then re-run with --yes.");
        return 3;
    }
    c.SaveTextureEdits();
    Console.WriteLine($"saved {Path.Combine(c.ProjectFolder, TextureEditStore.FileName)}");
    var plan = c.PlanTextures();
    PrintTexturePlan(plan);
    return plan.Ok ? 0 : 2;
}

try
{
    switch (argv[0])
    {
        case "edit" when argv.Count >= 5:
        {
            var package = ResolvePackage(argv[1]);
            var session = OpenSession(package);
            var tree = WidgetTreeReader.Build(source, package);
            var nodes = tree.All().Where(n => n.Name == argv[2]).ToList();
            if (nodes.Count != 1) { Console.Error.WriteLine($"{nodes.Count} widgets named {argv[2]} in {package}"); return 2; }
            var node = nodes[0];
            var d = (int i) => double.Parse(argv[i], CultureInfo.InvariantCulture);
            var edit = argv[3] switch
            {
                "set" when argv.Count >= 6 => SlotEditor.SetField(node, session.Current, FieldPath(argv[4]), d(5)),
                "move" when argv.Count >= 6 => SlotEditor.Move(node, session.Current, d(4), d(5)),
                "resize" when argv.Count >= 6 => SlotEditor.ResizeEdge(node, session.Current, Enum.Parse<Edge>(argv[4], ignoreCase: true), d(5)),
                _ => null,
            };
            if (edit == null) return Usage();
            if (!edit.Allowed) { Console.Error.WriteLine($"refused: {edit.Reason}"); return 2; }
            foreach (var c in edit.Changes) Console.WriteLine($"{node.Name} {c.Key.Path}: {Num(c.OldValue)} -> {Num(c.NewValue)}");

            var warnings = new EditWarnings(NewEngine(), new UsageGraph(index), W, H);
            var report = warnings.ForSelection(tree, node);
            var pending = warnings.ForPendingEdit(tree, node, session.Current.With(edit.Changes, session.BaseValue), session.Current);
            Console.WriteLine(report.ConfirmationText);
            foreach (var w in pending) Console.WriteLine($"  [{w.Severity}] {w.Message}");
            foreach (var c in edit.Changes.Where(c => session.StockValue(c.Key) == null && !c.NewValue.Equals(session.BaseValue(c.Key))))
                Console.WriteLine($"  [Warning] {c.Key.Path} isn't stored in {GamePaths.ShortName(package)}: saving adds a 29-byte tag, so the package grows (size-changing edit, device-tested by canary C10).");
            if (session.Saved.Count == 0 && !session.IsDirty && !yes)
            {
                Console.WriteLine($"First edit of {GamePaths.ShortName(package)}{(editSet != null ? $" in set {editSet}" : "")}: read the above, then re-run with --yes.");
                return 3;
            }

            if (session.Apply(edit) is { } why) { Console.Error.WriteLine($"refused: {why}"); return 2; }
            var saved = session.Save();
            Console.WriteLine(saved.Summary);
            PrintPlan(session);
            return saved.Saved ? 0 : 2;
        }
        case "show":
            PrintPlan(OpenSession(ResolvePackage(argv[1])));
            return 0;
        case "reset":
        {
            var session = OpenSession(ResolvePackage(argv[1]));
            var back = session.Current.Values.Select(kv => new FieldChange(kv.Key, kv.Value, session.BaseValue(kv.Key) ?? kv.Value)).ToList();
            session.Apply(EditResult.Of(back));
            var saved = session.Save();
            Console.WriteLine(saved.Summary);
            return saved.Saved ? 0 : 2;
        }
        case "build" when argv.Count >= 2:
        {
            // the export dialog's checks (unsaved edits, names, the Impact panel's Danger warnings, ...) and outputs
            var controller = Controller();
            // the set's texture edits ship with the packages they re-point: add those packages
            var packages = argv.Skip(2).Select(ResolvePackage).Concat(controller.TextureEdits.SelectMany(controller.StoringPackages))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (controller.TextureEdits.Count > 0) Console.WriteLine($"with {controller.TextureEdits.Count} texture edit(s): {string.Join(", ", packages.Select(GamePaths.ShortName))}");
            var request = new ExportRequest(argv[1], container, packages, config.RetocPath);
            return BuildCommand.Run(controller, request, ackDanger, new RetocProcess(config.RetocPath), Console.Out, Console.Error);
        }
        case "recipe-check" when argv.Count >= 2:
        {
            // read only: data, inputs and stock by sha256, every edit built and verified in memory, textures, warnings, pins
            if (!File.Exists(argv[1])) { Console.Error.WriteLine($"refused: {argv[1]} not found"); return 2; }
            var (recipe, bad) = UmgEditor.Core.Recipes.RecipeReader.Read(File.ReadAllText(argv[1]));
            if (recipe == null) { foreach (var b in bad) Console.Error.WriteLine($"refused: {b}"); return 2; }
            var context = new UmgEditor.Core.Recipes.RecipeContext(source, UmgEditor.Core.Recipes.RecipeContext.RootOf(config),
                () => TextureUsage.LoadOrBuild(source, index), new UsageGraph(index), new EditImpact(NewEngine(), new UsageGraph(index), W, H),
                p => WidgetTreeReader.Build(source, p), W, H);
            Console.WriteLine($"recipe {recipe.Name} ({argv[1]}): {recipe.PackagesOrEmpty.Count} edited package(s), {recipe.TexturesOrEmpty.Count} texture edit(s), container {recipe.Container}");
            var checkResult = UmgEditor.Core.Recipes.RecipeCheck.Run(recipe, context);
            foreach (var l in checkResult.Log) Console.WriteLine(l);
            Console.WriteLine(checkResult.Warnings.Count == 0 ? "layout warnings: none" : $"layout warnings ({checkResult.Warnings.Count}):");
            foreach (var w in checkResult.Warnings) Console.WriteLine($"  {w.Text}{(w.IsDanger ? ackDanger ? $" (acknowledged by {BuildCommand.AckFlag})" : " (NOT acknowledged)" : "")}");
            Console.WriteLine(checkResult.Hidden.Count == 0 ? "hidden on purpose by the edits: none" : $"hidden on purpose by the edits ({checkResult.Hidden.Count}):");
            foreach (var h in checkResult.Hidden) Console.WriteLine($"  {h.Text}");
            if (!checkResult.Ok) { foreach (var r in checkResult.Refusals) Console.Error.WriteLine($"refused: {r}"); return 2; }
            if (checkResult.Danger.Count > 0 && !ackDanger)
            {
                Console.Error.WriteLine($"Read the danger warning{(checkResult.Danger.Count == 1 ? "" : "s")} above, then rerun with {BuildCommand.AckFlag}.");
                return 3;
            }
            Console.WriteLine($"OK: {checkResult.Chunks.Count} chunk(s){(checkResult.ChunksPinned ? ", all as pinned" : recipe.Expect?.Chunks == null ? " (no Expect.Chunks pins)" : "")}; nothing written");
            return 0;
        }
        case "recipe-build" when argv.Count >= 2:
        {
            if (!File.Exists(argv[1])) { Console.Error.WriteLine($"refused: {argv[1]} not found"); return 2; }
            var text = File.ReadAllText(argv[1]);
            var (recipe, bad) = UmgEditor.Core.Recipes.RecipeReader.Read(text);
            if (recipe == null) { foreach (var b in bad) Console.Error.WriteLine($"refused: {b}"); return 2; }
            var engine = NewEngine();
            var usage = new UsageGraph(index);
            var context = new UmgEditor.Core.Recipes.RecipeContext(source, UmgEditor.Core.Recipes.RecipeContext.RootOf(config),
                () => TextureUsage.LoadOrBuild(source, index), usage, new EditImpact(engine, usage, W, H), p => WidgetTreeReader.Build(source, p), W, H);
            var env = new ExportEnvironment(source, engine, usage, p => WidgetTreeReader.Build(source, p), W, H, $"recipe {recipe.Name}");
            var outcome = UmgEditor.Core.Recipes.RecipeBuild.Run(recipe, text, context, env, new RetocProcess(config.RetocPath), newName, ackDanger,
                new Progress<string>(Console.WriteLine));
            foreach (var w in outcome.Check.Warnings) Console.WriteLine($"  {w.Text}{(w.IsDanger ? ackDanger ? $" (acknowledged by {BuildCommand.AckFlag})" : " (NOT acknowledged)" : "")}");
            foreach (var w in outcome.Plan?.Warnings ?? []) Console.WriteLine($"warning: {w}");
            foreach (var r in outcome.Refusals) Console.Error.WriteLine($"refused: {r}");
            if (outcome.Code == UmgEditor.Core.Recipes.RecipeBuild.NeedsAck) Console.Error.WriteLine($"Read the danger warnings above, then rerun with {BuildCommand.AckFlag}.");
            if (outcome.Code == 0)
            {
                Console.WriteLine($"OK: copy these {outcome.Result!.Files.Count} files from {outcome.Result.ModDir} next to the stock containers on the device:");
                foreach (var f in outcome.Result.Files) Console.WriteLine($"  {f.Name}  {f.Size} bytes  sha256 {f.Sha256}");
                Console.WriteLine(recipe.Expect?.Files == null ? "(no Expect.Files pins in the recipe)" : "all device files as pinned");
            }
            return outcome.Code;
        }
        case "disable-script" when argv.Count >= 2:
        {
            if (ModBuilder.ModNameRefusal(argv[1]) is { } badMod) { Console.Error.WriteLine($"refused: {badMod}"); return 2; }
            var (script, files, why) = DisableScript.Write(config, Path.Combine(WriteGuard.ForMods(config).Root, argv[1]), DateTime.Now);
            if (script == null) { Console.Error.WriteLine($"refused: {why}"); return 2; }
            Console.Write(DisableScript.Checklist(argv[1], files!));
            Console.WriteLine($"wrote {script}: run it with no arguments for this list, or -Dir <folder> (dry run) [-Apply] [-Restore] to move the files in a folder on this PC");
            return 0;
        }
        case "recipe-from-set":
        {
            var name = newName ?? editSet ?? "default";
            var (recipe, bad) = UmgEditor.Core.Recipes.RecipeFromSet.Make(Controller(), UmgEditor.Core.Recipes.RecipeContext.RootOf(config), name, container);
            if (recipe == null) { foreach (var b in bad) Console.Error.WriteLine($"refused: {b}"); return 2; }
            var recipes = WriteGuard.ForRecipes(config);
            var file = Path.Combine(recipes.Root, name + ".json");
            var existed = File.Exists(file);
            recipes.WriteAllText(file, UmgEditor.Core.Recipes.RecipeReader.Write(recipe));
            Console.WriteLine($"{(existed ? "replaced" : "wrote")} {file}: {recipe.PackagesOrEmpty.Count} package(s), {recipe.PackagesOrEmpty.Sum(p => p.Values.Count)} value(s), " +
                              $"{recipe.TexturesOrEmpty.Count} texture edit(s), {recipe.InputsOrEmpty.Count} input(s); no Expect pins (add them from a device-tested build)");
            return 0;
        }
        case "predict" when argv.Count >= 2:
        {
            // the saved edits of the given packages, together (what `build` would ship)
            var changes = new List<FieldChange>();
            foreach (var arg in argv.Skip(1))
            {
                var session = OpenSession(ResolvePackage(arg));
                if (session.IsDirty) Console.WriteLine($"warning: {GamePaths.ShortName(session.PackagePath)} has unsaved edits; predicting the saved ones");
                changes.AddRange(session.Saved.Values.Select(kv => new FieldChange(kv.Key, session.BaseValue(kv.Key) ?? double.NaN, kv.Value)));
                foreach (var kv in session.Saved.Values)
                    Console.WriteLine($"edit: {GamePaths.ShortName(kv.Key.Package)} {session.SlotName(kv.Key.SlotExport)}.{kv.Key.Path} {Num(session.BaseValue(kv.Key) ?? double.NaN)}{(session.StockValue(kv.Key) == null ? " (default, new tag)" : "")} -> {Num(kv.Value)}");
            }
            var edits = EditSet.Empty.With(changes, _ => null);
            if (edits.Count == 0) { Console.Error.WriteLine("refused: no saved edits to predict"); return 2; }

            var mods = WriteGuard.ForMods(config);
            var outDir = Path.Combine(mods.Root, "_predictions", editSet ?? "default");
            mods.DeleteDirectory(outDir);
            var impacts = new EditImpact(NewEngine(), new UsageGraph(index), W, H).Predict(edits, p => WidgetTreeReader.Build(source, p));
            Console.WriteLine($"viewport {W}x{H} game px; LDPlayer screenshot = game px + 50 (x) / + 32 (y). JSON for validate_layout.py: {outDir}");
            // the Impact panel's view of the same layouts: its warnings and which boxes a clip cuts off
            var report = ImpactReport.Build(editSet ?? "default", W, H, impacts);
            foreach (var (impact, reported) in impacts.Zip(report.Packages))
            {
                var a = impact.Package;
                var title = $"{a.Name}{(a.Board != null ? $" [{a.Board.Label}]" : a.IsOnline ? " [online]" : "")}{(a.Edited ? " (edited)" : $" (embeds via {a.Via})")}";
                if (impact.Error != null) { Console.WriteLine($"== {title}: can't arrange ({impact.Error})"); continue; }
                mods.WriteAllText(Path.Combine(outDir, a.Name + ".stock.json"), LayoutExport.ToJson(impact.Stock, a.PackagePath));
                mods.WriteAllText(Path.Combine(outDir, a.Name + ".json"), LayoutExport.ToJson(impact.Edited, a.PackagePath, new { Edits = edits.Values.Select(kv => $"{GamePaths.ShortName(kv.Key.Package)} slot#{kv.Key.SlotExport} {kv.Key.Path} -> {Num(kv.Value)}") }));
                var scale = impact.Stock.ScaleBoxes.Zip(impact.Edited.ScaleBoxes).Where(p => Math.Abs(p.First.Applied - p.Second.Applied) > 1e-6).ToList();
                Console.WriteLine($"== {title}: {impact.Changes.Count} box{(impact.Changes.Count == 1 ? "" : "es")} change{(scale.Count > 0 ? $"; RESCALES {string.Join(", ", scale.Select(p => $"{p.First.Widget} x{p.First.Applied:0.#####} -> x{p.Second.Applied:0.#####}"))}" : "")}");
                foreach (var w in reported.Warnings)
                    Console.WriteLine($"   [{w.Severity}] {w.Message}");
                var cuts = reported.Boxes.Where(b => b.CutOff).ToDictionary(b => b.Path, b => b.Cut!);
                foreach (var c in impact.Changes)
                    Console.WriteLine($"   {(c.Visible ? "        " : "[hidden]")} {c.Path}  [{c.OwnerPackage}]" +
                                      (cuts.TryGetValue(c.Path, out var cut) ? $"  [{cut.Text}]" : "") + Environment.NewLine +
                                      $"            L {c.Before.X:0.0} -> {c.After.X:0.0}  R {c.Before.Right:0.0} -> {c.After.Right:0.0}  T {c.Before.Y:0.0} -> {c.After.Y:0.0}  B {c.Before.Bottom:0.0} -> {c.After.Bottom:0.0}");
            }
            var dangers = report.Packages.Sum(p => p.Warnings.Count(w => w.Severity == Severity.Danger));
            Console.WriteLine($"{report.WarningCount} warning{(report.WarningCount == 1 ? "" : "s")}{(dangers > 0 ? $", {dangers} danger (the export dialog asks to acknowledge each)" : "")}");
            return 0;
        }
        case "anim-flatten" when argv.Count >= 3:
        {
            // every key of the named animations set to the channel's normal value (last key of --normal-from, checked
            // against --check), only the --parts tracks; saved like any edit (both copies of each key, hard checks)
            var package = ResolvePackage(argv[1]);
            var session = OpenSession(package);
            var picked = UmgEditor.Core.Animation.TrackParts.None;
            foreach (var p in (parts ?? "width,visibility,positions").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                picked |= Enum.Parse<UmgEditor.Core.Animation.TrackParts>(p, ignoreCase: true);
            var names = argv[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var flat = UmgEditor.Core.Animation.AnimationFlatten.Plan(package, session.AnimKeys.Animations, names, normalFrom, crossCheck, picked,
                File.ReadAllBytes(session.StockCopyPath));
            foreach (var l in flat.Log) Console.WriteLine("  " + l);
            foreach (var r in flat.Refusals) Console.Error.WriteLine("refused: " + r);
            if (flat.Refusals.Count > 0) return 2;
            if (flat.Changes.Count == 0) { Console.Error.WriteLine("refused: nothing to change"); return 2; }
            Console.WriteLine($"{flat.Changes.Count} keys in {string.Join(", ", names)} ({picked}), normal value = last key of {normalFrom}, checked against {crossCheck}");
            if (session.Saved.Count == 0 && !session.IsDirty && !yes)
            {
                Console.WriteLine($"First edit of {GamePaths.ShortName(package)}{(editSet != null ? $" in set {editSet}" : "")}: read the above, then re-run with --yes.");
                return 3;
            }
            if (session.Apply(EditResult.Of(flat.Changes)) is { } why) { Console.Error.WriteLine($"refused: {why}"); return 2; }
            var saved = session.Save();
            Console.WriteLine(saved.Summary);
            PrintPlan(session);
            return saved.Saved ? 0 : 2;
        }
        case "anim-key" when argv.Count >= 8:
        {
            // one key's value, frame (ticks) or interp mode (0 Linear, 1 Constant, 2 Cubic); both copies when it has a compiled one
            var package = ResolvePackage(argv[1]);
            var session = OpenSession(package);
            if (session.AnimKeys.Animations.FirstOrDefault(a => a.Name == argv[2]) is not { } anim) { Console.Error.WriteLine($"refused: {GamePaths.ShortName(package)} has no animation {argv[2]}"); return 2; }
            var tracks = anim.Tracks.Where(t => t.Widget == argv[3] && t.Channels.Any(c => c.Path == argv[4])).ToList();
            if (tracks.Count != 1) { Console.Error.WriteLine($"refused: {tracks.Count} tracks of {argv[3]} in {argv[2]} have a channel {argv[4]} (need exactly 1)"); return 2; }
            var channel = tracks[0].Channels.Single(c => c.Path == argv[4]);
            var keyIndex = int.Parse(argv[5], CultureInfo.InvariantCulture);
            if (keyIndex < 0 || keyIndex >= channel.Keys.Count) { Console.Error.WriteLine($"refused: {argv[4]} has {channel.Keys.Count} keys"); return 2; }
            var field = Enum.Parse<UmgEditor.Core.Animation.AnimKeyField>(argv[6], ignoreCase: true);
            var key = UmgEditor.Core.Animation.AnimKeyIndex.KeyOf(package, tracks[0], channel, keyIndex, field);
            var value = double.Parse(argv[7], CultureInfo.InvariantCulture);
            Console.WriteLine($"{argv[2]} · {argv[3]} · {argv[4]} · key {keyIndex} {field}: {Num(session.BaseValue(key) ?? double.NaN)} -> {Num(value)}");
            return ApplyAndSave(session, package, [new FieldChange(key, session.BaseValue(key) ?? 0, value)]);
        }
        case "anim-end" when argv.Count >= 5:
        {
            // C9's range edit: every range bound of the animation at oldEnd -> newEnd (the MovieScene's playback end oldEnd+1 -> newEnd+1)
            var package = ResolvePackage(argv[1]);
            var session = OpenSession(package);
            var (oldEnd, newEnd) = (int.Parse(argv[3], CultureInfo.InvariantCulture), int.Parse(argv[4], CultureInfo.InvariantCulture));
            var changes = session.AnimationEndChanges(argv[2], oldEnd, newEnd);
            if (changes.Count == 0) { Console.Error.WriteLine($"refused: no range bound of {argv[2]} at {oldEnd}"); return 2; }
            foreach (var c in changes) Console.WriteLine($"  {session.AnimRange(c.Key).Ref!.Description} -> {Num(c.NewValue)}");
            return ApplyAndSave(session, package, changes);
        }
        case "show-anim":
        {
            // every animation of the package, what each track does, and with --bytes every key with its byte locations
            var package = ResolvePackage(argv[1]);
            var anims = UmgEditor.Core.Animation.AnimationReader.Read(source, package, animFilter == null ? null : n => animFilter.IsMatch(n));
            Console.WriteLine($"{GamePaths.ShortName(package)}: {anims.Count} animation{(anims.Count == 1 ? "" : "s")}{(animFilter != null ? $" matching {animFilter}" : "")}");
            foreach (var a in anims)
            {
                var tracks = a.Tracks.Where(t => widgetFilter == null || t.Widget.Equals(widgetFilter, StringComparison.OrdinalIgnoreCase)).ToList();
                if (tracks.Count == 0) continue;
                Console.WriteLine($"== {a.Name}  playback {UmgEditor.Core.Animation.AnimationText.S(a.Seconds(a.Start))}–{UmgEditor.Core.Animation.AnimationText.S(a.Seconds(a.End))}");
                foreach (var t in tracks)
                {
                    var does = UmgEditor.Core.Animation.AnimationText.Describe(t);
                    if (does.Count == 0 && !withBytes) continue;
                    Console.WriteLine($"  {UmgEditor.Core.Animation.AnimationText.Target(t)}  {t.Track} {t.Property}  section {UmgEditor.Core.Animation.AnimationText.S(a.Seconds(t.SectionStart))}–{UmgEditor.Core.Animation.AnimationText.S(a.Seconds(t.SectionEnd))}  {t.CompletionMode}  (export {t.SectionExport})");
                    if (withBytes)
                        foreach (var c in t.Channels) Console.WriteLine(UmgEditor.Core.Animation.AnimationText.KeysWithBytes(t, c, "    "));
                    else
                        foreach (var d in does) Console.WriteLine($"    {d.Name}: {d.Text}");
                }
            }
            return 0;
        }
        case "tex-info" when argv.Count >= 3:
        {
            var c = Controller();
            var package = ResolvePackage(argv[1]);
            if (c.TextureOf(package, argv[2]) is not { } current) { Console.Error.WriteLine($"refused: {argv[2]} in {GamePaths.ShortName(package)} isn't a uiImage with a stored texture"); return 2; }
            var tex = TextureEdits.LoadTexture(source, current.Texture);
            Console.WriteLine($"{GamePaths.ShortName(package)}/{argv[2]} shows {current.Texture}");
            Console.WriteLine($"  {(tex == null ? "not a Texture2D" : $"{tex.PlatformData.SizeX}x{tex.PlatformData.SizeY} {tex.PlatformData.PixelFormat}, {tex.PlatformData.Mips.Length} mip(s)")}; " +
                              $"theme copies: {string.Join(", ", TextureEdits.ThemeCopies(source, current.Texture).Select(p => p[..p.LastIndexOf('/')][(p.IndexOf("/Widget/", StringComparison.Ordinal) + 8)..]))}");
            Console.WriteLine($"  name-map path {current.NameMapPath} ({current.NameMapPath.Length} characters)");
            Console.WriteLine($"  {TextureEdits.SourceRefusal(source, current.Texture) ?? "can be cloned (B8G8R8A8)"}");
            var margins = UmgEditor.Core.Layout.PreviewRaster.StoredMargins(WidgetTreeReader.Build(source, package).Find(argv[2])!);
            if (margins.Any) Console.WriteLine($"  9-slice margins {Num(margins.Left)}/{Num(margins.Top)}/{Num(margins.Right)}/{Num(margins.Bottom)} (a real image wants --margins0)");
            PrintUsers(c, current.Texture, package, argv[2]);
            if (c.TextureEditOf(package, argv[2]) is { } edit) Console.WriteLine($"edited in this project: -> {edit.NewName} ({edit.Mode}{(edit.Image != null ? ", " + edit.Image : "")})");
            return 0;
        }
        case "tex-users" when argv.Count >= 2:
        {
            var texture = ResolveTexture(argv[1]);
            Console.WriteLine(texture);
            PrintUsers(Controller(), texture);
            return 0;
        }
        case "tex-sources" when argv.Count >= 3:
        {
            var c = Controller();
            var package = ResolvePackage(argv[1]);
            if (c.TextureOf(package, argv[2]) is not { } current) { Console.Error.WriteLine($"refused: {argv[2]} isn't a uiImage with a stored texture"); return 2; }
            var sources = TextureEdits.Sources(source, c.TextureFormats, current);
            Console.WriteLine($"{sources.Count} B8G8R8A8 texture(s) a clone of {GamePaths.ShortName(current.Texture)} can come from (best first: same folder, same theme copies, a path of the same length):");
            foreach (var s in sources.Take(40))
            {
                var f = c.TextureFormats.Of(s)!;
                var same = TextureEdits.NewNameMapPath(s, GamePaths.ShortName(s)).Length == current.NameMapPath.Length;
                Console.WriteLine($"  {s}  ({f.Width}x{f.Height}, {f.Mips} mip(s); a clone's name is {GamePaths.ShortName(s).Length} characters{(same ? "; same path length" : "")})");
            }
            return 0;
        }
        case "tex-preview" when argv.Count >= 5:
        {
            var c = Controller();
            var package = ResolvePackage(argv[1]);
            if (c.TextureOf(package, argv[2]) is not { } current) { Console.Error.WriteLine($"refused: {argv[2]} isn't a uiImage with a stored texture"); return 2; }
            var (image, why) = PngReader.Read(argv[3]);
            if (image == null) { Console.Error.WriteLine($"refused: {why}"); return 2; }
            var screen = WidgetTreeReader.Build(source, screenArg != null ? ResolvePackage(screenArg) : package);
            var repointed = scope == TextureEditScope.AllUsers ? c.TextureUsers(current.Texture).Where(r => r.NameMapPath == current.NameMapPath).Select(r => r.Package).Distinct().ToList() : [package];
            var result = TexturePreview.Render(source, p => WidgetTreeReader.Build(source, p, readOnly: true), screen, current.Texture, repointed, image, marginsZero, W, H,
                scope == TextureEditScope.Widget ? argv[2] : null);
            PngWriter.Write(argv[4], result.Image);
            Console.WriteLine($"{argv[4]}: {GamePaths.ShortName(screen.PackagePath)} at {W}x{H}, {result.Boxes} box(es) with {GamePaths.ShortName(current.Texture)}; stock above, the image below" +
                              (result.ShowsHidden ? " (hidden at rest: drawn with every hidden widget)" : "") + (result.Boxes == 0 ? $" (the texture isn't on {GamePaths.ShortName(screen.PackagePath)}; try --in)" : ""));
            return 0;
        }
        case "stock-chunk" when argv.Count >= 3:
        {
            // read-only: a stock package's raw Zen chunk (decrypted, decompressed) for independent checks (check_point.py)
            var (fileKey, bytes) = source.ReadPackageBytes(ResolvePackage(argv[1]));
            File.WriteAllBytes(argv[2], bytes);
            Console.WriteLine($"{fileKey}: {bytes.Length} bytes, chunk {source.ChunkIdHex(fileKey)} -> {argv[2]}");
            return 0;
        }
        case "tex-coverage":
        {
            // every uiImage outside the library: plan a white copy (of the widget's own texture, or of the best source for
            // one that can't be cloned) with nothing saved, and count what is refused and why
            var c = Controller();
            var refs = c.TextureUsage.Refs.Where(r => r.IsImageTexture && !GamePaths.IsLibrary(r.Package)
                && (folderFilter == null || r.Package.Contains(folderFilter, StringComparison.OrdinalIgnoreCase))).ToList();
            Console.WriteLine($"{refs.Count} uiImage widget(s){(folderFilter != null ? $" in packages containing \"{folderFilter}\"" : "")}");
            var reasons = new Dictionary<string, List<string>>();
            int ok = 0, renamed = 0, pointed = 0, done = 0, needSource = 0, noSameLength = 0;
            foreach (var r in refs)
            {
                string? why = null;
                try
                {
                    var bad = TextureEdits.SourceRefusal(source, r.Texture);
                    var all = bad == null ? [] : TextureEdits.Sources(source, c.TextureFormats, r);
                    var src = all.FirstOrDefault();
                    if (bad != null)
                    {
                        needSource++;
                        // the same-length rule (before appending + pointing) needed a source whose clone path is as long
                        if (!all.Any(x => TextureEdits.NewNameMapPath(x, GamePaths.ShortName(x)).Length == r.NameMapPath.Length))
                        {
                            noSameLength++;
                            if (listAll) Console.WriteLine($"  no same-length source: {GamePaths.ShortName(r.Package)}/{r.Export} ({GamePaths.ShortName(r.Texture)}, path {r.NameMapPath.Length} characters, shown in: {string.Join(", ", c.EmbeddedBy(r.Package).Select(GamePaths.ShortName).Order())})");
                        }
                    }
                    if (bad != null && src == null) why = bad + ", and no source";
                    else
                    {
                        var edit = new UmgEditor.Core.Modding.TextureEdit(r.Package, r.Export, r.Texture, TextureEditMode.White,
                            TextureEdits.SuggestName(source, src ?? r.Texture, []), TextureEditScope.Widget, Source: src);
                        var plan = TextureEdits.Plan(source, c.TextureUsage, c.ProjectFolder, [edit]);
                        if (!plan.Ok) why = plan.Refusals[0];
                        else if (plan.Items[0].Pointed.Count > 0) pointed++;
                        else renamed++;
                    }
                }
                catch (Exception e) { why = $"{e.GetType().Name}: {e.Message}"; }
                if (why == null) ok++;
                else
                {
                    var key = System.Text.RegularExpressions.Regex.Replace(why, @"^texture edit \d+ \([^)]*\): ", "");
                    key = System.Text.RegularExpressions.Regex.Replace(key, @"/Game/[^\s,;:]+|""[^""]*""|\d+", "…");
                    (reasons.TryGetValue(key, out var l) ? l : reasons[key] = []).Add($"{GamePaths.ShortName(r.Package)}/{r.Export}: {why}");
                }
                if (++done % 50 == 0) Console.Error.WriteLine($"  {done}/{refs.Count}");
            }
            Console.WriteLine($"replaceable: {ok} of {refs.Count} ({renamed} by renaming the path in place, {pointed} by appending + pointing)");
            Console.WriteLine($"{needSource} can't be cloned themselves (ETC2 or an ETC2 theme copy) and take a source; {noSameLength} of them have no source of the same path length (impossible with renames alone)");
            foreach (var (key, list) in reasons.OrderByDescending(kv => kv.Value.Count))
            {
                Console.WriteLine($"refused {list.Count}x: {key}");
                foreach (var line in list.Take(5)) Console.WriteLine($"    {line}");
            }
            return reasons.Count == 0 ? 0 : 2;
        }
        case "tex" when argv.Count >= 4:
            return TextureEdit(TextureEditMode.Image, argv[1], argv[2], argv[3]);
        case "tex-white" when argv.Count >= 3:
            return TextureEdit(TextureEditMode.White, argv[1], argv[2], null);
        case "tex-show":
        {
            var c = Controller();
            Console.WriteLine($"{c.TextureEdits.Count} texture edit{(c.TextureEdits.Count == 1 ? "" : "s")} in {Path.Combine(c.ProjectFolder, TextureEditStore.FileName)}");
            if (c.TextureEdits.Count == 0) return 0;
            var plan = c.PlanTextures();
            PrintTexturePlan(plan);
            return plan.Ok ? 0 : 2;
        }
        case "tex-reset" when argv.Count >= 3:
        {
            var c = Controller();
            if (!c.RemoveTextureEdit(ResolvePackage(argv[1]), argv[2])) { Console.Error.WriteLine($"refused: no texture edit on {argv[2]}"); return 2; }
            c.SaveTextureEdits();
            Console.WriteLine($"removed; {c.TextureEdits.Count} texture edit(s) left");
            return 0;
        }
        default:
            return Usage();
    }
}
catch (Exception e) when (e is ArgumentException or InvalidDataException or UnauthorizedAccessException or FileNotFoundException or FormatException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 2;
}

static string FieldPath(string f) => f.StartsWith("LayoutData.") || f == "ZOrder" || f == "bAutoSize" ? f : "LayoutData." + f;

static T? Try<T>(Func<T> f) where T : class
{
    try { return f(); }
    catch { return null; }
}

int ApplyAndSave(PatchSession session, string package, IReadOnlyList<FieldChange> changes)
{
    if (session.Saved.Count == 0 && !session.IsDirty && !yes)
    {
        Console.WriteLine($"First edit of {GamePaths.ShortName(package)}{(editSet != null ? $" in set {editSet}" : "")}: read the above, then re-run with --yes.");
        return 3;
    }
    if (session.Apply(EditResult.Of(changes)) is { } why) { Console.Error.WriteLine($"refused: {why}"); return 2; }
    var saved = session.Save();
    Console.WriteLine(saved.Summary);
    PrintPlan(session);
    return saved.Saved ? 0 : 2;
}

static int Usage()
{
    Console.Error.WriteLine("""
        usage:
          umgmod [--set NAME] edit  <package> <widget> set <field> <value> [--yes]
          umgmod [--set NAME] edit  <package> <widget> move <dx> <dy> [--yes]
          umgmod [--set NAME] edit  <package> <widget> resize <Left|Top|Right|Bottom> <d> [--yes]
          umgmod [--set NAME] show  <package>
          umgmod [--set NAME] reset <package>
          umgmod [--set NAME] build <modName> <package>... [--container pc9999_mobile_and_P] [--ack-danger]
          umgmod [--set NAME] predict <package>...
          umgmod show-anim <package> [--anim REGEX] [--widget NAME] [--bytes]
          umgmod [--set NAME] anim-flatten <package> <anim,anim...> [--parts width,visibility,positions] [--normal-from Info_Hide] [--check Show] [--yes]
          umgmod [--set NAME] anim-key <package> <anim> <widget> <channel> <key#> <value|frame|interp> <new> [--yes]
          umgmod [--set NAME] anim-end <package> <anim> <oldEndTicks> <newEndTicks> [--yes]
          umgmod tex-info <package> <widget>
          umgmod tex-users <texture>
          umgmod tex-sources <package> <widget>
          umgmod stock-chunk <package> <out.bin>
          umgmod tex-coverage [--folder SUBSTR] [--list]
          umgmod tex-preview <package> <widget> <image.png> <out.png> [--in SCREEN] [--margins0] [--scope widget|package|all]
          umgmod [--set NAME] tex <package> <widget> <image.png> [--name NEW] [--source TEXTURE] [--margins0] [--scope widget|package|all] [--yes]
          umgmod [--set NAME] tex-white <package> <widget> [--name NEW] [--scope widget|package|all] [--yes]
          umgmod [--set NAME] tex-show
          umgmod [--set NAME] tex-reset <package> <widget>
          umgmod recipe-check <recipe.json> [--ack-danger]
          umgmod recipe-build <recipe.json> [--name MOD] [--ack-danger]
          umgmod [--set NAME] recipe-from-set [--name RECIPE] [--container C]
          umgmod disable-script <mod>
        <package>: /Game/... path or a unique suffix (MatchTime/MatchTimeBase); <field>: e.g. Offsets.Right
        """);
    return 1;
}
