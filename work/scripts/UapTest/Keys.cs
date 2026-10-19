// Can a margin keyframe be patched in place? Edits the section key with UAssetAPI, then locates the compiled-template copy by bytes.
using UAssetAPI;
using UAssetAPI.ExportTypes;
using UAssetAPI.PropertyTypes.Structs;
using UAssetAPI.UnrealTypes;

static class Keys
{
    public static void Run(string src, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var asset = new UAsset(src, EngineVersion.VER_UE4_26);
        var headerSize = new FileInfo(src).Length;
        var uexp = File.ReadAllBytes(Path.ChangeExtension(src, ".uexp"));

        var secIdx = asset.Exports.FindIndex(e => e.GetExportClassType().ToString() == "MovieSceneMarginSection"
            && e.OuterIndex.IsExport() && e.OuterIndex.ToExport(asset).OuterIndex.ToExport(asset).OuterIndex.ToExport(asset).ObjectName.ToString() == "Aspect_0403_INST");
        var sec = (NormalExport) asset.Exports[secIdx];
        foreach (var p in sec.Data) Console.WriteLine($"  prop {p.Name} : {p.GetType().Name} {(p is StructPropertyData sp ? sp.StructType + " [" + string.Join(",", sp.Value.Select(x => x.GetType().Name)) + "]" : "")}");
        var left = (MovieSceneFloatChannelPropertyData) sec.Data.OfType<StructPropertyData>().First(p => p.Name.ToString() == "LeftCurve").Value[0];
        Console.WriteLine($"section export #{secIdx} LeftCurve keys: [{string.Join(",", left.Value.Values.Select(v => v.Value))}] times [{string.Join(",", left.Value.Times.Select(t => t.Value))}]");

        // byte pattern of this channel's key arrays in the stock uexp: count of compiled-template copies
        var compIdx = asset.Exports.FindIndex(e => e.GetExportClassType().ToString() == "MovieSceneCompiledData"
            && e.OuterIndex.IsExport() && e.OuterIndex.ToExport(asset).ObjectName.ToString() == "Aspect_0403_INST");
        var comp = asset.Exports[compIdx];
        long cStart = comp.SerialOffset - headerSize, cEnd = cStart + comp.SerialSize;
        long sStart = sec.SerialOffset - headerSize, sEnd = sStart + sec.SerialSize;
        Console.WriteLine($"compiled export #{compIdx} type {comp.GetType().Name} uexp range 0x{cStart:X}-0x{cEnd:X}; section range 0x{sStart:X}-0x{sEnd:X}");

        var old = left.Value.Values[0].Value;
        left.Value.Values[0].Value = old + 100f;
        var edPath = Path.Combine(outDir, Path.GetFileNameWithoutExtension(src) + ".keyedit.uasset");
        asset.Write(edPath);
        var ed = File.ReadAllBytes(Path.ChangeExtension(edPath, ".uexp"));
        var diffs = Enumerable.Range(0, Math.Min(uexp.Length, ed.Length)).Where(i => uexp[i] != ed[i]).ToList();
        Console.WriteLine($"section key {old} -> {old + 100f}: size {uexp.Length} -> {ed.Length}, diff bytes {diffs.Count} at [{string.Join(",", diffs.Select(i => "0x" + i.ToString("X")))}] (inside section: {diffs.All(i => i >= sStart && i < sEnd)})");

        // the compiled copy: same float, find the section's key record (value+tangents = 24 bytes) inside the compiled export
        int floatOff = FindFloatStart(uexp, ed, diffs);
        var record = uexp.AsSpan(floatOff, 24).ToArray();
        var hits = new List<long>();
        for (long i = cStart; i + 24 <= cEnd; i++) if (uexp.AsSpan((int) i, 24).SequenceEqual(record)) hits.Add(i);
        Console.WriteLine($"key record (value+tangents) at 0x{floatOff:X}; copies inside compiled export: {hits.Count} at [{string.Join(",", hits.Select(h => "0x" + h.ToString("X")))}]");
        var allHits = 0; for (int i = 0; i + 24 <= uexp.Length; i++) if (uexp.AsSpan(i, 24).SequenceEqual(record)) allHits++;
        Console.WriteLine($"same 24-byte record anywhere in file: {allHits} (needs wider context to disambiguate if >2)");

        // reload check
        var re = new UAsset(edPath, EngineVersion.VER_UE4_26);
        var v = ((MovieSceneFloatChannelPropertyData) ((NormalExport) re.Exports[secIdx]).Data.OfType<StructPropertyData>().First(p => p.Name.ToString() == "LeftCurve").Value[0]).Value.Values[0].Value;
        Console.WriteLine($"reloaded section key: {v}");
    }

    static int FindFloatStart(byte[] a, byte[] b, List<int> diffs)
    {
        // the edited float is 4-byte aligned to the changed bytes' start; check candidates for the old/new value pair
        for (int s = diffs[0] - 3; s <= diffs[0]; s++)
            if (s + 4 > diffs[^1] && BitConverter.ToSingle(a, s) != BitConverter.ToSingle(b, s)) return s;
        return diffs[0];
    }
}
