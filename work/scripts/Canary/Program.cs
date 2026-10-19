// Canary patch: move one non-animated CanvasPanelSlot in a Zen package, in place.
// 1) make the edit on a legacy copy with UAssetAPI and diff it to find the exact float offsets,
// 2) map each float into the raw Zen chunk by its ±32-byte stock context (must match exactly once),
// 3) patch a copy of the Zen chunk and verify: same size, only the expected bytes differ.
// Usage: Canary <legacy.uasset> <zenChunk> <outChunk> <slotName> <contentName> Left=829 Top=407 ...
using System.Globalization;
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Objects;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

const int Ctx = 32;
var (legacyPath, zenPath, outPath, slotName, contentName) = (args[0], args[1], args[2], args[3], args[4]);
var edits = args.Skip(5).Select(a => a.Split('=')).ToDictionary(p => p[0], p => float.Parse(p[1], CultureInfo.InvariantCulture));

var asset = new UAsset(legacyPath, EngineVersion.VER_UE4_26);
var slots = asset.Exports.OfType<NormalExport>().Where(e => e.ObjectName.ToString() == slotName && e.GetExportClassType().ToString() == "CanvasPanelSlot")
    .Where(e => e.Data.OfType<ObjectPropertyData>().Any(p => p.Name.ToString() == "Content" && p.Value.IsExport() && p.Value.ToExport(asset).ObjectName.ToString() == contentName))
    .ToList();
if (slots.Count != 1) Fail($"expected exactly one {slotName} holding {contentName}, found {slots.Count}");
var slot = slots[0];
var offsets = slot.Data.OfType<StructPropertyData>().First(p => p.Name.ToString() == "LayoutData")
    .Value.OfType<StructPropertyData>().First(p => p.Name.ToString() == "Offsets");

var planned = new List<(string field, float oldV, float newV)>();
foreach (var (field, value) in edits)
{
    var f = offsets.Value.OfType<FloatPropertyData>().FirstOrDefault(p => p.Name.ToString() == field);
    if (f == null) Fail($"{field} is not stored on {slotName} (only stored values are editable)");
    planned.Add((field, f!.Value, value));
    f.Value = value;
}

var tmp = Path.Combine(Path.GetTempPath(), "canary_" + Guid.NewGuid().ToString("N") + ".uasset");
asset.Write(tmp);
var stockUexp = File.ReadAllBytes(Path.ChangeExtension(legacyPath, ".uexp"));
var editUexp = File.ReadAllBytes(Path.ChangeExtension(tmp, ".uexp"));
File.Delete(tmp); File.Delete(Path.ChangeExtension(tmp, ".uexp"));
if (stockUexp.Length != editUexp.Length) Fail("legacy edit changed the size");

var zen = File.ReadAllBytes(zenPath);
var patched = (byte[]) zen.Clone();
var expected = new HashSet<int>();
foreach (var (field, oldV, newV) in planned)
{
    var oldB = BitConverter.GetBytes(oldV); var newB = BitConverter.GetBytes(newV);
    // locate the float in the legacy uexp: the 4 bytes that changed from old to new
    var locs = Enumerable.Range(0, stockUexp.Length - 3)
        .Where(i => stockUexp.AsSpan(i, 4).SequenceEqual(oldB) && editUexp.AsSpan(i, 4).SequenceEqual(newB)).ToList();
    if (locs.Count != 1) Fail($"{field}: changed float found {locs.Count} times in legacy diff");
    var at = locs[0];
    var context = stockUexp.AsSpan(at - Ctx, 4 + 2 * Ctx).ToArray();
    var hits = new List<int>();
    for (int i = zen.AsSpan().IndexOf(context); i >= 0;)
    {
        hits.Add(i);
        var next = zen.AsSpan(i + 1).IndexOf(context);
        i = next < 0 ? -1 : i + 1 + next;
    }
    if (hits.Count != 1) Fail($"{field}: ±{Ctx}B context matched {hits.Count} times in the Zen chunk (must be exactly 1); refusing");
    var zAt = hits[0] + Ctx;
    if (!zen.AsSpan(zAt, 4).SequenceEqual(oldB)) Fail($"{field}: stock bytes at Zen 0x{zAt:X} are not {oldV}");
    newB.CopyTo(patched, zAt);
    for (int k = 0; k < 4; k++) if (oldB[k] != newB[k]) expected.Add(zAt + k);
    Console.WriteLine($"{slotName}.LayoutData.Offsets.{field}: {oldV} -> {newV}  legacy uexp 0x{at:X}, Zen 0x{zAt:X} (file size {zen.Length}, offset < size: {zAt + 4 <= zen.Length}), context unique");
}

// verify: size unchanged, the only differing bytes are the expected ones
var diff = Enumerable.Range(0, zen.Length).Where(i => zen[i] != patched[i]).ToHashSet();
if (patched.Length != zen.Length) Fail("size changed");
if (!diff.SetEquals(expected)) Fail($"unexpected byte diff: {diff.Count} changed vs {expected.Count} expected");
File.WriteAllBytes(outPath, patched);
Console.WriteLine($"OK: wrote {outPath}; size {patched.Length} unchanged; {diff.Count} bytes differ, all expected: [{string.Join(",", diff.Order().Select(i => "0x" + i.ToString("X")))}]");
return 0;

static void Fail(string msg)
{
    Console.Error.WriteLine("REFUSED: " + msg);
    Environment.Exit(2);
}
