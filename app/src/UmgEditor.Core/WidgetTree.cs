using System.Globalization;
using CUE4Parse.UE4.Assets.Exports;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UmgEditor.Core;

/// <summary>One slot value. Tagged serialization stores only values that differ from the class default, so Stored=false means "not in the file".</summary>
/// <param name="Value">The stored value, or the default when not stored</param>
/// <param name="DefaultKnown">False when the engine default for this field is not known to the editor</param>
public sealed record SlotField(string Path, bool Stored, string Value, bool DefaultKnown = true);

/// <param name="Tracks">e.g. "Margin (LayoutData)", "Byte (Visibility)", "2DTransform (RenderTransform)"</param>
/// <param name="OnSlot">True when the animation drives the widget's slot (layout), not the widget itself</param>
public sealed record AnimationUse(string Animation, IReadOnlyList<string> Tracks, bool OnSlot)
{
    /// <summary>Margin tracks move the slot; 2D transform tracks move the rendered widget.</summary>
    public bool MovesWidget => Tracks.Any(t => t.StartsWith("Margin") || t.StartsWith("2DTransform"));

    public string TracksText => string.Join(", ", Tracks);
    public string Target => OnSlot ? "drives the slot (layout)" : "drives the widget";
}

public sealed class SlotInfo
{
    public required string Name { get; init; }
    public required string ClassName { get; init; }
    public required int ExportIndex { get; init; }
    /// <summary>Known layout fields in a fixed order; unknown slot classes list only what is stored.</summary>
    public required IReadOnlyList<SlotField> Fields { get; init; }
}

public sealed class WidgetNode
{
    public required string Name { get; init; }
    public required string ClassName { get; init; }
    public required string ClassPath { get; init; }
    public required int ExportIndex { get; init; }
    /// <summary>The package these bytes live in (the opened package, or an embedded child package).</summary>
    public required string PackagePath { get; init; }
    /// <summary>True for widgets that belong to an embedded child package: shown, never edited from here.</summary>
    public required bool IsReadOnly { get; init; }
    /// <summary>Set when this widget is an instance of another widget blueprint (e.g. MatchTimeTimer_C).</summary>
    public string? EmbeddedPackagePath { get; init; }
    public SlotInfo? Slot { get; set; }
    public List<WidgetNode> Children { get; } = [];
    public List<AnimationUse> Animations { get; } = [];
    public bool ClipsToBounds { get; init; }
    public required IReadOnlyList<KeyValuePair<string, string>> StoredProperties { get; init; }

    public bool IsAnimated => Animations.Count > 0;
    public bool IsSlotAnimated => Animations.Any(a => a.OnSlot);
}

public sealed class WidgetPackageTree
{
    public required string PackagePath { get; init; }
    public required WidgetNode? Root { get; init; }
    /// <summary>Widgets in the WidgetTree that no panel references (should be empty for cooked assets).</summary>
    public required IReadOnlyList<WidgetNode> Unattached { get; init; }
    public required int WidgetCount { get; init; }
    public required IReadOnlyList<string> AnimationNames { get; init; }

    public IEnumerable<WidgetNode> All()
    {
        var stack = new Stack<WidgetNode>(Root == null ? Unattached : Unattached.Prepend(Root));
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in n.Children) stack.Push(c);
        }
    }

    public WidgetNode? Find(string name) => All().FirstOrDefault(n => n.Name == name);
}

/// <summary>Reads a widget blueprint's hierarchy (WidgetTree → panels → slots → content) from the stock containers.</summary>
public static class WidgetTreeReader
{
    // UE 4.26 class defaults: UCanvasPanelSlot sets LayoutData.Offsets = (0, 0, 100, 30); everything else zero/false.
    private static readonly (string Path, string Default)[] CanvasSlotFields =
    [
        ("LayoutData.Offsets.Left", "0"), ("LayoutData.Offsets.Top", "0"),
        ("LayoutData.Offsets.Right", "100"), ("LayoutData.Offsets.Bottom", "30"),
        ("LayoutData.Anchors.Minimum.X", "0"), ("LayoutData.Anchors.Minimum.Y", "0"),
        ("LayoutData.Anchors.Maximum.X", "0"), ("LayoutData.Anchors.Maximum.Y", "0"),
        ("LayoutData.Alignment.X", "0"), ("LayoutData.Alignment.Y", "0"),
        ("bAutoSize", "false"), ("ZOrder", "0"),
    ];

    private static readonly (string Path, string Default)[] PaddingFields =
        [("Padding.Left", "0"), ("Padding.Top", "0"), ("Padding.Right", "0"), ("Padding.Bottom", "0")];

    private static readonly Dictionary<string, (string Path, string Default)[]> SlotFieldsByClass = new()
    {
        ["CanvasPanelSlot"] = CanvasSlotFields,
        ["ScaleBoxSlot"] = [.. PaddingFields, ("HorizontalAlignment", "HAlign_Center"), ("VerticalAlignment", "VAlign_Center")],
        ["OverlaySlot"] = [.. PaddingFields, ("HorizontalAlignment", "HAlign_Left"), ("VerticalAlignment", "VAlign_Top")],
        ["HorizontalBoxSlot"] = [.. PaddingFields, ("Size.SizeRule", "Automatic"), ("Size.Value", "1"), ("HorizontalAlignment", "HAlign_Fill"), ("VerticalAlignment", "VAlign_Fill")],
        ["VerticalBoxSlot"] = [.. PaddingFields, ("Size.SizeRule", "Automatic"), ("Size.Value", "1"), ("HorizontalAlignment", "HAlign_Fill"), ("VerticalAlignment", "VAlign_Fill")],
    };

    private static readonly HashSet<string> ReferenceProps = ["Slot", "Slots", "Parent", "Content"];

    public static WidgetPackageTree Build(GameSource source, string packagePath, bool readOnly = false)
    {
        var pkg = source.Provider.LoadPackage(GamePaths.ToFileKey(packagePath));
        var exports = pkg.ExportsLazy;
        var json = new Dictionary<int, JObject>();
        JObject Json(int i)
        {
            if (!json.TryGetValue(i, out var o))
                json[i] = o = JObject.Parse(JsonConvert.SerializeObject(exports[i].Value));
            return o;
        }
        UObject Obj(int i) => exports[i].Value;

        // a reference in JSON form points into this package as "<package>.<exportIndex>"
        int? Ref(JToken? token)
        {
            var path = token?["ObjectPath"]?.Value<string>();
            if (path == null || !GamePaths.PackageOf(path).Equals(packagePath, StringComparison.OrdinalIgnoreCase)) return null;
            return int.TryParse(path[(path.LastIndexOf('.') + 1)..], out var idx) ? idx : null;
        }

        var treeIndex = Enumerable.Range(0, exports.Length).FirstOrDefault(i => Obj(i).ExportType == "WidgetTree", -1);
        var animations = ReadAnimations(exports.Length, Obj, Json, Ref);

        var visited = new HashSet<int>();
        WidgetNode Node(int i, SlotInfo? slot)
        {
            visited.Add(i);
            var obj = Obj(i);
            var props = (JObject?) Json(i)["Properties"] ?? [];
            var classPath = obj.Class?.GetPathName() ?? obj.ExportType;
            var node = new WidgetNode
            {
                Name = obj.Name,
                ClassName = obj.ExportType,
                ClassPath = classPath,
                ExportIndex = i,
                PackagePath = packagePath,
                IsReadOnly = readOnly,
                EmbeddedPackagePath = classPath.StartsWith("/Game/") ? GamePaths.PackageOf(classPath) : null,
                ClipsToBounds = props["Clipping"]?.Value<string>()?.EndsWith("ClipToBounds") == true,
                StoredProperties = Flatten(props).Where(kv => !ReferenceProps.Contains(kv.Key.Split('.')[0])).ToList(),
                Slot = slot,
            };
            if (animations.TryGetValue(obj.Name, out var uses)) node.Animations.AddRange(uses);

            foreach (var slotRef in props["Slots"] as JArray ?? [])
            {
                if (Ref(slotRef) is not { } s) continue;
                var slotProps = (JObject?) Json(s)["Properties"] ?? [];
                var childSlot = ReadSlot(Obj(s), s, slotProps);
                if (Ref(slotProps["Content"]) is { } child && !visited.Contains(child))
                    node.Children.Add(Node(child, childSlot));
            }
            return node;
        }

        WidgetNode? root = null;
        if (treeIndex >= 0 && Ref(Json(treeIndex)["Properties"]?["RootWidget"]) is { } rootIndex)
            root = Node(rootIndex, null);

        var unattached = new List<WidgetNode>();
        if (treeIndex >= 0)
            for (int i = 0; i < exports.Length; i++)
                if (!visited.Contains(i) && Obj(i).Outer is { ExportIndex: var o } && o == treeIndex && !Obj(i).ExportType.EndsWith("Slot"))
                    unattached.Add(Node(i, null));

        return new WidgetPackageTree
        {
            PackagePath = packagePath,
            Root = root,
            Unattached = unattached,
            WidgetCount = visited.Count,
            AnimationNames = animations.Values.SelectMany(v => v).Select(a => a.Animation).Distinct().Order().ToList(),
        };
    }

    private static SlotInfo ReadSlot(UObject slot, int index, JObject props)
    {
        var stored = Flatten(props).Where(kv => !ReferenceProps.Contains(kv.Key.Split('.')[0])).ToDictionary(kv => kv.Key, kv => kv.Value);
        List<SlotField> fields;
        if (SlotFieldsByClass.TryGetValue(slot.ExportType, out var known))
        {
            fields = known.Select(f => stored.TryGetValue(f.Path, out var v) ? new SlotField(f.Path, true, v) : new SlotField(f.Path, false, f.Default)).ToList();
            // anything stored that the fixed list doesn't cover is still shown
            fields.AddRange(stored.Where(kv => known.All(f => f.Path != kv.Key)).Select(kv => new SlotField(kv.Key, true, kv.Value)));
        }
        else
        {
            fields = stored.Select(kv => new SlotField(kv.Key, true, kv.Value)).ToList();
        }
        return new SlotInfo { Name = slot.Name, ClassName = slot.ExportType, ExportIndex = index, Fields = fields };
    }

    /// <summary>widget name → how animations drive it (only bindings that actually have tracks).</summary>
    private static Dictionary<string, List<AnimationUse>> ReadAnimations(int count, Func<int, UObject> obj, Func<int, JObject> json, Func<JToken?, int?> reference)
    {
        var result = new Dictionary<string, List<AnimationUse>>();
        for (int i = 0; i < count; i++)
        {
            if (obj(i).ExportType != "WidgetAnimation") continue;
            var props = json(i)["Properties"];
            var label = props?["DisplayLabel"]?.Value<string>() ?? obj(i).Name.Replace("_INST", "");
            if (reference(props?["MovieScene"]) is not { } sceneIndex) continue;

            var tracksByGuid = new Dictionary<string, List<string>>();
            foreach (var binding in json(sceneIndex)["Properties"]?["ObjectBindings"] as JArray ?? [])
            {
                var tracks = new List<string>();
                foreach (var track in binding["Tracks"] as JArray ?? [])
                {
                    var type = track["ObjectName"]?.Value<string>()?.Split('\'')[0].Replace("MovieScene", "").Replace("Track", "") ?? "?";
                    var property = reference(track) is { } t ? json(t)["Properties"]?["PropertyBinding"]?["PropertyName"]?.Value<string>() : null;
                    tracks.Add(property != null ? $"{type} ({property})" : type);
                }
                if (tracks.Count > 0) tracksByGuid[binding["ObjectGuid"]?.Value<string>() ?? ""] = tracks;
            }

            foreach (var b in props?["AnimationBindings"] as JArray ?? [])
            {
                if (!tracksByGuid.TryGetValue(b["AnimationGuid"]?.Value<string>() ?? "", out var tracks)) continue;
                var widget = b["WidgetName"]?.Value<string>() ?? "";
                var onSlot = b["SlotWidgetName"]?.Value<string>() is { } s && s != "None";
                if (!result.TryGetValue(widget, out var list)) result[widget] = list = [];
                list.Add(new AnimationUse(label, tracks, onSlot));
            }
        }
        return result;
    }

    /// <summary>Leaf values with dotted paths, e.g. LayoutData.Offsets.Left = 129.</summary>
    private static IEnumerable<KeyValuePair<string, string>> Flatten(JToken token, string prefix = "")
    {
        switch (token)
        {
            case JObject o when !IsReference(o):
                foreach (var p in o.Properties())
                    foreach (var kv in Flatten(p.Value, prefix.Length == 0 ? p.Name : prefix + "." + p.Name))
                        yield return kv;
                break;
            case JObject reference:
                yield return new(prefix, reference["ObjectName"]?.Value<string>() ?? reference["AssetPathName"]?.Value<string>() ?? reference.ToString(Formatting.None));
                break;
            case JArray a:
                yield return new(prefix, $"[{a.Count} items]");
                break;
            case JValue v:
                yield return new(prefix, v.Type switch
                {
                    JTokenType.Float => ((double) v).ToString(CultureInfo.InvariantCulture),
                    JTokenType.Boolean => (bool) v ? "true" : "false",
                    _ => v.ToString(CultureInfo.InvariantCulture),
                });
                break;
        }
    }

    private static bool IsReference(JObject o) => o.ContainsKey("ObjectName") || o.ContainsKey("AssetPathName");
}
