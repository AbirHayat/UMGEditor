// Smoke checks for UmgEditor.Core against the stock containers (read-only).
// Exit code 0 = all checks passed.
using System.Diagnostics;
using UmgEditor.Core;
using UmgEditor.Core.Textures;
using UmgEditor.Core.Layout;

var config = GameConfig.LoadOrDefault(Path.Combine(AppContext.BaseDirectory, "umgeditor.json"));
var failures = 0;
void Check(string what, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}");
    if (!ok) failures++;
}

var sw = Stopwatch.StartNew();
using var source = GameSource.Open(config);
Console.WriteLine($"opened {source.Provider.MountedVfs.Count} containers, {source.Provider.Files.Count} files in {sw.ElapsedMilliseconds} ms, fingerprint {source.Fingerprint}");

if (args.Contains("--ini"))
{
    foreach (var ini in new[] { "PesMobile/Config/DefaultEngine.ini", "Engine/Config/BaseEngine.ini" })
    {
        if (!source.Provider.TrySaveAsset(ini, out var bytes)) { Console.WriteLine($"{ini}: not found"); continue; }
        var lines = System.Text.Encoding.UTF8.GetString(bytes).Split('\n');
        var at = Array.FindIndex(lines, l => l.Trim().Equals("[/Script/Engine.UserInterfaceSettings]", StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"--- {ini} ({bytes.Length} bytes): " + (at < 0 ? "no UserInterfaceSettings section" : ""));
        if (at >= 0) foreach (var l in lines.Skip(at).TakeWhile((l, i) => i == 0 || !l.TrimStart().StartsWith('['))) Console.WriteLine("  " + l.TrimEnd());
    }
    return 0;
}

sw.Restart();
var rebuild = args.Contains("--rebuild");
var index = rebuild ? UiAssetIndex.Build(source) : UiAssetIndex.LoadOrBuild(source);
Console.WriteLine($"index: {index.Count} entries in {sw.ElapsedMilliseconds} ms{(rebuild ? " (rebuilt, cache untouched)" : "")}");
foreach (var g in index.GroupBy(e => e.Kind).OrderBy(g => g.Key)) Console.WriteLine($"  {g.Key,-9} {g.Count()}");

var dataWidget = index.Where(e => e.Path.StartsWith("PesMobile/Content/Assets/ui/Data/Widget/", StringComparison.OrdinalIgnoreCase)).ToList();
Check("5,181 packages under Assets/ui/Data/Widget (recon survey)", dataWidget.Count == 5181, dataWidget.Count.ToString());
var androidWidget = index.Count(e => e.IsPlatformOverride && e.Path.Contains("/ui/Data/Widget/", StringComparison.OrdinalIgnoreCase));
Check("3 Android override textures under ui/Data/Widget", androidWidget == 3, androidWidget.ToString());
Check("ui/Widget legacy folder present", index.Any(e => e.Group == "ui/Widget"));
Check("no packages failed to read", index.All(e => e.Kind != UiAssetKind.Error),
    string.Join("; ", index.Where(e => e.Kind == UiAssetKind.Error).Take(3).Select(e => e.Name + ": " + e.MainClass)));

var matchTime = index.FirstOrDefault(e => e.Path.EndsWith("/SwitchAssets_Original/MatchTime/MatchTime.uasset"));
Check("MatchTime is a Widget from pc1000_mobile_and", matchTime is { Kind: UiAssetKind.Widget, Container: "pc1000_mobile_and.utoc", ExportCount: 1473 },
    matchTime == null ? "missing" : $"{matchTime.Kind}, {matchTime.Container}, {matchTime.ExportCount} exports");
var menuCall = index.FirstOrDefault(e => e.Path.EndsWith("/TVGraphicsCommon/MenuCall/MenuCall.uasset"));
Check("MenuCall comes from the newer pc0010_mobile_and", menuCall?.Container == "pc0010_mobile_and.utoc", menuCall?.Container ?? "missing");
var logo = index.FirstOrDefault(e => e.Path.EndsWith("/MatchTime/Tex720/MatchTimeLogo.uasset"));
Check("MatchTimeLogo is a Texture2D", logo is { Kind: UiAssetKind.Texture, MainClass: "Texture2D" }, logo?.MainClass ?? "missing");
Check("bulk image sets listed but not opened", index.Where(e => e.IsBulkImageSet).All(e => !e.Inspected) && index.Count(e => e.IsBulkImageSet) > 70000,
    index.Count(e => e.IsBulkImageSet).ToString());

// full property parse (needs block decompression of the export data, not just the header)
var mtPkg = source.Provider.LoadPackage(matchTime!.Path);
var parsed = 0; string? parseError = null;
foreach (var lazy in mtPkg.ExportsLazy)
{
    try { _ = lazy.Value; parsed++; }
    catch (Exception e) { parseError ??= e.Message; }
}
Check("all MatchTime exports parse (properties)", parsed == 1473, $"{parsed}/1473 {parseError}");
Check("no native zlib loaded", CUE4Parse.Compression.ZlibHelper.Instance == null);

// ---- feature 2: widget tree ----
const string Tv = "/Game/Assets/ui/Data/Widget/TVGraphics/SwitchAssets_Original/";
sw.Restart();
var tree = WidgetTreeReader.Build(source, Tv + "MatchTime/MatchTime");
Console.WriteLine($"MatchTime tree: {tree.WidgetCount} widgets, {tree.AnimationNames.Count} animations, {tree.Unattached.Count} unattached, {sw.ElapsedMilliseconds} ms");
Check("MatchTime root is CanvasPanel_0", tree.Root?.Name == "CanvasPanel_0", tree.Root?.Name ?? "none");
Check("no unattached widgets", tree.Unattached.Count == 0, string.Join(",", tree.Unattached.Select(n => n.Name)));

var mask = tree.Find("Mask");
string F(WidgetNode? n, string path) => n?.Slot?.Fields.FirstOrDefault(f => f.Path == path) is { } f ? $"{(f.Stored ? "stored" : "default")} {f.Value}" : "missing";
Check("Mask slot is CanvasPanelSlot_29", mask?.Slot is { Name: "CanvasPanelSlot_29", ClassName: "CanvasPanelSlot" }, mask?.Slot?.Name ?? "none");
Check("Mask Left/Top/Right/Bottom stored 129/7/114/64",
    F(mask, "LayoutData.Offsets.Left") == "stored 129" && F(mask, "LayoutData.Offsets.Top") == "stored 7"
    && F(mask, "LayoutData.Offsets.Right") == "stored 114" && F(mask, "LayoutData.Offsets.Bottom") == "stored 64",
    $"{F(mask, "LayoutData.Offsets.Left")}, {F(mask, "LayoutData.Offsets.Top")}");
Check("Mask anchors not stored (defaults)", F(mask, "LayoutData.Anchors.Minimum.X") == "default 0");
Check("Mask is not animated, clips to bounds", mask is { IsAnimated: false, ClipsToBounds: true });
var home = tree.Find("Text_Score_Home");
Check("Text_Score_Home: Left/Top not stored, Right stored 54", F(home, "LayoutData.Offsets.Left") == "default 0" && F(home, "LayoutData.Offsets.Right") == "stored 54",
    $"{F(home, "LayoutData.Offsets.Left")}, {F(home, "LayoutData.Offsets.Right")}");
Check("Text_Score_Home is animated", home?.IsAnimated == true,
    string.Join("; ", home?.Animations.Select(a => $"{a.Animation}: {string.Join(",", a.Tracks)}{(a.OnSlot ? " [slot]" : "")}") ?? []));
var timer = tree.Find("Time_Timer");
Check("Time_Timer embeds MatchTimeTimer", timer?.EmbeddedPackagePath == Tv + "MatchTime/MatchTimeTimer", timer?.EmbeddedPackagePath ?? "none");
var embedded = WidgetTreeReader.Build(source, timer!.EmbeddedPackagePath!, readOnly: true);
Check("embedded MatchTimeTimer tree loads read-only", embedded.Root != null && embedded.All().All(n => n.IsReadOnly), $"{embedded.WidgetCount} widgets");

var graph = new UsageGraph(index);
string Boards(string p) => string.Join(" + ", graph.ScoreboardsUsing(p).Select(b => b.Label));
Check("MatchTime → offline scoreboard only", Boards(Tv + "MatchTime/MatchTime") == "Offline scoreboard", Boards(Tv + "MatchTime/MatchTime"));
Check("MatchTimeBase → offline + online", Boards(Tv + "MatchTime/MatchTimeBase") == "Offline scoreboard + Online scoreboard", Boards(Tv + "MatchTime/MatchTimeBase"));
Check("MatchTimeConnectionTimer → online only", Boards(Tv + "MatchTimeConnection/MatchTimeConnectionTimer") == "Online scoreboard", Boards(Tv + "MatchTimeConnection/MatchTimeConnectionTimer"));
var uiText = "/Game/Assets/ui/library/uiText";
Check("uiText is used by all four boards", graph.ScoreboardsUsing(uiText).Count == 4, Boards(uiText));

var allWidgets = index.Where(e => e.Kind == UiAssetKind.Widget).ToList();
var treeFailures = new List<string>();
var rootless = new List<string>();
sw.Restart();
Parallel.ForEach(allWidgets, e =>
{
    try
    {
        var t = WidgetTreeReader.Build(source, GamePaths.ToPackagePath(e.Path));
        if (t.Root == null)
        {
            var hasTree = source.Provider.LoadPackage(e.Path).GetExports().Any(o => o.ExportType == "WidgetTree");
            lock (treeFailures) rootless.Add(e.Name + ": " + (hasTree ? "WidgetTree has no RootWidget" : "no WidgetTree export"));
        }
    }
    catch (Exception ex) { lock (treeFailures) treeFailures.Add(e.Name + ": " + ex.Message); }
});
Console.WriteLine($"rootless widget blueprints: {rootless.Count}"); foreach (var r in rootless.Order()) Console.WriteLine("  " + r);
Check($"all {allWidgets.Count} widget trees build", treeFailures.Count == 0, $"{treeFailures.Count} failed, {sw.ElapsedMilliseconds} ms; {string.Join(" | ", treeFailures.Take(3))}");

// ---- feature 3: textures ----
var texCache = new TextureCache(source, new AssetRipperTextureDecoder());
var uiTextures = index.Where(e => e.Kind == UiAssetKind.Texture && e.Inspected).ToList();
var formats = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
var placeholders = new System.Collections.Concurrent.ConcurrentBag<string>();
var stats = new System.Collections.Concurrent.ConcurrentBag<(string Path, DecodedImage Img, double Alpha, double Sat)>();
sw.Restart();
Parallel.ForEach(uiTextures, e =>
{
    var img = texCache.Get(GamePaths.ToPackagePath(e.Path));
    formats.AddOrUpdate(img.Format, 1, (_, n) => n + 1);
    if (img.IsPlaceholder) { placeholders.Add($"{e.Name}: {img.PlaceholderReason}"); return; }
    int transparent = 0; double sat = 0; int n = img.Width * img.Height;
    for (int i = 0; i < n; i++)
    {
        byte b = img.Bgra[i * 4], g = img.Bgra[i * 4 + 1], r = img.Bgra[i * 4 + 2], a = img.Bgra[i * 4 + 3];
        if (a < 250) transparent++;
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        sat += max == 0 ? 0 : (max - min) / (double) max * (a / 255.0);
    }
    stats.Add((GamePaths.ToPackagePath(e.Path), img, transparent / (double) n, sat / n));
});
Console.WriteLine($"textures: {uiTextures.Count} in {sw.ElapsedMilliseconds} ms, decoded {texCache.Decodes}, disk cache hits {texCache.DiskHits}; formats: {string.Join(", ", formats.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}");
Check("every UI texture decodes (no placeholders)", placeholders.IsEmpty, $"{placeholders.Count}: {string.Join(" | ", placeholders.Take(5))}");
var cache2 = new TextureCache(source, new AssetRipperTextureDecoder());
var again = cache2.Get(Tv + "MatchTime/Tex720/MatchTimeLogo");
Check("second load comes from the disk cache", cache2.DiskHits == 1 && cache2.Decodes == 0 && again.Width == 80, $"hits {cache2.DiskHits}, decodes {cache2.Decodes}");

// --export-tex <outDir> <objectPath>...: write our decodes as PNG for comparison with FModel
var exportAt = Array.IndexOf(args, "--export-tex");
if (exportAt >= 0)
{
    var outDir = args[exportAt + 1];
    Directory.CreateDirectory(outDir);
    foreach (var path in args.Skip(exportAt + 2).TakeWhile(a => !a.StartsWith("--")))
    {
        var img = texCache.Get(path);
        var file = Path.Combine(outDir, GamePaths.ShortName(path) + ".png");
        PngWriter.Write(file, img);
        Console.WriteLine($"exported {img.Format} {img.Width}x{img.Height} -> {file}");
    }
}

// ---- feature 3: layout ----
var uiScale = UiScaleSettings.Load(source);
Console.WriteLine($"UI scale: rule {uiScale.Rule}, curve [{string.Join(", ", uiScale.Curve.Select(k => $"{k.Time}->{k.Value}"))}], ApplicationScale {uiScale.ApplicationScale}, source {uiScale.Source}; {string.Join("; ", uiScale.Origins)}");
Check("UI scale settings read from the game's config", uiScale.Source == ValueSource.Config);
var embeddedTrees = new System.Collections.Concurrent.ConcurrentDictionary<string, WidgetPackageTree?>(StringComparer.OrdinalIgnoreCase);
var engine = new LayoutEngine(uiScale,
    p => embeddedTrees.GetOrAdd(p, k => { try { return WidgetTreeReader.Build(source, k, readOnly: true); } catch { return null; } }),
    t => texCache.Get(t) is { IsPlaceholder: false } img ? (img.Width, img.Height) : null);

string Snapshot(WidgetPackageTree t) => string.Join("|", t.All().Select(n => n.Name + ":" + string.Join(",", n.Slot?.Fields.Select(f => $"{f.Path}={f.Value}{(f.Stored ? "*" : "")}") ?? [])));
var before = Snapshot(tree);
var gameView = engine.Arrange(tree, 1786, 1000);
Check("layout does not change stored data", Snapshot(tree) == before);
var sb = gameView.ScaleBoxes.FirstOrDefault(b => b.Widget == "ScaleBox_0");
var maskBox = gameView.Find("Mask");
var ppu = maskBox?.PixelsPerUnit ?? 0;
Console.WriteLine($"1786x1000: DPI {gameView.DpiScale:0.#####} (rule input {gameView.DpiRuleInput}), ScaleBox_0 {sb?.Stretch} x {sb?.ScaleX:0.#####} y {sb?.ScaleY:0.#####} -> {sb?.Applied:0.#####}, limiting {sb?.LimitingAxis}; Mask {ppu:0.#####} px/unit");
Check("1786x1000 reproduces canary 2's 0.93 px per design unit", Math.Round(ppu, 2) == 0.93, $"{ppu:0.#####} px/unit; DPI {gameView.DpiScale:0.#####} x ScaleBox {sb?.Applied:0.#####}");
Check("ScaleBox_0 is ScaleToFit, height-limited at 1786x1000", sb is { Stretch: "ScaleToFit", LimitingAxis: "height" }, sb?.LimitingAxis ?? "none");
foreach (var (w, h) in new[] { (1920, 1080), (1280, 720), (2400, 1080), (2048, 1536) })
{
    var l = engine.Arrange(tree, w, h);
    var s0 = l.ScaleBoxes.FirstOrDefault(b => b.Widget == "ScaleBox_0");
    Console.WriteLine($"  {w}x{h}: DPI {l.DpiScale:0.####}, ScaleBox_0 {s0?.Applied:0.####} ({s0?.LimitingAxis} limits), Mask {l.Find("Mask")?.PixelsPerUnit:0.####} px/unit, Mask at {l.Find("Mask")?.Bounds}");
}

// uiImage textures that are really materials must come back as labelled placeholders
var materialPackages = index.Where(e => e.Kind == UiAssetKind.Material).Select(e => GamePaths.ToPackagePath(e.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
var materialRefs = new System.Collections.Concurrent.ConcurrentBag<(string Widget, string Path)>();
Parallel.ForEach(allWidgets.Take(2139), e =>
{
    try
    {
        foreach (var n in WidgetTreeReader.Build(source, GamePaths.ToPackagePath(e.Path)).All())
            if (n.ClassName == "uiImage_C" && n.StoredProperties.FirstOrDefault(kv => kv.Key == "texture").Value is { } t && materialPackages.Contains(GamePaths.PackageOf(t)))
                materialRefs.Add((e.Name + "/" + n.Name, t));
    }
    catch { }
});
var matSample = materialRefs.FirstOrDefault();
var matImg = matSample.Path != null ? texCache.Get(matSample.Path) : null;
Console.WriteLine($"uiImage widgets pointing at materials: {materialRefs.Count}; e.g. {matSample.Widget} -> {matSample.Path}: {matImg?.PlaceholderReason}");
Check("material references become 'material' placeholders", materialRefs.IsEmpty || (matImg is { IsPlaceholder: true } && matImg.PlaceholderReason!.Contains("Material")), matImg?.PlaceholderReason ?? "none found");

// canary 2: Mask Left 129 -> 249 must move it by 120 units
var maskNode = tree.Find("Mask")!;
var stockSlot = maskNode.Slot!;
maskNode.Slot = new SlotInfo { Name = stockSlot.Name, ClassName = stockSlot.ClassName, ExportIndex = stockSlot.ExportIndex,
    Fields = stockSlot.Fields.Select(f => f.Path == "LayoutData.Offsets.Left" ? f with { Value = "249" } : f).ToList() };
var moved = engine.Arrange(tree, 1786, 1000).Find("Mask")!;
maskNode.Slot = stockSlot;
var dx = moved.Bounds.X - maskBox!.Bounds.X;
Check("Mask Left +120 units moves it 120 x scale px (canary 2)", Math.Abs(dx - 120 * ppu) < 0.01, $"{dx:0.###} px = {dx / ppu:0.###} units; canary 2 measured ~114-117 px");

// --layout <packagePath> <w> <h>: print one package's layout boxes
var layoutAt = Array.IndexOf(args, "--layout");
if (layoutAt >= 0)
{
    var lt = WidgetTreeReader.Build(source, args[layoutAt + 1]);
    var lr = engine.Arrange(lt, int.Parse(args[layoutAt + 2]), int.Parse(args[layoutAt + 3]));
    foreach (var b in lr.All())
        Console.WriteLine($"  {b.Node.Name,-28} {b.Node.ClassName,-26} vis {b.Visible,-5} op {b.Opacity:0.##} px {b.Bounds.X,8:0.0} {b.Bounds.Y,8:0.0} {b.Bounds.Width,8:0.0} {b.Bounds.Height,8:0.0} {b.Content} {b.TexturePath} {b.Approximation}");
}

if (args.Contains("--layout-dump"))
    foreach (var b in gameView.All().Where(b => b.Visible))
        Console.WriteLine($"  {new string(' ', 0)}{b.Node.Name,-22} {b.Node.ClassName,-24} px {b.Bounds.X,7:0.0} {b.Bounds.Y,7:0.0} {b.Bounds.Width,7:0.0} {b.Bounds.Height,7:0.0}  {b.PixelsPerUnit:0.####}/u {(b.Approximation != null ? "~ " + b.Approximation : "")}");

if (args.Contains("--pick-textures"))
{
    var big = stats.Where(s => s.Img.Width * s.Img.Height >= 64 * 64).ToList();
    void Show(string title, IEnumerable<(string Path, DecodedImage Img, double Alpha, double Sat)> list)
    {
        Console.WriteLine(title);
        foreach (var s in list.Take(6)) Console.WriteLine($"  {s.Img.Format,-12} {s.Img.Width}x{s.Img.Height,-5} alpha<250 {s.Alpha:P0}  sat {s.Sat:F2}  {s.Path}");
    }
    Show("transparency (ETC2_RGBA, most partially transparent):", big.Where(s => s.Img.Format == "PF_ETC2_RGBA").OrderByDescending(s => s.Alpha * (1 - Math.Abs(s.Alpha - 0.5))));
    Show("strong colours:", big.OrderByDescending(s => s.Sat));
    Show("non-square:", big.Where(s => s.Img.Width != s.Img.Height && Math.Max(s.Img.Width, s.Img.Height) >= 2 * Math.Min(s.Img.Width, s.Img.Height)).OrderByDescending(s => s.Sat + s.Alpha));
}

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
