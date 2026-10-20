using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Animation;

/// <param name="Frame">Key time in ticks of the movie scene's TickResolution (UMG: 60000 per second)</param>
/// <param name="Interp">Float keys: Linear, Constant (stepped), Cubic or None; null for byte/bool/integer keys</param>
/// <param name="FrameAt">Offset of the key's frame (int32) in the raw chunk, -1 when not located</param>
/// <param name="ValueAt">Offset of the key's value in the raw chunk (float: the 28-byte FMovieSceneFloatValue), -1 when not located</param>
/// <param name="CompiledFrameAt">The same for the compiled copy in MovieSceneCompiledData, -1 when there is none or it wasn't located</param>
public sealed record AnimKey(int Frame, double Seconds, string Value, string? Interp = null, int FrameAt = -1, int ValueAt = -1,
    int CompiledFrameAt = -1, int CompiledValueAt = -1);

/// <param name="Path">Channel inside the section, e.g. "LeftCurve" or "Translation[1]"</param>
/// <param name="Default">The channel's default value when it has one (used where there are no keys)</param>
/// <param name="Compiled">"same" when MovieSceneCompiledData's copy has identical keys, otherwise what differs</param>
/// <param name="Kind">Float, Byte, Bool or Integer channel; null when its bytes weren't located</param>
/// <param name="CompiledAt">Offset of the compiled copy's first frame, null when there is none or it wasn't located</param>
public sealed record AnimChannel(string Path, IReadOnlyList<AnimKey> Keys, string? Default, string Compiled, ChannelKind? Kind = null, int? CompiledAt = null)
{
    /// <summary>Offset of the section's key block (the times' header), -1 when not located.</summary>
    public int BlockAt { get; init; } = -1;
}

/// <param name="Widget">Bound widget name (the slot's content widget when OnSlot)</param>
/// <param name="Track">Track type without the MovieScene prefix, e.g. "MarginTrack"</param>
/// <param name="Property">Bound property path, e.g. "LayoutData.Offsets" or "RenderTransform"</param>
/// <param name="SectionExport">Export index of the section (its keys live there and in the compiled copy)</param>
public sealed record AnimTrack(string Widget, bool OnSlot, string Track, string Property, int SectionExport, int? SectionStart, int? SectionEnd,
    string CompletionMode, IReadOnlyList<AnimChannel> Channels);

/// <param name="Start">Playback range start in ticks</param>
public sealed record AnimationInfo(string Name, int TickResolution, int? Start, int? End, IReadOnlyList<AnimTrack> Tracks)
{
    public double Seconds(int? frame) => frame is { } f && TickResolution > 0 ? (double) f / TickResolution : double.NaN;
}

/// <summary>
/// Reads widget animations down to their keys: WidgetAnimation → MovieScene → bindings → tracks → sections → channels,
/// and checks every channel against the copy in MovieSceneCompiledData.EvaluationTemplate (what 4.26 evaluates).
/// Every key also gets its interp mode and the offsets of its frame and value in the raw chunk (AnimationBytes), and
/// every channel the offset of its compiled copy.
/// </summary>
public static class AnimationReader
{
    public static IReadOnlyList<AnimationInfo> Read(GameSource source, string packagePath, Func<string, bool>? animationFilter = null)
    {
        var pkg = source.LoadPackage(packagePath);
        var exports = pkg.ExportsLazy;
        byte[]? bytes = null;
        ZenPackageLayout? layout = null;
        try
        {
            bytes = source.ReadPackageBytes(packagePath).Bytes;
            layout = ZenPackageLayout.Read(bytes, (IoPackage) pkg);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidCastException or KeyNotFoundException) { } // keys without offsets
        var located = new Dictionary<int, Dictionary<string, ChannelBytes>>();
        Dictionary<string, ChannelBytes> Located(int section)
        {
            if (located.TryGetValue(section, out var l)) return l;
            try { l = bytes != null && layout != null ? AnimationBytes.Locate(bytes, layout, section) : []; }
            catch (KeyNotFoundException) { l = []; }
            return located[section] = l;
        }
        var json = new Dictionary<int, JObject>();
        JObject Json(int i)
        {
            if (!json.TryGetValue(i, out var o)) json[i] = o = JObject.Parse(JsonConvert.SerializeObject(exports[i].Value));
            return o;
        }
        UObject Obj(int i) => exports[i].Value;
        int? Ref(JToken? token)
        {
            var path = token?["ObjectPath"]?.Value<string>();
            if (path == null || !GamePaths.PackageOf(path).Equals(packagePath, StringComparison.OrdinalIgnoreCase)) return null;
            return int.TryParse(path[(path.LastIndexOf('.') + 1)..], out var idx) ? idx : null;
        }

        // compiled data: one per WidgetAnimation, child templates point back at their source section
        var compiledBySection = new Dictionary<int, JObject>();
        var compiledAt = new Dictionary<(int Section, string Path), CompiledKeys?>();
        for (int i = 0; i < exports.Length; i++)
        {
            if (Obj(i).ExportType != "MovieSceneCompiledData") continue;
            var order = new List<(int Section, string Path, ChannelBytes Bytes)>();
            foreach (var track in Json(i)["Properties"]?["EvaluationTemplate"]?["Tracks"] as JArray ?? [])
                foreach (var child in track["Value"]?["ChildTemplates"] as JArray ?? [])
                    if (child["Data"] is JObject data && Ref(data["SourceSectionPtr"]) is { } s)
                    {
                        compiledBySection[s] = data;
                        foreach (var (path, _) in Channels(data, ""))
                            if (Located(s).TryGetValue(path, out var b)) order.Add((s, path, b));
                    }
            if (order.Count == 0 || bytes == null || layout == null) continue;
            foreach (var (o, at) in order.Zip(AnimationBytes.LocateInOrder(bytes, layout, i, order.Select(o => o.Bytes).ToList())))
                compiledAt[(o.Section, o.Path)] = at;
        }

        var result = new List<AnimationInfo>();
        for (int i = 0; i < exports.Length; i++)
        {
            if (Obj(i).ExportType != "WidgetAnimation") continue;
            var props = Json(i)["Properties"];
            var name = props?["DisplayLabel"]?.Value<string>() ?? Obj(i).Name.Replace("_INST", "");
            if (animationFilter != null && !animationFilter(name)) continue;
            if (Ref(props?["MovieScene"]) is not { } sceneIndex) continue;
            var scene = Json(sceneIndex)["Properties"];
            var tick = scene?["TickResolution"]?["Numerator"]?.Value<int>() ?? 60000;
            var range = scene?["PlaybackRange"]?["Value"];

            var widgets = new Dictionary<string, (string Widget, bool OnSlot)>();
            foreach (var b in props?["AnimationBindings"] as JArray ?? [])
                widgets[b["AnimationGuid"]?.Value<string>() ?? ""] = (b["WidgetName"]?.Value<string>() ?? "?", b["SlotWidgetName"]?.Value<string>() is { } s && s != "None");

            var tracks = new List<AnimTrack>();
            foreach (var binding in scene?["ObjectBindings"] as JArray ?? [])
            {
                var (widget, onSlot) = widgets.GetValueOrDefault(binding["ObjectGuid"]?.Value<string>() ?? "", ("?", false));
                foreach (var trackRef in binding["Tracks"] as JArray ?? [])
                {
                    if (Ref(trackRef) is not { } t) continue;
                    var trackProps = Json(t)["Properties"];
                    var type = Obj(t).ExportType.Replace("MovieScene", "");
                    var property = trackProps?["PropertyBinding"]?["PropertyPath"]?.Value<string>() ?? trackProps?["PropertyBinding"]?["PropertyName"]?.Value<string>() ?? "";
                    foreach (var sectionRef in trackProps?["Sections"] as JArray ?? [])
                    {
                        if (Ref(sectionRef) is not { } s) continue;
                        var section = (JObject?) Json(s)["Properties"] ?? [];
                        compiledBySection.TryGetValue(s, out var compiled);
                        var channels = new List<AnimChannel>();
                        foreach (var (path, ch) in Channels(section, ""))
                        {
                            var b = Located(s).GetValueOrDefault(path);
                            var c = compiledAt.GetValueOrDefault((s, path));
                            channels.Add(new AnimChannel(path, Keys(ch, tick, b, c), Default(ch), CompareCompiled(path, ch, compiled), b?.Kind,
                                c?.BlockStart) { BlockAt = b?.BlockStart ?? -1 });
                        }
                        var r = section["SectionRange"]?["Value"];
                        tracks.Add(new AnimTrack(widget, onSlot, type, property, s, Bound(r?["LowerBound"]), Bound(r?["UpperBound"]),
                            compiled?["CompletionMode"]?.Value<string>()?.Replace("EMovieSceneCompletionMode::", "") ?? "?", channels));
                    }
                }
            }
            result.Add(new AnimationInfo(name, tick, Bound(range?["LowerBound"]), Bound(range?["UpperBound"]), tracks));
        }
        return result.OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every keyframe channel (an object with Times + Values) below a section's properties, with its path.</summary>
    private static IEnumerable<(string Path, JObject Channel)> Channels(JToken token, string path)
    {
        switch (token)
        {
            case JObject o when o["Times"] is JArray && o["Values"] is JArray:
                yield return (path, o);
                break;
            case JObject o:
                foreach (var p in o.Properties())
                {
                    if (p.Name is "SourceSectionPtr" or "Easing" or "SectionRange") continue;
                    foreach (var c in Channels(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name)) yield return c;
                }
                break;
            case JArray a:
                for (int i = 0; i < a.Count; i++)
                    foreach (var c in Channels(a[i], $"{path}[{i}]")) yield return c;
                break;
        }
    }

    private static readonly string[] InterpModes = ["Linear", "Constant", "Cubic", "None"]; // ERichCurveInterpMode

    private static IReadOnlyList<AnimKey> Keys(JObject channel, int tick, ChannelBytes? bytes, CompiledKeys? compiled)
    {
        var times = (JArray) channel["Times"]!;
        var values = (JArray) channel["Values"]!;
        var keys = new List<AnimKey>();
        if (bytes != null && bytes.FramesAt.Count != times.Count) bytes = null; // not the channel the parse has
        if (bytes == null) compiled = null;
        for (int k = 0; k < times.Count; k++)
        {
            var frame = times[k]["Value"]?.Value<int>() ?? times[k].Value<int>();
            var v = k < values.Count ? values[k] : null;
            var value = v is JObject vo && vo["Value"] is { } inner ? inner.ToString(Formatting.None) : v?.ToString(Formatting.None) ?? "?";
            var interp = v is JObject io && io["InterpMode"]?.Value<int>() is { } m ? m >= 0 && m < InterpModes.Length ? InterpModes[m] : m.ToString() : null;
            keys.Add(new AnimKey(frame, (double) frame / tick, value, interp, bytes?.FramesAt[k] ?? -1, bytes?.ValuesAt[k] ?? -1,
                compiled?.FramesAt[k] ?? -1, compiled?.ValuesAt[k] ?? -1));
        }
        return keys;
    }

    private static string? Default(JObject channel) =>
        channel["bHasDefaultValue"]?.Value<bool>() == true ? channel["DefaultValue"]?.ToString(Formatting.None) : null;

    private static string CompareCompiled(string path, JObject channel, JObject? compiled)
    {
        if (compiled == null) return "no compiled copy";
        var other = Channels(compiled, "").FirstOrDefault(c => c.Path == path).Channel;
        if (other == null) return "channel missing in compiled copy";
        string Sig(JObject c) => c["Times"]!.ToString(Formatting.None) + c["Values"]!.ToString(Formatting.None) + Default(c);
        return Sig(channel) == Sig(other) ? "same" : "DIFFERS";
    }

    /// <summary>TRange bound: Type 0 exclusive, 1 inclusive, 2 open (null).</summary>
    private static int? Bound(JToken? bound) =>
        bound == null || bound["Type"]?.Value<int>() == 2 ? null : bound["Value"]?["Value"]?.Value<int>();
}
