using System.Buffers.Binary;
using CUE4Parse.UE4.Assets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Animation;

/// <summary>New state of one key; null keeps the stored value.</summary>
public sealed record KeyChange(int Index, int? Frame = null, float? Value = null, byte? InterpMode = null);

/// <summary>
/// Same-size edits of widget animation data in one package's raw Zen chunk: key times/values/interp modes of a float channel,
/// and frame numbers of ranges (playback, section, compiled). Every location is found from CUE4Parse's parse of the stock
/// bytes and must match as often as the parse says; the result is re-parsed and every export's JSON is diffed against stock:
/// only the intended values may change.
/// </summary>
public sealed class AnimationPatch
{
    private const int FloatValueSize = 28; // FMovieSceneFloatValue 4.26: Value, Arrive/Leave tangent, Arrive/Leave weight, WeightMode+3 pad, Interp, TangentMode, 2 pad

    private readonly GameSource _source;
    private readonly IoPackage _stock;
    private readonly ZenPackageLayout _layout;
    private readonly byte[] _stockBytes;
    private readonly Dictionary<int, JObject> _json = [];
    private readonly Dictionary<(int Export, string Path), string> _expected = [];

    public string PackagePath { get; }
    public string FileKey { get; }
    public byte[] Bytes { get; }
    public List<string> Log { get; } = [];
    public List<string> Refusals { get; } = [];

    public AnimationPatch(GameSource source, string packagePath)
    {
        _source = source;
        PackagePath = packagePath;
        (FileKey, _stockBytes) = source.ReadPackageBytes(packagePath);
        Bytes = (byte[]) _stockBytes.Clone();
        _stock = source.ParsePackage(FileKey, _stockBytes);
        _layout = ZenPackageLayout.Read(_stockBytes, _stock);
    }

    private JObject Json(int i)
    {
        if (!_json.TryGetValue(i, out var o)) _json[i] = o = JObject.Parse(JsonConvert.SerializeObject(_stock.ExportsLazy[i].Value));
        return o;
    }

    public string ExportName(int i) => _stock.ExportsLazy[i].Value.Name;

    /// <summary>Export indices whose outer chain contains the animation (e.g. "Show_INST"), with their names and types.</summary>
    public IEnumerable<(int Index, string Name, string Type, string Outer)> ExportsOf(string animationName)
    {
        for (int i = 0; i < _stock.ExportsLazy.Length; i++)
        {
            var e = _stock.ExportsLazy[i].Value;
            var outer = new List<string>();
            for (var o = e.Outer; o != null; o = o.Outer) outer.Add(o.Name.Text);
            if (e.Name == animationName + "_INST" || outer.Contains(animationName + "_INST"))
                yield return (i, e.Name, e.ExportType, string.Join(" < ", outer));
        }
    }

    /// <summary>Rewrites keys of one float channel in a section export (same key count; times, values, interp modes).</summary>
    public bool SetKeys(int sectionExport, string channel, IReadOnlyList<KeyChange> changes)
    {
        var what = $"{ExportName(sectionExport)}#{sectionExport} {channel}";
        if (Json(sectionExport)["Properties"]?[channel] is not JObject ch || ch["Times"] is not JArray times || ch["Values"] is not JArray values)
            return Refuse($"{what}: no such channel");
        var n = times.Count;
        if (values.Count != n) return Refuse($"{what}: {n} times but {values.Count} values");

        // [int32 4][int32 n][frames…][int32 28][int32 n][values…]
        var pattern = new List<byte>();
        void I32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); pattern.AddRange(b); }
        I32(4); I32(n);
        foreach (var t in times) I32(t["Value"]!.Value<int>());
        I32(FloatValueSize); I32(n);
        var span = _layout.SpanOf(sectionExport);
        var hits = FindAll(_stockBytes.AsSpan(span.Start, span.Size), pattern.ToArray())
            .Where(h => ValuesMatch(span.Start + h + pattern.Count, values)).ToList();
        if (hits.Count != 1) return Refuse($"{what}: key block found {hits.Count} times in the export (needs exactly 1)");
        int timesAt = span.Start + hits[0] + 8, valuesAt = timesAt + 4 * n + 8;

        foreach (var c in changes)
        {
            if (c.Index < 0 || c.Index >= n) return Refuse($"{what}: key {c.Index} doesn't exist ({n} keys)");
            var p = $"Properties.{channel}";
            if (c.Frame is { } f) { BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(timesAt + 4 * c.Index), f); Expect(sectionExport, $"{p}.Times[{c.Index}].Value", f); }
            var v = valuesAt + FloatValueSize * c.Index;
            if (c.Value is { } x) { BinaryPrimitives.WriteSingleLittleEndian(Bytes.AsSpan(v), x); Expect(sectionExport, $"{p}.Values[{c.Index}].Value", x); }
            if (c.InterpMode is { } m) { Bytes[v + 24] = m; Expect(sectionExport, $"{p}.Values[{c.Index}].InterpMode", m); }
            Log.Add($"{what} key {c.Index}: frame {times[c.Index]["Value"]} → {c.Frame?.ToString() ?? "same"}, value {values[c.Index]["Value"]} → {c.Value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "same"}, interp {values[c.Index]["InterpMode"]} → {c.InterpMode?.ToString() ?? "same"}  @0x{timesAt:x}/0x{valuesAt:x}");
        }
        return true;
    }

    private bool ValuesMatch(int at, JArray values)
    {
        for (int k = 0; k < values.Count; k++)
        {
            int v = at + FloatValueSize * k;
            if (v + FloatValueSize > _stockBytes.Length) return false;
            if (BinaryPrimitives.ReadSingleLittleEndian(_stockBytes.AsSpan(v)) != values[k]["Value"]!.Value<float>()) return false;
            if (_stockBytes[v + 24] != values[k]["InterpMode"]!.Value<byte>()) return false;
        }
        return true;
    }

    /// <summary>
    /// Moves every range bound equal to <paramref name="oldFrame"/> in an export to <paramref name="newFrame"/>.
    /// Each distinct range (10 bytes: type, frame, type, frame) must occur in the bytes exactly as often as in the parse.
    /// </summary>
    public bool MoveRangeBounds(int export, int oldFrame, int newFrame)
    {
        var what = $"{ExportName(export)}#{export}";
        var ranges = new List<(string Path, byte LT, int LV, byte UT, int UV)>();
        void Walk(JToken t, string p)
        {
            if (t is JObject o && o["LowerBound"] is JObject lb && o["UpperBound"] is JObject ub && lb["Type"] != null && ub["Type"] != null)
            {
                ranges.Add((p, lb["Type"]!.Value<byte>(), lb["Value"]?["Value"]?.Value<int>() ?? 0, ub["Type"]!.Value<byte>(), ub["Value"]?["Value"]?.Value<int>() ?? 0));
                return;
            }
            if (t is JObject j) foreach (var prop in j.Properties()) Walk(prop.Value, p.Length == 0 ? prop.Name : p + "." + prop.Name);
            else if (t is JArray a) for (int i = 0; i < a.Count; i++) Walk(a[i], $"{p}[{i}]");
        }
        Walk(Json(export)["Properties"]!, "Properties");
        var targets = ranges.Where(r => (r.LT != 2 && r.LV == oldFrame) || (r.UT != 2 && r.UV == oldFrame)).ToList();
        if (targets.Count == 0) return Refuse($"{what}: no range bound at frame {oldFrame}");

        var span = _layout.SpanOf(export);
        foreach (var group in targets.GroupBy(r => (r.LT, r.LV, r.UT, r.UV)))
        {
            var (lt, lv, ut, uv) = group.Key;
            var old = Range(lt, lv, ut, uv);
            var hits = FindAll(_stockBytes.AsSpan(span.Start, span.Size), old).ToList();
            var inParse = ranges.Count(r => (r.LT, r.LV, r.UT, r.UV) == group.Key);
            if (hits.Count != inParse) return Refuse($"{what}: range {Show(lt, lv)}..{Show(ut, uv)} found {hits.Count} times in the bytes, the parse has {inParse}");
            var @new = Range(lt, lt != 2 && lv == oldFrame ? newFrame : lv, ut, ut != 2 && uv == oldFrame ? newFrame : uv);
            foreach (var h in hits) @new.CopyTo(Bytes, span.Start + h);
            foreach (var r in group)
            {
                if (r.LT != 2 && r.LV == oldFrame) Expect(export, r.Path + ".LowerBound.Value.Value", newFrame);
                if (r.UT != 2 && r.UV == oldFrame) Expect(export, r.Path + ".UpperBound.Value.Value", newFrame);
            }
            Log.Add($"{what}: {group.Count()}× range {Show(lt, lv)}..{Show(ut, uv)} → frame {oldFrame} becomes {newFrame} ({string.Join(", ", group.Select(r => r.Path))}) @{string.Join(",", hits.Select(h => $"0x{span.Start + h:x}"))}");
        }
        return true;
    }

    private static string Show(byte type, int frame) => type == 2 ? "open" : $"{frame}{(type == 0 ? ")" : "]")}";

    private static byte[] Range(byte lt, int lv, byte ut, int uv)
    {
        var b = new byte[10];
        b[0] = lt; BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), lv);
        b[5] = ut; BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(6), uv);
        return b;
    }

    private void Expect(int export, string path, object value) =>
        _expected[(export, path)] = JToken.FromObject(value).ToString(Formatting.None);

    private bool Refuse(string why) { Refusals.Add(why); return false; }

    private static IEnumerable<int> FindAll(ReadOnlySpan<byte> hay, byte[] needle)
    {
        var hits = new List<int>();
        for (int i = hay.IndexOf(needle); i >= 0;)
        {
            hits.Add(i);
            var next = hay[(i + 1)..].IndexOf(needle);
            i = next < 0 ? -1 : i + 1 + next;
        }
        return hits;
    }

    /// <summary>Re-parses the patched bytes; every export must parse and differ from stock in exactly the intended values.</summary>
    public IReadOnlyList<string> Verify()
    {
        var problems = new List<string>(Refusals);
        if (Bytes.Length != _stockBytes.Length) problems.Add($"size changed: {_stockBytes.Length} → {Bytes.Length}");
        var changedBytes = Enumerable.Range(0, Bytes.Length).Count(i => Bytes[i] != _stockBytes[i]);
        Log.Add($"{changedBytes} bytes differ from stock, size {Bytes.Length} unchanged");

        IoPackage patched;
        try { patched = _source.ParsePackage(FileKey, Bytes); }
        catch (Exception e) { problems.Add("patched package doesn't parse: " + e.Message); return problems; }

        var seen = new HashSet<(int, string)>();
        for (int i = 0; i < _stock.ExportsLazy.Length; i++)
        {
            Dictionary<string, string> a, b;
            try { a = Flatten(Json(i)); b = Flatten(JObject.Parse(JsonConvert.SerializeObject(patched.ExportsLazy[i].Value))); }
            catch (Exception e) { problems.Add($"export {i} {ExportName(i)} doesn't parse after patching: {e.Message}"); continue; }
            foreach (var k in a.Keys.Union(b.Keys))
            {
                var x = a.GetValueOrDefault(k); var y = b.GetValueOrDefault(k);
                if (x == y) continue;
                if (_expected.TryGetValue((i, k), out var want) && Same(y, want)) { seen.Add((i, k)); continue; }
                problems.Add($"unintended change in {ExportName(i)}#{i} {k}: {x} → {y}{(_expected.TryGetValue((i, k), out var w) ? $" (wanted {w})" : "")}");
            }
        }
        foreach (var e in _expected.Keys.Where(k => !seen.Contains(k)))
            if (!(Flatten(Json(e.Export)).TryGetValue(e.Path, out var stockValue) && Same(stockValue, _expected[e])))
                problems.Add($"intended change not found after re-parse: {ExportName(e.Export)}#{e.Export} {e.Path} = {_expected[e]}");
        Log.Add($"re-parse: {seen.Count} intended values changed, {problems.Count} problems");
        return problems;
    }

    private static bool Same(string? a, string b) =>
        a == b || (double.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                   && double.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) && x == y);

    private static Dictionary<string, string> Flatten(JObject export)
    {
        var map = new Dictionary<string, string>();
        void Walk(JToken t, string p)
        {
            switch (t)
            {
                case JObject j: foreach (var prop in j.Properties()) Walk(prop.Value, p.Length == 0 ? prop.Name : p + "." + prop.Name); break;
                case JArray arr: for (int i = 0; i < arr.Count; i++) Walk(arr[i], $"{p}[{i}]"); break;
                default: map[p] = t.ToString(Formatting.None); break;
            }
        }
        Walk(export, "");
        return map;
    }
}
