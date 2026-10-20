// umgmod: make and pack edits from the command line (no UI), for device canaries before the editor UI exists.
//   umgmod [--set NAME] edit  <package> <widget> set <field> <value> [--yes]
//   umgmod [--set NAME] edit  <package> <widget> move <dx> <dy> [--yes]
//   umgmod [--set NAME] edit  <package> <widget> resize <Left|Top|Right|Bottom> <d> [--yes]
//   umgmod [--set NAME] show  <package>
//   umgmod [--set NAME] reset <package>
//   umgmod [--set NAME] build <modName> <package>... [--container pc9999_mobile_and_P] [--ack-danger]
//   umgmod [--set NAME] predict <package>...
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
if (argv.Count < 2) return Usage();

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
        Console.WriteLine($"  {e.SlotName}.{e.Key.Path}  @ {e.OffsetHex}  {Num(e.OldValue)} -> {Num(e.NewValue)}  [{e.OldHex}] -> [{e.NewHex}]\n      context x{e.ContextMatches}: {e.ContextHex}");
    foreach (var i in plan.Insertions)
        Console.WriteLine($"  {i.SlotName}.{i.Path}  NEW TAG before {i.Before} @ {i.InsertAtHex}  {Num(i.Key is { } k ? s.BaseValue(k) ?? 0 : 0)} (default) -> {Num(i.Value)}  [{i.TagHex}]\n      context x{i.ContextMatches}");
    if (plan.ChangesSize) Console.WriteLine($"  {plan.SizeText}; {PackageBytes.HeaderChangesOf(plan)}");
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
        case "build" when argv.Count >= 3:
        {
            // the export dialog's checks (unsaved edits, names, the Impact panel's Danger warnings, ...) and outputs
            var controller = new EditorController(source, new UsageGraph(index), NewEngine(), p => WidgetTreeReader.Build(source, p), W, H,
                editSet ?? EditorController.DefaultProject);
            var request = new ExportRequest(argv[1], container, argv.Skip(2).Select(ResolvePackage).ToList(), config.RetocPath);
            return BuildCommand.Run(controller, request, ackDanger, new RetocProcess(config.RetocPath), Console.Out, Console.Error);
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
        <package>: /Game/... path or a unique suffix (MatchTime/MatchTimeBase); <field>: e.g. Offsets.Right
        """);
    return 1;
}
