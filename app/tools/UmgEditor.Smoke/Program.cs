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

Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;
