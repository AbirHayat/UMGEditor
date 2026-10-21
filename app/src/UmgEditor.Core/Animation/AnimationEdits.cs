using System.Buffers.Binary;
using System.Globalization;
using CUE4Parse.UE4.Assets;
using Newtonsoft.Json.Linq;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Animation;

/// <summary>What an animation key edit changes: the key's value, its frame (time), or its interp mode (float keys).</summary>
public enum AnimKeyField { Value, Frame, Interp }

/// <summary>
/// Animation key edits ride in the ordinary EditSet: FieldKey(package, section export, "anim:&lt;channel&gt;#&lt;key&gt;"),
/// value = the key's new value (float, or the byte for visibility); "…@frame" = its frame in ticks, "…@interp" = its
/// interp mode (0 Linear, 1 Constant, 2 Cubic; float keys only). One such edit writes the section and, when it has
/// one, the compiled copy, so both copies always agree.
/// </summary>
public static class AnimKeyPath
{
    public const string Prefix = "anim:";

    public static bool Is(FieldKey key) => key.Path.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Make(string channel, int keyIndex, AnimKeyField field = AnimKeyField.Value) =>
        $"{Prefix}{channel}#{keyIndex.ToString(CultureInfo.InvariantCulture)}{field switch { AnimKeyField.Frame => "@frame", AnimKeyField.Interp => "@interp", _ => "" }}";

    public static (string Channel, int Key, AnimKeyField Field) Parse(string path)
    {
        var field = AnimKeyField.Value;
        if (path.EndsWith("@frame", StringComparison.Ordinal)) { field = AnimKeyField.Frame; path = path[..^"@frame".Length]; }
        else if (path.EndsWith("@interp", StringComparison.Ordinal)) { field = AnimKeyField.Interp; path = path[..^"@interp".Length]; }
        var hash = path.LastIndexOf('#');
        return (path[Prefix.Length..hash], int.Parse(path[(hash + 1)..], CultureInfo.InvariantCulture), field);
    }
}

/// <summary>Where one animation key is (stock offsets): the section copy and, when there is one, the compiled copy.</summary>
/// <param name="CompiledExport">The MovieSceneCompiledData export of the animation, -1 when the channel has no compiled copy</param>
/// <param name="Interp">Float keys: the stock interp mode (0 Linear, 1 Constant, 2 Cubic, 3 None); null for other kinds</param>
public sealed record AnimKeyRef(ChannelKind Kind, int SectionExport, string Channel, int KeyIndex, int ValueAt, int CompiledExport, int CompiledValueAt,
    string StockValue, string Animation, string Description)
{
    public int FrameAt { get; init; } = -1;
    public int CompiledFrameAt { get; init; } = -1;
    public int Frame { get; init; }
    public int? Interp { get; init; }

    /// <summary>Bytes written: the float value (not its tangents), the byte, or the int32.</summary>
    public int ValueSize => Kind is ChannelKind.Float or ChannelKind.Integer ? 4 : 1;
    public bool HasCompiledCopy => CompiledValueAt >= 0;

    /// <summary>The stock value of the edited field: the key's value, frame or interp mode (null: the kind has none).</summary>
    public double? Stock(AnimKeyField field) => field switch
    {
        AnimKeyField.Frame => Frame,
        AnimKeyField.Interp => Interp,
        _ => AnimKeyIndex.Parse(StockValue),
    };
}

/// <summary>Every animation key of one package by its edit key (from AnimationReader on the stock package).</summary>
public sealed class AnimKeyIndex
{
    private readonly Dictionary<(int Section, string Channel, int Key), AnimKeyRef> _keys = [];

    public IReadOnlyList<AnimationInfo> Animations { get; }

    public AnimKeyIndex(IReadOnlyList<AnimationInfo> animations)
    {
        Animations = animations;
        foreach (var a in animations)
            foreach (var t in a.Tracks)
                foreach (var c in t.Channels)
                {
                    if (c.Kind is not { } kind) continue;
                    for (int k = 0; k < c.Keys.Count; k++)
                    {
                        var key = c.Keys[k];
                        if (key.ValueAt < 0) continue;
                        _keys[(t.SectionExport, c.Path, k)] = new AnimKeyRef(kind, t.SectionExport, c.Path, k, key.ValueAt,
                            key.CompiledValueAt >= 0 ? c.CompiledExport : -1, key.CompiledValueAt, key.Value, a.Name,
                            $"{a.Name} · {AnimationText.Target(t)} · {AnimationText.ChannelName(t, c.Path)} · key {k} ({AnimationText.S(key.Seconds)})")
                        {
                            FrameAt = key.FrameAt, CompiledFrameAt = key.CompiledValueAt >= 0 ? key.CompiledFrameAt : -1, Frame = key.Frame,
                            Interp = kind == ChannelKind.Float && key.Interp != null ? Array.IndexOf(InterpModes, key.Interp) is var m and >= 0 ? m : null : null,
                        };
                    }
                }
    }

    public static AnimKeyIndex Read(GameSource source, string package) => new(AnimationReader.Read(source, package));

    public AnimKeyRef? Find(FieldKey key)
    {
        if (!AnimKeyPath.Is(key)) return null;
        var (channel, k, _) = AnimKeyPath.Parse(key.Path);
        return _keys.GetValueOrDefault((key.SlotExport, channel, k));
    }

    public static FieldKey KeyOf(string package, AnimTrack track, AnimChannel channel, int keyIndex, AnimKeyField field = AnimKeyField.Value) =>
        new(package, track.SectionExport, AnimKeyPath.Make(channel.Path, keyIndex, field));

    /// <summary>ERichCurveInterpMode names as AnimationReader writes them.</summary>
    public static readonly string[] InterpModes = ["Linear", "Constant", "Cubic", "None"];

    public static double Parse(string value) => value switch
    {
        "true" => 1,
        "false" => 0,
        _ => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
    };
}

/// <summary>
/// Range bound edits (an animation's playback, section and compiled ranges): FieldKey(package, export, "range@&lt;frame&gt;"),
/// value = the new frame. One edit moves every closed bound of that export at that stock frame, as C9's
/// AnimationPatch.MoveRangeBounds did.
/// </summary>
public static class AnimRangePath
{
    public const string Prefix = "range@";

    public static bool Is(FieldKey key) => key.Path.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Make(int frame) => Prefix + frame.ToString(CultureInfo.InvariantCulture);

    public static int Parse(string path) => int.Parse(path[Prefix.Length..], CultureInfo.InvariantCulture);
}

/// <param name="Offset">Stock offset of the bound's frame (int32)</param>
/// <param name="Path">Its path in the export's flattened parse (what the re-parse must show changed, and nothing else)</param>
public sealed record RangeBound(int Offset, string Path);

/// <summary>Every closed range bound of one export at one stock frame.</summary>
public sealed record AnimRangeRef(int Export, int Frame, IReadOnlyList<RangeBound> Bounds, string Description);

public static class AnimRanges
{
    /// <summary>Exports whose outer chain contains the animation ("Show_INST"), with their types.</summary>
    public static IEnumerable<(int Index, string Type)> ExportsOf(IoPackage package, string animation)
    {
        for (int i = 0; i < package.ExportsLazy.Length; i++)
        {
            var e = package.ExportsLazy[i].Value;
            var inside = e.Name == animation + "_INST";
            for (var o = e.Outer; o != null && !inside; o = o.Outer) inside = o.Name.Text == animation + "_INST";
            if (inside) yield return (i, e.ExportType);
        }
    }

    /// <summary>
    /// Every closed bound at <paramref name="frame"/> in the export. Each distinct range holding one (10 bytes: type, frame,
    /// type, frame) must occur in the export's bytes exactly as often as in the parse; the k-th occurrence is taken as the
    /// k-th such range in property order (the re-parse confirms it). Null with the reason otherwise.
    /// </summary>
    public static AnimRangeRef? Locate(ReadOnlySpan<byte> stock, ZenPackageLayout layout, int export, string exportName, JObject exportJson, int frame, out string? why)
    {
        why = null;
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
        if (exportJson["Properties"] is { } props) Walk(props, "Properties");
        bool Lower((string, byte LT, int LV, byte, int) r) => r.LT != 2 && r.LV == frame;
        bool Upper((string, byte, int, byte UT, int UV) r) => r.UT != 2 && r.UV == frame;
        var targets = ranges.Where(r => Lower(r) || Upper(r)).ToList();
        if (targets.Count == 0) { why = $"{exportName} (export {export}) has no range bound at frame {frame}"; return null; }

        var span = layout.SpanOf(export);
        var bytes = stock.Slice(span.Start, span.Size);
        var bounds = new List<RangeBound>();
        foreach (var group in targets.GroupBy(r => (r.LT, r.LV, r.UT, r.UV)))
        {
            var (lt, lv, ut, uv) = group.Key;
            var pattern = new byte[10];
            pattern[0] = lt; BinaryPrimitives.WriteInt32LittleEndian(pattern.AsSpan(1), lv);
            pattern[5] = ut; BinaryPrimitives.WriteInt32LittleEndian(pattern.AsSpan(6), uv);
            var hits = new List<int>();
            for (int at = bytes.IndexOf(pattern); at >= 0;)
            {
                hits.Add(at);
                var next = bytes[(at + 1)..].IndexOf(pattern);
                at = next < 0 ? -1 : at + 1 + next;
            }
            var inParse = group.ToList();
            if (hits.Count != inParse.Count)
            {
                why = $"{exportName} (export {export}): range {lv}..{uv} occurs {hits.Count} times in the bytes, the parse has {inParse.Count}";
                return null;
            }
            foreach (var (r, h) in inParse.Zip(hits))
            {
                if (Lower(r)) bounds.Add(new RangeBound(span.Start + h + 1, r.Path + ".LowerBound.Value.Value"));
                if (Upper(r)) bounds.Add(new RangeBound(span.Start + h + 6, r.Path + ".UpperBound.Value.Value"));
            }
        }
        return new AnimRangeRef(export, frame, bounds.OrderBy(b => b.Offset).ToList(), $"{exportName} · {bounds.Count} range bound{(bounds.Count == 1 ? "" : "s")} at frame {frame}");
    }
}

/// <summary>Which tracks an animation edit may touch (C11 and its variants).</summary>
[Flags]
public enum TrackParts
{
    None = 0,
    /// <summary>Slot margin tracks (layout box: the board's width).</summary>
    Width = 1,
    /// <summary>Visibility byte tracks.</summary>
    Visibility = 2,
    /// <summary>2D transform (translation, scale, angle, shear) and opacity tracks.</summary>
    Positions = 4,
    All = Width | Visibility | Positions,
}

/// <summary>
/// "Make these animations keep the normal state": every key of every channel of the given animations is set to the
/// channel's normal value = its last key in <c>normalFrom</c> (e.g. Info_Hide, the state after returning), cross-checked
/// with its last key in <c>crossCheck</c> (e.g. Show). A channel with neither, or where the two disagree, is refused.
/// Only key values change; a float channel that ends up flat must already have |tangent| ≤ 1e-6 everywhere (else
/// cubic segments between equal values could bulge), otherwise it's refused.
/// </summary>
public static class AnimationFlatten
{
    public const float MaxTangent = 1e-6f;

    public sealed record Result(IReadOnlyList<FieldChange> Changes, IReadOnlyList<string> Refusals, IReadOnlyList<string> Log);

    public static TrackParts PartOf(AnimTrack track) => track.Track switch
    {
        "MarginTrack" => TrackParts.Width,
        "ByteTrack" when track.Property == "Visibility" => TrackParts.Visibility,
        "2DTransformTrack" or "FloatTrack" => TrackParts.Positions,
        _ => TrackParts.None,
    };

    public static Result Plan(string package, IReadOnlyList<AnimationInfo> animations, IReadOnlyList<string> names, string normalFrom, string crossCheck,
        TrackParts parts, ReadOnlySpan<byte> stock)
    {
        var byName = animations.ToDictionary(a => a.Name);
        var changes = new List<FieldChange>();
        var refusals = new List<string>();
        var log = new List<string>();
        foreach (var n in names.Append(normalFrom).Append(crossCheck))
            if (!byName.ContainsKey(n)) refusals.Add($"{GamePaths.ShortName(package)} has no animation {n}");
        if (refusals.Count > 0) return new Result(changes, refusals, log);

        static string Id(AnimTrack t, AnimChannel c) => $"{t.Widget}|{t.OnSlot}|{t.Track}|{t.Property}|{c.Path}";
        Dictionary<string, string> Last(string anim) => byName[anim].Tracks.SelectMany(t => t.Channels.Where(c => c.Keys.Count > 0).Select(c => (Id(t, c), c.Keys[^1].Value)))
            .GroupBy(x => x.Item1).ToDictionary(g => g.Key, g => g.Last().Value);
        var normal = Last(normalFrom);
        var check = Last(crossCheck);

        foreach (var name in names)
            foreach (var t in byName[name].Tracks)
            {
                var part = PartOf(t);
                if (part == TrackParts.None || !parts.HasFlag(part)) continue;
                foreach (var c in t.Channels.Where(c => c.Keys.Count > 0))
                {
                    var where = $"{name} · {AnimationText.Target(t)} · {AnimationText.ChannelName(t, c.Path)}";
                    var hasN = normal.TryGetValue(Id(t, c), out var nText);
                    var hasC = check.TryGetValue(Id(t, c), out var cText);
                    if (hasN && hasC && AnimKeyIndex.Parse(nText!) != AnimKeyIndex.Parse(cText!)) { refusals.Add($"{where}: {normalFrom} ends at {nText}, {crossCheck} at {cText}"); continue; }
                    if (!hasN && !hasC) { refusals.Add($"{where}: no normal value (in neither {normalFrom} nor {crossCheck})"); continue; }
                    var target = AnimKeyIndex.Parse(hasN ? nText! : cText!);
                    if (c.Keys.All(k => AnimKeyIndex.Parse(k.Value) == target)) continue;
                    if (c.Kind is not { } kind) { refusals.Add($"{where}: key bytes not located"); continue; }
                    if (kind == ChannelKind.Float)
                    {
                        AnimKey? bulge = null;
                        foreach (var key in c.Keys)
                            if (Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(stock[(key.ValueAt + 4)..])) > MaxTangent
                                || Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(stock[(key.ValueAt + 8)..])) > MaxTangent) { bulge = key; break; }
                        if (bulge != null) { refusals.Add($"{where}: key at {AnimationText.S(bulge.Seconds)} has a tangent above {MaxTangent} (a flat channel would bulge)"); continue; }
                    }
                    for (int k = 0; k < c.Keys.Count; k++)
                    {
                        var old = AnimKeyIndex.Parse(c.Keys[k].Value);
                        if (old == target) continue;
                        changes.Add(new FieldChange(AnimKeyIndex.KeyOf(package, t, c, k), old, target));
                        log.Add($"{where} key {k} ({AnimationText.S(c.Keys[k].Seconds)}): {AnimationText.ValueText(t, c.Keys[k].Value)} → {AnimationText.ValueText(t, target.ToString(CultureInfo.InvariantCulture))}");
                    }
                }
            }
        return new Result(changes, refusals, log);
    }
}
