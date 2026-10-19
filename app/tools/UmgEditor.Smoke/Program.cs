// Smoke checks for UmgEditor.Core against the stock containers (read-only).
// Exit code 0 = all checks passed.
using System.Diagnostics;
using UmgEditor.Core;

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

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
