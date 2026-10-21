using CUE4Parse.UE4.Assets;

namespace UmgEditor.Core.Patching;

/// <summary>
/// The last check of a package built in layers (branch experiment, canary C16), each layer already verified by its own
/// tool against the layer before: the layers must be disjoint (no layer changes a value an earlier layer changed, so
/// every layer's verified change survives to the end), and parsed against stock the result differs by exactly their
/// union. Each layer must parse with stock's export count.
/// </summary>
public static class LayerUnion
{
    public static List<Dictionary<string, string>> FlattenAll(IoPackage package) => package.ExportsLazy.Select(e => TagInserter.Flatten(e.Value)).ToList();

    public static Dictionary<(int Export, string Key), string?> Diff(IReadOnlyList<Dictionary<string, string>> a, IReadOnlyList<Dictionary<string, string>> b)
    {
        var diff = new Dictionary<(int, string), string?>();
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
            foreach (var k in a[i].Keys.Union(b[i].Keys))
                if (a[i].GetValueOrDefault(k) != b[i].GetValueOrDefault(k)) diff[(i, k)] = b[i].GetValueOrDefault(k);
        return diff;
    }

    /// <returns>The problems (empty = passed) and how many values differ from stock</returns>
    public static (List<string> Problems, int Changes) Check(GameSource source, string fileKey, byte[] stock, IReadOnlyList<byte[]> layers)
    {
        var problems = new List<string>();
        var stockFlat = FlattenAll(source.ParsePackage(fileKey, stock));
        var prev = stockFlat;
        var union = new Dictionary<(int Export, string Key), string?>();
        foreach (var (layer, n) in layers.Select((l, n) => (l, n + 1)))
        {
            List<Dictionary<string, string>> flat;
            try { flat = FlattenAll(source.ParsePackage(fileKey, layer)); }
            catch (Exception e) { problems.Add($"layer {n} doesn't parse: {e.Message}"); return (problems, 0); }
            if (flat.Count != stockFlat.Count) { problems.Add($"layer {n} has {flat.Count} exports, stock {stockFlat.Count}"); return (problems, 0); }
            foreach (var kv in Diff(prev, flat))
            {
                if (union.ContainsKey(kv.Key)) problems.Add($"layer {n} changes export {kv.Key.Export} {kv.Key.Key} again ({union[kv.Key] ?? "(absent)"} → {kv.Value ?? "(absent)"}): an earlier layer changed it");
                union[kv.Key] = kv.Value;
            }
            prev = flat;
        }
        var final = Diff(stockFlat, prev);
        foreach (var (k, v) in final)
            if (!union.TryGetValue(k, out var u) || u != v) problems.Add($"export {k.Export} {k.Key}: {stockFlat[k.Export].GetValueOrDefault(k.Key) ?? "(absent)"} → {v ?? "(absent)"} isn't a layer's change");
        foreach (var k in union.Keys.Where(k => !final.ContainsKey(k)))
            problems.Add($"export {k.Export} {k.Key}: a layer's change is missing from the result");
        return (problems, final.Count);
    }
}
