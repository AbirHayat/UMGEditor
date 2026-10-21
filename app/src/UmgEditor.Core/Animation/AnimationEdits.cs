using System.Buffers.Binary;
using System.Globalization;
using UmgEditor.Core.Editing;

namespace UmgEditor.Core.Animation;

/// <summary>
/// Animation key edits ride in the ordinary EditSet: FieldKey(package, section export, "anim:&lt;channel&gt;#&lt;key&gt;"),
/// value = the key's new value (float, or the byte for visibility). One such edit writes the key's value in the section
/// and, when it has one, in the compiled copy, so both copies always agree.
/// </summary>
public static class AnimKeyPath
{
    public const string Prefix = "anim:";

    public static bool Is(FieldKey key) => key.Path.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Make(string channel, int keyIndex) => $"{Prefix}{channel}#{keyIndex.ToString(CultureInfo.InvariantCulture)}";

    public static (string Channel, int Key) Parse(string path)
    {
        var hash = path.LastIndexOf('#');
        return (path[Prefix.Length..hash], int.Parse(path[(hash + 1)..], CultureInfo.InvariantCulture));
    }
}

/// <summary>Where one animation key's value is (stock offsets): the section copy and, when there is one, the compiled copy.</summary>
/// <param name="CompiledExport">The MovieSceneCompiledData export of the animation, -1 when the channel has no compiled copy</param>
public sealed record AnimKeyRef(ChannelKind Kind, int SectionExport, string Channel, int KeyIndex, int ValueAt, int CompiledExport, int CompiledValueAt,
    string StockValue, string Animation, string Description)
{
    /// <summary>Bytes written: the float value (not its tangents), the byte, or the int32.</summary>
    public int ValueSize => Kind is ChannelKind.Float or ChannelKind.Integer ? 4 : 1;
    public bool HasCompiledCopy => CompiledValueAt >= 0;
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
                            $"{a.Name} · {AnimationText.Target(t)} · {AnimationText.ChannelName(t, c.Path)} · key {k} ({AnimationText.S(key.Seconds)})");
                    }
                }
    }

    public static AnimKeyIndex Read(GameSource source, string package) => new(AnimationReader.Read(source, package));

    public AnimKeyRef? Find(FieldKey key)
    {
        if (!AnimKeyPath.Is(key)) return null;
        var (channel, k) = AnimKeyPath.Parse(key.Path);
        return _keys.GetValueOrDefault((key.SlotExport, channel, k));
    }

    public static FieldKey KeyOf(string package, AnimTrack track, AnimChannel channel, int keyIndex) =>
        new(package, track.SectionExport, AnimKeyPath.Make(channel.Path, keyIndex));

    public static double Parse(string value) => value switch
    {
        "true" => 1,
        "false" => 0,
        _ => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
    };
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
