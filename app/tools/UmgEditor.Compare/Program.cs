// PC ↔ mobile comparison of UI widget packages. Read-only on both stock installs; output only under WorkDir/out/experiment.
//   umgcompare check-pc  [--pc-paks DIR]                       opens the PC containers and checks the basics (exit 0 = pass)
//   umgcompare compare   [--pc-paks DIR] [--focus SUBSTR] [--out DIR]   diffs every widget package both builds ship
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UmgEditor.Core;
using UmgEditor.Core.Layout;
using UmgEditor.Core.Textures;

var config = GameConfig.LoadOrDefault(Path.Combine(AppContext.BaseDirectory, "umgeditor.json"));
string? Opt(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
var pcConfig = config with { PaksDir = Opt("--pc-paks") ?? @"F:\SteamLibrary\steamapps\common\eFootball\pak" };
const string WidgetRoot = "/Game/Assets/ui/Data/Widget/";
const string Tv = WidgetRoot + "TVGraphics/SwitchAssets_Original/";

/// <summary>--pc = PC stock; --mod DIR = PC stock with that mod folder on top; otherwise mobile.</summary>
GameConfig SourceConfig() => Opt("--mod") is { } mod ? pcConfig with { ExtraPaksDirs = [mod] } : args.Contains("--pc") ? pcConfig : config;

var failures = 0;
void Check(string what, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
    if (!ok) failures++;
}

switch (args.FirstOrDefault())
{
    case "check-pc": return CheckPc();
    case "compare": return Compare();
    case "anim-dump": return AnimDump();
    case "export-dump": return AnimDump(all: true);
    case "anim-keys": return AnimKeys();
    case "anim-report": return AnimReportCommand();
    case "mod-diff": return RunModDiff();
    case "canary-goal-slide": return CanaryGoalSlide();
    case "canary-insert": return CanaryInsert();
    case "canary-texture": return CanaryTexture();
    default:
        Console.WriteLine("       umgcompare mod-diff --mod DIR [--out DIR]   what a PC mod folder changes against PC stock");
        Console.WriteLine("usage: umgcompare check-pc|compare [--pc-paks DIR] [--focus SUBSTR] [--out DIR]");
        Console.WriteLine("       umgcompare anim-keys <packagePath> [--anim REGEX] [--widget NAME] [--pc]   keys of every channel, checked against the compiled copy");
        Console.WriteLine("       umgcompare anim-dump <packagePath> <out.json> [--pc]   every MovieScene/WidgetAnimation export as JSON");
        Console.WriteLine("       umgcompare export-dump <packagePath> <out.json> [--pc]   every export (widgets, slots, animations) as JSON");
        return 2;
}

// C9: the UCL mod's goal-banner motion on mobile MatchTimeGoal, same-size only (keys, playback/section/compiled ranges).
// Stock: Show drops the banner from -96 to 0 by 0.4s (0.8s long); Hide raises it back to -96.
// C9:    Show holds it behind the board (-96) until 0.4s, then slides it out linearly to 0 at 1.8s (2.2s long);
//        Hide carries on downwards to +90 instead of going back up. The mod's section/compiled ranges, not the tracks' EvaluationField.
int CanaryGoalSlide()
{
    var modName = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "C9";
    const string Goal = Tv + "MatchTime/MatchTimeGoal";
    const int OldEnd = 48000, NewEnd = 132000, Hold = 24000, SlideEnd = 108000;
    using var source = GameSource.Open(config);
    var anims = UmgEditor.Core.Animation.AnimationReader.Read(source, Goal).ToDictionary(a => a.Name);
    UmgEditor.Core.Animation.AnimTrack GoalY(string anim) =>
        anims[anim].Tracks.Single(t => t.Widget == "Goal" && t.Track == "2DTransformTrack" && t.Channels.Any(c => c.Path == "Translation[1]" && c.Keys.Count > 0));
    string Keys(UmgEditor.Core.Animation.AnimTrack t) => string.Join(" ", t.Channels.Single(c => c.Path == "Translation[1]").Keys.Select(k => $"{k.Frame}={k.Value}"));

    var show = GoalY("Show");
    var hide = GoalY("Hide");
    Check("stock Show Goal Y = 0:-96, 24000:0, 48000:0", Keys(show) == "0=-96.0 24000=0.0 48000=0.0", Keys(show));
    Check("stock Hide Goal Y = 24000:0, 48000:-96", Keys(hide) == "24000=0.0 48000=-96.0", Keys(hide));
    Check("stock Show playback 0..48001", anims["Show"].Start == 0 && anims["Show"].End == OldEnd + 1, $"{anims["Show"].Start}..{anims["Show"].End}");
    if (failures > 0) return 1;

    var patch = new UmgEditor.Core.Animation.AnimationPatch(source, Goal);
    patch.SetKeys(show.SectionExport, "Translation[1]", [new(1, Value: -96f, InterpMode: 1), new(2, Frame: SlideEnd, Value: 0f)]);
    patch.SetKeys(hide.SectionExport, "Translation[1]", [new(1, Value: 90f)]);
    foreach (var (i, name, type, _) in patch.ExportsOf("Show").ToList())
    {
        if (type == "MovieScene") patch.MoveRangeBounds(i, OldEnd + 1, NewEnd + 1);
        else if (type.EndsWith("Section") || type == "MovieSceneCompiledData") patch.MoveRangeBounds(i, OldEnd, NewEnd);
    }
    var problems = patch.Verify();
    foreach (var l in patch.Log) Console.WriteLine("  " + l);
    foreach (var p in problems) Console.WriteLine("  PROBLEM " + p);
    Check("patch verified by re-parse (only intended values changed)", problems.Count == 0, $"{problems.Count} problems");
    if (failures > 0) return 1;

    var result = new UmgEditor.Core.Modding.ModBuilder(source, new UmgEditor.Core.Modding.RetocProcess(config.RetocPath))
        .Build(modName, "pc9999_mobile_and_P", [new UmgEditor.Core.Modding.ModPackage(Goal, patch.FileKey, source.ChunkIdHex(patch.FileKey), patch.Bytes)]);
    Console.WriteLine(result.Log);
    foreach (var r in result.Refusals) Console.WriteLine("  REFUSED " + r);
    Check("mod built, retoc verify + read-back passed", result.Built, result.ModDir);
    foreach (var f in result.Files) Console.WriteLine($"  {f.Name}  {f.Size:N0} B  {f.Sha256}");
    Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
    return failures == 0 ? 0 : 1;
}

// C13 (option 2, step 1): two NEW texture packages, copies of both theme copies of DemoTitlePlate (2x2) renamed to
// DemoTitleUCL01 with test colours (Tex720 green, 0164F magenta), and DemoTitleBase's soft texture path re-pointed to
// them (a same-length name-map rename). Same sizes everywhere. Seen on the replay title plate (DemoTitle).
int CanaryTexture()
{
    var modName = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "C13";
    const string DemoTitle = Tv + "DemoTitle/";
    const string NewName = "DemoTitleUCL01";
    string softFrom = DemoTitle + "Tex720/DemoTitlePlate.DemoTitlePlate", softTo = DemoTitle + $"Tex720/{NewName}.{NewName}";
    using var source = GameSource.Open(config);

    var green = UmgEditor.Core.Modding.TextureClone.Build(source, DemoTitle + "Tex720/DemoTitlePlate", NewName, UmgEditor.Core.Modding.TextureClone.Solid(0, 255, 0));
    var magenta = UmgEditor.Core.Modding.TextureClone.Build(source, DemoTitle + "0164F/Tex720/DemoTitlePlate", NewName, UmgEditor.Core.Modding.TextureClone.Solid(255, 0, 255));
    var repoint = UmgEditor.Core.Modding.SoftPathRepoint.Build(source, DemoTitle + "DemoTitleBase", softFrom, softTo);
    foreach (var (what, log, refusals) in new[] { ("Tex720 clone (green)", green.Log, green.Refusals), ("0164F clone (magenta)", magenta.Log, magenta.Refusals), ("DemoTitleBase re-point", repoint.Log, repoint.Refusals) })
    {
        Console.WriteLine(what);
        foreach (var l in log) Console.WriteLine("  " + l);
        Check(what + " planned and verified (byte diff + re-parse)", refusals.Count == 0, string.Join(" | ", refusals));
    }
    if (failures > 0) return 1;

    var packages = new[] { green.Package!, magenta.Package!, repoint.Package! };
    var result = new UmgEditor.Core.Modding.ModBuilder(source, new UmgEditor.Core.Modding.RetocProcess(config.RetocPath)).Build(modName, "pc9999_mobile_and_P", packages);
    Console.WriteLine(result.Log);
    foreach (var r in result.Refusals) Console.WriteLine("  REFUSED " + r);
    Check("mod built, retoc verify + read-back passed", result.Built, result.ModDir);
    if (failures > 0) return 1;

    // as the game would see it: stock + the mod mounted together, everything loaded by path
    using (var withMod = GameSource.Open(config with { ExtraPaksDirs = [result.ModDir] }))
    {
        var dtb = Newtonsoft.Json.JsonConvert.SerializeObject(withMod.LoadPackage(DemoTitle + "DemoTitleBase").GetExports().Single(e => e.Name == "Img_Plate"));
        Check("with the mod mounted, DemoTitleBase's plate points at " + NewName, dtb.Contains(softTo), "");
        foreach (var (p, rgb) in new[] { (green.Package!, (0, 255, 0)), (magenta.Package!, (255, 0, 255)) })
        {
            var tex = withMod.LoadPackage(p.PackagePath).GetExports().OfType<CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D>().SingleOrDefault();
            var data = tex?.PlatformData.Mips[0].BulkData?.Data ?? [];
            var ok = tex != null && data.Length >= 4 && data.Chunk(4).All(px => px[2] == rgb.Item1 && px[1] == rgb.Item2 && px[0] == rgb.Item3);
            Check($"with the mod mounted, {p.PackagePath} loads by path and decodes to RGB {rgb}", ok, tex == null ? "not found" : $"{tex.PlatformData.SizeX}x{tex.PlatformData.SizeY}");
        }
    }
    foreach (var f in result.Files) Console.WriteLine($"  {f.Name}  {f.Size:N0} B  {f.Sha256}");
    Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
    return failures == 0 ? 0 : 1;
}

// C10: the first size-changing edit on mobile. Text_Score_Home's slot in MatchTime doesn't store Offsets.Left (default 0);
// insert it as 20 (the home score digit moves 20 units right inside the score box). Always on screen, unanimated slot.
int CanaryInsert()
{
    var modName = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "C10";
    const string MatchTime = Tv + "MatchTime/MatchTime";
    const float NewLeft = 20f;
    using var source = GameSource.Open(config);
    var tree = WidgetTreeReader.Build(source, MatchTime);
    var node = tree.Find("Text_Score_Home");
    var left = node?.Slot?.Fields.FirstOrDefault(f => f.Path == "LayoutData.Offsets.Left");
    Check("Text_Score_Home slot found, Offsets.Left not stored", node?.Slot != null && left is { Stored: false, Value: "0" }, $"{node?.Slot?.Name} export {node?.Slot?.ExportIndex}, Left {(left?.Stored == true ? "stored " : "default ")}{left?.Value}");
    Check("Text_Score_Home slot isn't animated", node is { IsSlotAnimated: false });
    if (failures > 0) return 1;

    var ins = new UmgEditor.Core.Patching.TagInserter(source, MatchTime);
    ins.InsertFloat(node!.Slot!.ExportIndex, "LayoutData.Offsets", "Left", NewLeft, ["Left", "Top", "Right", "Bottom"]);
    var problems = ins.Verify();
    foreach (var l in ins.Log) Console.WriteLine("  " + l);
    foreach (var p in problems) Console.WriteLine("  PROBLEM " + p);
    Check("insertion verified by re-parse (only the new field differs)", problems.Count == 0, $"{problems.Count} problems");
    if (failures > 0) return 1;

    var result = new UmgEditor.Core.Modding.ModBuilder(source, new UmgEditor.Core.Modding.RetocProcess(config.RetocPath))
        .Build(modName, "pc9999_mobile_and_P", [new UmgEditor.Core.Modding.ModPackage(MatchTime, ins.FileKey, source.ChunkIdHex(ins.FileKey), ins.Bytes)]);
    Console.WriteLine(result.Log);
    foreach (var r in result.Refusals) Console.WriteLine("  REFUSED " + r);
    Check("mod built, retoc verify + read-back passed", result.Built, result.ModDir);
    foreach (var f in result.Files) Console.WriteLine($"  {f.Name}  {f.Size:N0} B  {f.Sha256}");

    // predicted box: same tree with the field stored as 20 (layout reads Value whether stored or not)
    var layoutEngine = new LayoutEngine(UiScaleSettings.Load(source),
        p => { try { return WidgetTreeReader.Build(source, p, readOnly: true); } catch { return null; } }, _ => null);
    var stockBox = layoutEngine.Arrange(tree, 1786, 1000).Find("Text_Score_Home")!.Bounds;
    var ppu = layoutEngine.Arrange(tree, 1786, 1000).Find("Text_Score_Home")!.PixelsPerUnit;
    Console.WriteLine($"stock Text_Score_Home box at 1786x1000: {stockBox}; +{NewLeft} units = +{NewLeft * ppu:0.0} px → x {stockBox.X + NewLeft * ppu:0.0}..{stockBox.Right + NewLeft * ppu:0.0} (screenshot: +50/+32)");
    Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
    return failures == 0 ? 0 : 1;
}

// Step 4b/4c: the scoreboard family's animations on mobile, PC stock and PC + the UCL mod, side by side.
int AnimReportCommand()
{
    var modDir = Opt("--mod") ?? @"E:\n3tron\patch\pc_mods\3RD_PARTY\PesConsole\Content\Paks\~mods\UCL_theme_by_eFootball.cz";
    var outDir = Opt("--out") ?? Path.Combine(config.WorkDir, "out", "experiment", "anim");
    using var mobile = GameSource.Open(config);
    using var pc = GameSource.Open(pcConfig);
    using var ucl = GameSource.Open(pcConfig with { ExtraPaksDirs = [modDir] });
    var packages = UiAssetIndex.LoadOrBuild(mobile).Where(e => e.Kind == UiAssetKind.Widget).Select(e => GamePaths.ToPackagePath(e.Path))
        .Where(p => p.StartsWith(Tv + "MatchTime/", StringComparison.OrdinalIgnoreCase)).Distinct().Order(StringComparer.Ordinal).ToList();
    var report = AnimReport.Build(packages, [new("mobile", mobile), new("PC", pc), new("UCL", ucl)], new Progress<string>(p => Console.WriteLine("  " + p)));
    Directory.CreateDirectory(outDir);
    var file = Path.Combine(outDir, "REPORT.md");
    File.WriteAllText(file, report);
    Console.WriteLine($"{packages.Count} packages -> {file}");
    return 0;
}

int RunModDiff()
{
    var modDir = Opt("--mod") ?? throw new ArgumentException("--mod DIR is required");
    var outDir = Opt("--out") ?? Path.Combine(config.WorkDir, "out", "experiment", "moddiff");
    var sw = Stopwatch.StartNew();
    using var stock = Side.Open(pcConfig);
    using var modded = Side.Open(pcConfig with { ExtraPaksDirs = [modDir] });
    Console.WriteLine($"opened PC stock and PC + mod in {sw.ElapsedMilliseconds} ms (fingerprints {stock.Source.Fingerprint} / {modded.Source.Fingerprint})");
    var r = ModDiff.Run(stock, modded, modDir, new Progress<string>(Console.WriteLine));
    Directory.CreateDirectory(outDir);
    File.WriteAllText(Path.Combine(outDir, "moddiff.json"), JsonSerializer.Serialize(r, new JsonSerializerOptions { WriteIndented = true }));
    File.WriteAllText(Path.Combine(outDir, "REPORT.md"), ModDiff.Report(r));
    Console.WriteLine($"widgets {r.Widgets.Count}, anim-key changes in {r.AnimationKeys.Count}, textures {r.Textures.Count}, other {r.OtherPackages.Count}, new {r.NewPackages.Count}, loose {r.LooseFiles.Count}, errors {r.Errors.Count}, {sw.ElapsedMilliseconds} ms");
    Console.WriteLine($"-> {outDir}");
    return r.Errors.Count == 0 ? 0 : 1;
}

int AnimKeys()
{
    using var source = GameSource.Open(SourceConfig());
    var animFilter = Opt("--anim") is { } re ? new System.Text.RegularExpressions.Regex(re) : null;
    var widget = Opt("--widget");
    var anims = UmgEditor.Core.Animation.AnimationReader.Read(source, args[1], animFilter == null ? null : n => animFilter.IsMatch(n));
    Console.WriteLine($"{source.Platform.Name} {args[1]}: {anims.Count} animations");
    var mismatches = 0;
    foreach (var a in anims)
    {
        var tracks = a.Tracks.Where(t => widget == null || t.Widget == widget).ToList();
        if (tracks.Count == 0) continue;
        Console.WriteLine($"== {a.Name}  playback {a.Seconds(a.Start):0.###}s..{a.Seconds(a.End):0.###}s");
        foreach (var t in tracks)
        {
            Console.WriteLine($"  {t.Widget}{(t.OnSlot ? " [slot]" : "")}  {t.Track} {t.Property}  section {a.Seconds(t.SectionStart):0.###}..{a.Seconds(t.SectionEnd):0.###}s  {t.CompletionMode}  (export {t.SectionExport})");
            foreach (var c in t.Channels.Where(c => c.Keys.Count > 0 || c.Default != null))
            {
                var keys = string.Join("  ", c.Keys.Select(k => $"{k.Seconds:0.###}s={k.Value}"));
                Console.WriteLine($"    {c.Path,-22} {keys}{(c.Default != null ? $"  default={c.Default}" : "")}{(c.Compiled == "same" ? "" : "  !! " + c.Compiled)}");
                if (c.Compiled != "same") mismatches++;
            }
        }
    }
    Console.WriteLine(mismatches == 0 ? "compiled copies: all same" : $"compiled copies: {mismatches} channel(s) differ or missing");
    return 0;
}

int AnimDump(bool all = false)
{
    using var source = GameSource.Open(SourceConfig());
    var pkg = source.LoadPackage(args[1]);
    var dump = new List<object>();
    foreach (var e in pkg.GetExports())
    {
        if (!all && !e.ExportType.Contains("MovieScene") && e.ExportType != "WidgetAnimation") continue;
        var outer = new List<string>();
        for (var o = e.Outer; o != null; o = o.Outer) outer.Add(o.Name.Text);
        dump.Add(new { e.Name, e.ExportType, Outer = string.Join(" < ", outer), Json = Newtonsoft.Json.Linq.JToken.FromObject(e).ToString() });
    }
    File.WriteAllText(args[2], Newtonsoft.Json.JsonConvert.SerializeObject(dump, Newtonsoft.Json.Formatting.Indented));
    Console.WriteLine($"{dump.Count} exports -> {args[2]}");
    return 0;
}

int CheckPc()
{
    var sw = Stopwatch.StartNew();
    using var pc = Side.Open(pcConfig);
    Console.WriteLine($"PC: {pc.Source.Provider.MountedVfs.Count} containers, {pc.Source.Provider.Files.Count} files, index {pc.Index.Count} entries, {sw.ElapsedMilliseconds} ms, fingerprint {pc.Source.Fingerprint}");
    foreach (var g in pc.Index.GroupBy(e => e.Kind).OrderBy(g => g.Key)) Console.WriteLine($"  {g.Key,-9} {g.Count()}");
    Check("PC containers detected as PC", pc.Source.Platform == GamePlatform.Pc, pc.Source.Platform.Name);
    Check("no PC UI packages failed to read", pc.Index.All(e => e.Kind != UiAssetKind.Error),
        $"{pc.Index.Count(e => e.Kind == UiAssetKind.Error)}: " + string.Join("; ", pc.Index.Where(e => e.Kind == UiAssetKind.Error).Take(3).Select(e => e.Name + ": " + e.MainClass)));
    var widgets = pc.Widgets.Count;
    Check("PC has widget packages under ui/Data/Widget", widgets > 1000, widgets.ToString());
    Console.WriteLine($"  Windows overrides under ui/: {pc.Index.Count(e => e.IsPlatformOverride)}");

    var mtPath = Tv + "MatchTime/MatchTime";
    Check("PC MatchTime is a Widget", pc.Widgets.TryGetValue(mtPath, out var mt), mt == null ? "missing" : $"{mt.Container}, {mt.ExportCount} exports");
    var pkg = pc.Source.LoadPackage(mtPath);
    int parsed = 0, total = 0; string? error = null;
    foreach (var lazy in pkg.ExportsLazy) { total++; try { _ = lazy.Value; parsed++; } catch (Exception e) { error ??= e.Message; } }
    Check("all PC MatchTime exports parse", parsed == total, $"{parsed}/{total} {error}");

    var tree = pc.Tree(mtPath)!;
    Console.WriteLine($"PC MatchTime tree: {tree.WidgetCount} widgets, {tree.AnimationNames.Count} animations, {tree.Unattached.Count} unattached");
    Check("PC MatchTime tree has a root", tree.Root != null, tree.Root?.Name ?? "none");
    var mask = tree.Find("Mask");
    Check("PC MatchTime has Mask", mask?.Slot != null, string.Join(", ", mask?.Slot?.Fields.Where(f => f.Path.Contains("Offsets")).Select(f => $"{f.Path[(f.Path.LastIndexOf('.') + 1)..]} {(f.Stored ? "" : "default ")}{f.Value}") ?? []));

    var scale = UiScaleSettings.Load(pc.Source);
    Console.WriteLine($"PC UI scale: rule {scale.Rule}, curve [{string.Join(", ", scale.Curve.Select(k => $"{k.Time}->{k.Value}"))}], {string.Join("; ", scale.Origins)}");
    Check("PC UI scale read from config", scale.Source == ValueSource.Config);
    var layout = pc.Arrange(tree, 1920, 1080);
    var maskBox = layout.Find("Mask");
    Console.WriteLine($"PC MatchTime at 1920x1080: DPI {layout.DpiScale:0.####}, {string.Join(", ", layout.ScaleBoxes.Select(s => $"{s.Widget} {s.Applied:0.####}"))}; Mask px {maskBox?.Bounds}");

    sw.Restart();
    using var mobile = Side.Open(config);
    Check("mobile containers still detected as Mobile", mobile.Source.Platform == GamePlatform.Mobile, $"{mobile.Source.Platform.Name}, {sw.ElapsedMilliseconds} ms");
    Check("mobile MatchTime resolves through the same code", mobile.Widgets.ContainsKey(mtPath));

    Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
    return failures == 0 ? 0 : 1;
}

int Compare()
{
    var focus = Opt("--focus") ?? "TVGraphics/SwitchAssets_Original/MatchTime";
    var outDir = Opt("--out") ?? Path.Combine(config.WorkDir, "out", "experiment", "compare");
    var sw = Stopwatch.StartNew();
    using var pc = Side.Open(pcConfig);
    using var mobile = Side.Open(config);
    Console.WriteLine($"opened PC ({pc.Widgets.Count} widgets) and mobile ({mobile.Widgets.Count} widgets) in {sw.ElapsedMilliseconds} ms");

    var under = (Side s) => s.Widgets.Keys.Where(k => k.StartsWith(WidgetRoot, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var pcSet = under(pc);
    var mobileSet = under(mobile);
    var both = pcSet.Intersect(mobileSet, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    var onlyPc = pcSet.Except(mobileSet, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
    var onlyMobile = mobileSet.Except(pcSet, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    sw.Restart();
    var diffs = new ConcurrentBag<PackageDiff>();
    var done = 0;
    Parallel.ForEach(both, path =>
    {
        diffs.Add(PackageDiff.Of(path, pc, mobile));
        var n = Interlocked.Increment(ref done);
        if (n % 250 == 0) Console.WriteLine($"  {n}/{both.Count}");
    });
    var ordered = diffs.OrderBy(d => d.Package, StringComparer.OrdinalIgnoreCase).ToList();
    Console.WriteLine($"compared {ordered.Count} packages in {sw.ElapsedMilliseconds} ms");

    Directory.CreateDirectory(outDir);
    var json = new JsonSerializerOptions { WriteIndented = true };
    File.WriteAllText(Path.Combine(outDir, "compare.json"), JsonSerializer.Serialize(new { onlyPc, onlyMobile, packages = ordered }, json));
    File.WriteAllText(Path.Combine(outDir, "REPORT.md"), Report.Write(ordered, onlyPc, onlyMobile, focus, pc, mobile));
    Console.WriteLine($"identical {ordered.Count(d => d.Identical)}, different {ordered.Count(d => !d.Identical && d.Error == null)}, errors {ordered.Count(d => d.Error != null)}; only PC {onlyPc.Count}, only mobile {onlyMobile.Count}");
    Console.WriteLine($"-> {outDir}");
    return 0;
}

/// <summary>One build's stock containers: index, widget trees and layout, all read-only.</summary>
sealed class Side : IDisposable
{
    public required GameSource Source { get; init; }
    public required IReadOnlyList<UiAssetEntry> Index { get; init; }
    /// <summary>Widget packages by /Game path (exact letter case first, as the provider keys).</summary>
    public required Dictionary<string, UiAssetEntry> Widgets { get; init; }
    private readonly ConcurrentDictionary<string, WidgetPackageTree?> _trees = new(StringComparer.OrdinalIgnoreCase);
    private TextureCache? _textures;
    private LayoutEngine? _engine;

    public static Side Open(GameConfig config)
    {
        var source = GameSource.Open(config);
        var index = UiAssetIndex.LoadOrBuild(source);
        var widgets = new Dictionary<string, UiAssetEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in index.Where(e => e.Kind == UiAssetKind.Widget)) widgets.TryAdd(GamePaths.ToPackagePath(e.Path), e);
        return new Side { Source = source, Index = index, Widgets = widgets };
    }

    public WidgetPackageTree? Tree(string path) =>
        _trees.GetOrAdd(path, p => { try { return WidgetTreeReader.Build(Source, p); } catch { return null; } });

    public LayoutResult Arrange(WidgetPackageTree tree, int w, int h)
    {
        _textures ??= new TextureCache(Source, new AssetRipperTextureDecoder());
        _engine ??= new LayoutEngine(UiScaleSettings.Load(Source),
            p => { try { return WidgetTreeReader.Build(Source, p, readOnly: true); } catch { return null; } },
            t => _textures.Get(t) is { IsPlaceholder: false } img ? (img.Width, img.Height) : null);
        lock (_engine) return _engine.Arrange(tree, w, h);
    }

    public void Dispose() => Source.Dispose();
}

sealed record FieldDiff(string Widget, string Field, string Pc, string Mobile);
sealed record BoxDiff(string Widget, string Pc, string Mobile, double Dx, double Dy, double Dw, double Dh);
sealed record TextureDiff(string Widget, string? Pc, string? Mobile, bool SameArtName);

sealed class PackageDiff
{
    public required string Package { get; init; }
    public string? Error { get; init; }
    public int PcExports { get; init; }
    public int MobileExports { get; init; }
    public List<string> WidgetsOnlyPc { get; } = [];
    public List<string> WidgetsOnlyMobile { get; } = [];
    public List<FieldDiff> Changed { get; } = [];
    public List<string> AnimationsOnlyPc { get; } = [];
    public List<string> AnimationsOnlyMobile { get; } = [];
    public List<FieldDiff> AnimationTracks { get; } = [];
    public List<TextureDiff> Textures { get; } = [];
    public List<BoxDiff> Boxes { get; } = [];

    public bool Identical => Error == null && WidgetsOnlyPc.Count + WidgetsOnlyMobile.Count + Changed.Count + AnimationsOnlyPc.Count
        + AnimationsOnlyMobile.Count + AnimationTracks.Count + Boxes.Count + Textures.Count(t => !t.SameArtName) == 0;

    /// <summary>Texture paths with the resolution folder and platform folder made neutral (Tex720 ↔ Tex1080/Tex2160, _Android ↔ _Windows).</summary>
    public static string ArtName(string path) => System.Text.RegularExpressions.Regex.Replace(path, @"/(Tex\d+|Platforms/_\w+)/", "/*/", System.Text.RegularExpressions.RegexOptions.IgnoreCase).ToLowerInvariant();

    public static PackageDiff Of(string path, Side pc, Side mobile)
    {
        var pt = pc.Tree(path);
        var mt = mobile.Tree(path);
        if (pt?.Root == null || mt?.Root == null)
            return new PackageDiff { Package = path, Error = $"tree: PC {(pt == null ? "failed" : pt.Root == null ? "rootless" : "ok")}, mobile {(mt == null ? "failed" : mt.Root == null ? "rootless" : "ok")}" };

        var d = new PackageDiff { Package = path, PcExports = pc.Widgets[path].ExportCount, MobileExports = mobile.Widgets[path].ExportCount };
        // own widgets only: embedded packages are compared as packages of their own
        Dictionary<string, WidgetNode> Own(WidgetPackageTree t)
        {
            var map = new Dictionary<string, WidgetNode>();
            foreach (var n in t.All().Where(n => n.PackagePath.Equals(t.PackagePath, StringComparison.OrdinalIgnoreCase))) map.TryAdd(n.Name, n);
            return map;
        }
        var pn = Own(pt);
        var mn = Own(mt);
        d.WidgetsOnlyPc.AddRange(pn.Keys.Except(mn.Keys).Order());
        d.WidgetsOnlyMobile.AddRange(mn.Keys.Except(pn.Keys).Order());

        var pParent = Parents(pt);
        var mParent = Parents(mt);
        foreach (var name in pn.Keys.Intersect(mn.Keys).Order())
        {
            var (a, b) = (pn[name], mn[name]);
            if (a.ClassName != b.ClassName) d.Changed.Add(new(name, "(class)", a.ClassName, b.ClassName));
            if (pParent.GetValueOrDefault(name) != mParent.GetValueOrDefault(name)) d.Changed.Add(new(name, "(parent)", pParent.GetValueOrDefault(name) ?? "-", mParent.GetValueOrDefault(name) ?? "-"));
            if (a.ClipsToBounds != b.ClipsToBounds) d.Changed.Add(new(name, "(clips)", a.ClipsToBounds.ToString(), b.ClipsToBounds.ToString()));
            if (a.EmbeddedPackagePath != b.EmbeddedPackagePath) d.Changed.Add(new(name, "(embeds)", a.EmbeddedPackagePath ?? "-", b.EmbeddedPackagePath ?? "-"));
            var af = (a.Slot?.Fields ?? []).GroupBy(f => f.Path).ToDictionary(g => g.Key, g => g.First());
            var bf = (b.Slot?.Fields ?? []).GroupBy(f => f.Path).ToDictionary(g => g.Key, g => g.First());
            foreach (var f in af.Keys.Union(bf.Keys).Order())
            {
                var x = af.TryGetValue(f, out var fa) ? (fa.Stored ? "" : "default ") + fa.Value : "-";
                var y = bf.TryGetValue(f, out var fb) ? (fb.Stored ? "" : "default ") + fb.Value : "-";
                if (x != y) d.Changed.Add(new(name, "slot." + f, x, y));
            }
            var ap = a.StoredProperties.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value);
            var bp = b.StoredProperties.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value);
            foreach (var k in ap.Keys.Union(bp.Keys).Order())
            {
                var x = ap.GetValueOrDefault(k) ?? "-";
                var y = bp.GetValueOrDefault(k) ?? "-";
                if (x == y) continue;
                if (x != "-" && y != "-" && ArtName(x) == ArtName(y)) continue; // same art, resolution folder only
                d.Changed.Add(new(name, k, Short(x), Short(y)));
            }
            // one animation can drive both the widget and its slot: one entry each
            static Dictionary<string, string> Tracks(WidgetNode n) => n.Animations.GroupBy(u => u.Animation)
                .ToDictionary(g => g.Key, g => string.Join(" + ", g.Select(u => (u.OnSlot ? "slot: " : "") + string.Join(", ", u.Tracks.Order())).Order()));
            var aa = Tracks(a);
            var ba = Tracks(b);
            foreach (var k in aa.Keys.Union(ba.Keys).Order())
                if (aa.GetValueOrDefault(k) != ba.GetValueOrDefault(k)) d.AnimationTracks.Add(new(name, k, aa.GetValueOrDefault(k) ?? "-", ba.GetValueOrDefault(k) ?? "-"));
        }
        d.AnimationsOnlyPc.AddRange(pt.AnimationNames.Except(mt.AnimationNames).Order());
        d.AnimationsOnlyMobile.AddRange(mt.AnimationNames.Except(pt.AnimationNames).Order());

        // same viewport on both, so box differences are design differences (not scale)
        try
        {
            var pl = pc.Arrange(pt, 1920, 1080);
            var ml = mobile.Arrange(mt, 1920, 1080);
            var pb = BoxesOf(pl, pt.PackagePath);
            var mb = BoxesOf(ml, mt.PackagePath);
            foreach (var k in pb.Keys.Intersect(mb.Keys).Order())
            {
                var (a, b) = (pb[k], mb[k]);
                if (!string.Equals(a.TexturePath, b.TexturePath, StringComparison.OrdinalIgnoreCase))
                    d.Textures.Add(new(k, a.TexturePath, b.TexturePath, a.TexturePath != null && b.TexturePath != null && ArtName(a.TexturePath) == ArtName(b.TexturePath)));
                var (ra, rb) = (a.Bounds, b.Bounds);
                double dx = rb.X - ra.X, dy = rb.Y - ra.Y, dw = rb.Width - ra.Width, dh = rb.Height - ra.Height;
                if (Math.Max(Math.Max(Math.Abs(dx), Math.Abs(dy)), Math.Max(Math.Abs(dw), Math.Abs(dh))) > 0.5)
                    d.Boxes.Add(new(k, Rect(ra), Rect(rb), Math.Round(dx, 1), Math.Round(dy, 1), Math.Round(dw, 1), Math.Round(dh, 1)));
            }
        }
        catch (Exception e) { d.Changed.Add(new("(layout)", "error", e.Message, "")); }
        return d;
    }

    private static Dictionary<string, string> Parents(WidgetPackageTree t)
    {
        var map = new Dictionary<string, string>();
        foreach (var n in t.All()) foreach (var c in n.Children) map.TryAdd(c.Name, n.Name);
        return map;
    }

    private static Dictionary<string, LayoutBox> BoxesOf(LayoutResult r, string package)
    {
        var map = new Dictionary<string, LayoutBox>();
        foreach (var b in r.All().Where(b => b.Node.PackagePath.Equals(package, StringComparison.OrdinalIgnoreCase))) map.TryAdd(b.Node.Name, b);
        return map;
    }

    private static string Rect(RectangleF r) => $"{r.X:0.#},{r.Y:0.#} {r.Width:0.#}x{r.Height:0.#}";
    private static string Short(string v) => v.Length > 140 ? v[..140] + "…" : v;
}

static class Report
{
    public static string Write(List<PackageDiff> diffs, List<string> onlyPc, List<string> onlyMobile, string focus, Side pc, Side mobile)
    {
        var sb = new StringBuilder();
        string Rel(string p) => p.StartsWith("/Game/Assets/ui/Data/Widget/", StringComparison.OrdinalIgnoreCase) ? p["/Game/Assets/ui/Data/Widget/".Length..] : p;
        var pcScale = UiScaleSettings.Load(pc.Source);
        var mobileScale = UiScaleSettings.Load(mobile.Source);
        sb.AppendLine("# PC ↔ mobile UI comparison").AppendLine();
        sb.AppendLine($"PC: `{pc.Source.Config.PaksDir}` (fingerprint {pc.Source.Fingerprint}); mobile: `{mobile.Source.Config.PaksDir}` (fingerprint {mobile.Source.Fingerprint}).");
        sb.AppendLine($"Both laid out at 1920×1080. UI scale PC: {pcScale.Rule} [{string.Join(", ", pcScale.Curve.Select(k => $"{k.Time}→{k.Value}"))}]; mobile: {mobileScale.Rule} [{string.Join(", ", mobileScale.Curve.Select(k => $"{k.Time}→{k.Value}"))}].").AppendLine();
        sb.AppendLine("Values: `default X` = not stored in the file (engine default); `-` = absent. Columns are PC → mobile.").AppendLine();

        sb.AppendLine("## Summary (widget packages under ui/Data/Widget)").AppendLine();
        sb.AppendLine("| | count |").AppendLine("|---|---|");
        sb.AppendLine($"| on both | {diffs.Count} |");
        sb.AppendLine($"| identical (tree, slots, stored props, animation tracks, boxes, art) | {diffs.Count(d => d.Identical)} |");
        sb.AppendLine($"| different | {diffs.Count(d => !d.Identical && d.Error == null)} |");
        sb.AppendLine($"| tree not readable on one side | {diffs.Count(d => d.Error != null)} |");
        sb.AppendLine($"| PC only | {onlyPc.Count} |");
        sb.AppendLine($"| mobile only | {onlyMobile.Count} |").AppendLine();
        var diff = diffs.Where(d => !d.Identical && d.Error == null).ToList();
        sb.AppendLine("Kinds of difference (packages affected):").AppendLine();
        sb.AppendLine($"- widgets only on one side: {diff.Count(d => d.WidgetsOnlyPc.Count + d.WidgetsOnlyMobile.Count > 0)}");
        sb.AppendLine($"- slot values: {diff.Count(d => d.Changed.Any(c => c.Field.StartsWith("slot.")))}");
        sb.AppendLine($"- other stored properties: {diff.Count(d => d.Changed.Any(c => !c.Field.StartsWith("slot.") && !c.Field.StartsWith("(")))}");
        sb.AppendLine($"- class/parent/clip/embed: {diff.Count(d => d.Changed.Any(c => c.Field.StartsWith("(")))}");
        sb.AppendLine($"- animations or tracks: {diff.Count(d => d.AnimationsOnlyPc.Count + d.AnimationsOnlyMobile.Count + d.AnimationTracks.Count > 0)}");
        sb.AppendLine($"- different art (not just Tex720 ↔ Tex1080): {diff.Count(d => d.Textures.Any(t => !t.SameArtName))}");
        sb.AppendLine($"- boxes at 1920×1080: {diff.Count(d => d.Boxes.Count > 0)}").AppendLine();

        sb.AppendLine($"## Focus: `{focus}`").AppendLine();
        foreach (var d in diffs.Where(d => d.Package.Contains(focus, StringComparison.OrdinalIgnoreCase)))
        {
            sb.AppendLine($"### {Rel(d.Package)}").AppendLine();
            if (d.Error != null) { sb.AppendLine(d.Error).AppendLine(); continue; }
            if (d.Identical) { sb.AppendLine($"identical ({d.PcExports} / {d.MobileExports} exports)").AppendLine(); continue; }
            sb.AppendLine($"exports PC {d.PcExports}, mobile {d.MobileExports}").AppendLine();
            if (d.WidgetsOnlyPc.Count > 0) sb.AppendLine($"- widgets only on PC: {string.Join(", ", d.WidgetsOnlyPc)}");
            if (d.WidgetsOnlyMobile.Count > 0) sb.AppendLine($"- widgets only on mobile: {string.Join(", ", d.WidgetsOnlyMobile)}");
            if (d.AnimationsOnlyPc.Count > 0) sb.AppendLine($"- animations only on PC: {string.Join(", ", d.AnimationsOnlyPc)}");
            if (d.AnimationsOnlyMobile.Count > 0) sb.AppendLine($"- animations only on mobile: {string.Join(", ", d.AnimationsOnlyMobile)}");
            if (d.Changed.Count > 0)
            {
                sb.AppendLine().AppendLine("| widget | field | PC | mobile |").AppendLine("|---|---|---|---|");
                foreach (var c in d.Changed) sb.AppendLine($"| {c.Widget} | {c.Field} | {Cell(c.Pc)} | {Cell(c.Mobile)} |");
            }
            if (d.AnimationTracks.Count > 0)
            {
                sb.AppendLine().AppendLine("| widget | animation | PC tracks | mobile tracks |").AppendLine("|---|---|---|---|");
                foreach (var c in d.AnimationTracks) sb.AppendLine($"| {c.Widget} | {c.Field} | {Cell(c.Pc)} | {Cell(c.Mobile)} |");
            }
            if (d.Textures.Any(t => !t.SameArtName))
            {
                sb.AppendLine().AppendLine("| widget | PC texture | mobile texture |").AppendLine("|---|---|---|");
                foreach (var t in d.Textures.Where(t => !t.SameArtName)) sb.AppendLine($"| {t.Widget} | {Cell(t.Pc ?? "-")} | {Cell(t.Mobile ?? "-")} |");
            }
            if (d.Boxes.Count > 0)
            {
                sb.AppendLine().AppendLine("| widget | PC box (px @1920×1080) | mobile box | Δx | Δy | Δw | Δh |").AppendLine("|---|---|---|---|---|---|---|");
                foreach (var b in d.Boxes) sb.AppendLine($"| {b.Widget} | {b.Pc} | {b.Mobile} | {b.Dx} | {b.Dy} | {b.Dw} | {b.Dh} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## All different packages").AppendLine();
        sb.AppendLine("| package | widgets ± | changed fields | anims | art | boxes |").AppendLine("|---|---|---|---|---|---|");
        foreach (var d in diff.OrderBy(d => d.Package, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"| {Rel(d.Package)} | +{d.WidgetsOnlyPc.Count}/−{d.WidgetsOnlyMobile.Count} | {d.Changed.Count} | {d.AnimationsOnlyPc.Count + d.AnimationsOnlyMobile.Count + d.AnimationTracks.Count} | {d.Textures.Count(t => !t.SameArtName)} | {d.Boxes.Count} |");
        sb.AppendLine().AppendLine("(widgets ±: +only on PC / −only on mobile)").AppendLine();
        if (diffs.Any(d => d.Error != null))
        {
            sb.AppendLine("## Not readable on one side").AppendLine();
            foreach (var d in diffs.Where(d => d.Error != null)) sb.AppendLine($"- {Rel(d.Package)}: {d.Error}");
            sb.AppendLine();
        }
        sb.AppendLine("## PC only").AppendLine();
        foreach (var g in onlyPc.GroupBy(p => Rel(p).Split('/')[0])) sb.AppendLine($"- {g.Key} ({g.Count()}): {string.Join(", ", g.Select(GamePaths.ShortName).Take(40))}{(g.Count() > 40 ? ", …" : "")}");
        sb.AppendLine().AppendLine("## Mobile only").AppendLine();
        foreach (var g in onlyMobile.GroupBy(p => Rel(p).Split('/')[0])) sb.AppendLine($"- {g.Key} ({g.Count()}): {string.Join(", ", g.Select(GamePaths.ShortName).Take(40))}{(g.Count() > 40 ? ", …" : "")}");
        return sb.ToString();
    }

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
