// Can UAssetAPI read and write back a legacy-converted eFootball Mobile widget without mappings?
// Reads from work/extract/legacy, writes only to work/extract/uaptest.
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

if (args[0] == "keys") { Keys.Run(args[1], args[2]); return; }
var src = args[0];
var outDir = args[1];
Directory.CreateDirectory(outDir);
var name = Path.GetFileNameWithoutExtension(src);

var asset = new UAsset(src, EngineVersion.VER_UE4_26);
var raw = asset.Exports.Count(e => e is RawExport);
Console.WriteLine($"{name}: exports {asset.Exports.Count}, RawExport (unparsed) {raw}, unversioned {asset.HasUnversionedProperties}, names {asset.GetNameMapIndexList().Count}");
foreach (var g in asset.Exports.GroupBy(e => e.GetType().Name)) Console.WriteLine($"  {g.Key}: {g.Count()}");

// 1) unchanged round-trip must be byte-identical
var rtPath = Path.Combine(outDir, name + ".roundtrip.uasset");
asset.Write(rtPath);
Console.WriteLine($"round-trip uasset identical: {Same(src, rtPath)}, uexp identical: {Same(Path.ChangeExtension(src, ".uexp"), Path.ChangeExtension(rtPath, ".uexp"))}");

// 2) change one present LayoutData.Offsets float on a CanvasPanelSlot and diff
FloatPropertyData? target = null; string where = ""; int whereIdx = -1;
foreach (var exp in asset.Exports.OfType<NormalExport>())
{
    if (exp.GetExportClassType().ToString() != "CanvasPanelSlot") continue;
    var layout = exp.Data.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "LayoutData");
    var offsets = layout?.Value.OfType<StructPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Offsets");
    var f = offsets?.Value.OfType<FloatPropertyData>().FirstOrDefault(p => p.Name.ToString() == "Left" && p.Value != 0);
    if (f != null) { target = f; where = exp.ObjectName.ToString(); whereIdx = asset.Exports.IndexOf(exp); break; }
}
if (target == null) { Console.WriteLine("no CanvasPanelSlot with a non-zero Offsets.Left found"); return; }
var old = target.Value;
target.Value = old + 100f;
var edPath = Path.Combine(outDir, name + ".edit.uasset");
asset.Write(edPath);
Console.WriteLine($"edited {where}.LayoutData.Offsets.Left {old} -> {target.Value}");
Diff(Path.ChangeExtension(src, ".uexp"), Path.ChangeExtension(edPath, ".uexp"));
Console.WriteLine($"uasset identical after edit: {Same(src, edPath)}");

// reload the edited copy and confirm the value
var re = new UAsset(edPath, EngineVersion.VER_UE4_26);
var reVal = ((NormalExport) re.Exports[whereIdx])
    .Data.OfType<StructPropertyData>().First(p => p.Name.ToString() == "LayoutData")
    .Value.OfType<StructPropertyData>().First(p => p.Name.ToString() == "Offsets")
    .Value.OfType<FloatPropertyData>().First(p => p.Name.ToString() == "Left").Value;
Console.WriteLine($"reloaded value: {reVal}");

static bool Same(string a, string b) => File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));

static void Diff(string a, string b)
{
    var x = File.ReadAllBytes(a); var y = File.ReadAllBytes(b);
    Console.WriteLine($"uexp sizes {x.Length} -> {y.Length}");
    var diffs = Enumerable.Range(0, Math.Min(x.Length, y.Length)).Where(i => x[i] != y[i]).ToList();
    Console.WriteLine($"differing bytes: {diffs.Count} at [{string.Join(",", diffs.Take(8).Select(i => "0x" + i.ToString("X")))}]");
}
