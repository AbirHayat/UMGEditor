using System.Globalization;
using System.Numerics;
using UmgEditor.Core.Editing;

namespace UmgEditor.Core.Layout;

/// <summary>One value the layout used, with where it came from. Defaults never get written back into stored data.</summary>
public sealed record LayoutInput(string Name, string Value, ValueSource Source);

public enum ContentKind { None, Texture, Text, Unknown }

/// <summary>A widget placed in the viewport.</summary>
public sealed class LayoutBox
{
    public required WidgetNode Node { get; init; }
    public LayoutBox? Parent { get; init; }
    public List<LayoutBox> Children { get; } = [];

    /// <summary>Size in the widget's own units (design units for widgets inside the scaled design canvas).</summary>
    public required Vector2 LocalSize { get; init; }
    /// <summary>Local units → viewport pixels (includes DPI, ScaleBox scale and render transforms).</summary>
    public required Matrix3x2 ToViewport { get; init; }
    /// <summary>Accumulated clip rectangle in viewport pixels, or null when nothing clips.</summary>
    public RectangleF? Clip { get; init; }
    public bool Visible { get; init; } = true;
    public double Opacity { get; init; } = 1;
    public List<LayoutInput> Inputs { get; } = [];
    /// <summary>Set when the geometry is approximated (unsupported panel, estimated auto-size, missing embedded tree).</summary>
    public string? Approximation { get; set; }

    public ContentKind Content { get; init; }
    /// <summary>Texture object path for ContentKind.Texture.</summary>
    public string? TexturePath { get; init; }
    public double FontSize { get; init; }
    public string? TextJustification { get; init; }
    public string? TextVerticalAlignment { get; init; }
    public bool TintNotApplied { get; init; }

    /// <summary>Viewport pixels per local unit (uniform scale assumed for reporting).</summary>
    public double PixelsPerUnit => Math.Sqrt(Math.Abs(ToViewport.GetDeterminant()));

    /// <summary>Axis-aligned bounds in viewport pixels.</summary>
    public RectangleF Bounds
    {
        get
        {
            Span<Vector2> c = [Vector2.Transform(Vector2.Zero, ToViewport), Vector2.Transform(new(LocalSize.X, 0), ToViewport),
                Vector2.Transform(new(0, LocalSize.Y), ToViewport), Vector2.Transform(LocalSize, ToViewport)];
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in c) { minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y); }
            return new RectangleF(minX, minY, maxX - minX, maxY - minY);
        }
    }

    public IEnumerable<LayoutBox> Descendants()
    {
        foreach (var c in Children) { yield return c; foreach (var d in c.Descendants()) yield return d; }
    }
}

public readonly record struct RectangleF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public bool Contains(float px, float py) => px >= X && py >= Y && px <= Right && py <= Bottom;

    public RectangleF Intersect(RectangleF o)
    {
        float x = Math.Max(X, o.X), y = Math.Max(Y, o.Y), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return new RectangleF(x, y, Math.Max(0, r - x), Math.Max(0, b - y));
    }
}

/// <summary>A ScaleBox decision, reported so the UI can show which axis limits the scale.</summary>
public sealed record ScaleBoxInfo(string Widget, string Stretch, double ScaleX, double ScaleY, double Applied, string LimitingAxis);

public sealed class LayoutResult
{
    public LayoutBox? Root { get; internal set; }
    public required int ViewportWidth { get; init; }
    public required int ViewportHeight { get; init; }
    public required double DpiScale { get; init; }
    public required double DpiRuleInput { get; init; }
    public required UiScaleSettings Settings { get; init; }
    public List<ScaleBoxInfo> ScaleBoxes { get; } = [];

    public IEnumerable<LayoutBox> All() => Root == null ? [] : Root.Descendants().Prepend(Root);
    public LayoutBox? Find(string name) => All().FirstOrDefault(b => b.Node.Name == name);
}

/// <summary>
/// Places a widget tree in a viewport the way UMG does for the panels this game uses: CanvasPanel (anchors, offsets,
/// alignment, auto-size, ZOrder), ScaleBox (stretch rules), single-child pass-through panels, embedded widget
/// blueprints, render transforms, visibility and clipping. Other panels are stacked and flagged as approximated.
/// </summary>
public sealed class LayoutEngine(UiScaleSettings settings, Func<string, WidgetPackageTree?> loadEmbedded, Func<string, (int W, int H)?> textureSize)
{
    /// <summary>Edited values for the current Arrange (read through, never written into the stock tree).</summary>
    private EditSet _edits = EditSet.Empty;

    private const int MaxDepth = 40;
    /// <summary>Konami library widgets drawn directly (texture / text) instead of expanding their own blueprint.</summary>
    public static readonly HashSet<string> LeafClasses = ["uiImage_C", "uiText_C", "uiTextSimple_C", "uiTextScroll_C"];
    private static readonly HashSet<string> PassThrough = ["InvalidationBox", "RetainerBox", "SafeZone", "NamedSlot", "SizeBox", "Border", "BackgroundBlur", "WindowTitleBarArea"];
    private static readonly HashSet<string> Stacking = ["VerticalBox", "HorizontalBox", "VerticalBoxCustom", "HorizontalBoxCustom", "ScrollBox", "WrapBox", "UniformGridPanel", "GridPanel", "Overlay"];

    /// <param name="edits">Edited slot values to lay out with (feature 4); stock data stays untouched.</param>
    public LayoutResult Arrange(WidgetPackageTree tree, int viewportWidth, int viewportHeight, EditSet? edits = null)
    {
        if (edits != null && edits.Count > 0)
            return new LayoutEngine(settings, loadEmbedded, textureSize) { _edits = edits }.Arrange(tree, viewportWidth, viewportHeight);

        var dpi = settings.DpiScale(viewportWidth, viewportHeight);
        var result = new LayoutResult
        {
            ViewportWidth = viewportWidth,
            ViewportHeight = viewportHeight,
            DpiScale = dpi,
            DpiRuleInput = settings.RuleInput(viewportWidth, viewportHeight),
            Settings = settings,
        };
        if (tree.Root == null) return result;
        // a widget added to the viewport fills it, in slate units
        var size = new Vector2((float) (viewportWidth / dpi), (float) (viewportHeight / dpi));
        result.Root = Place(tree.Root, null, Vector2.Zero, size, Matrix3x2.CreateScale((float) dpi), null, result, 0);
        return result;
    }

    private LayoutBox? Place(WidgetNode node, LayoutBox? parent, Vector2 position, Vector2 size, Matrix3x2 parentToViewport,
        RectangleF? clip, LayoutResult result, int depth)
    {
        if (depth > MaxDepth) return null;
        var visibility = Prop(node, "Visibility") ?? "ESlateVisibility::Visible";
        if (visibility.EndsWith("Collapsed")) return null;

        var local = Matrix3x2.CreateTranslation(position);
        var renderTransform = RenderTransform(node, size, out var rtInputs);
        var toViewport = renderTransform * local * parentToViewport;
        var opacity = double.TryParse(Prop(node, "RenderOpacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o : 1.0;

        var box = new LayoutBox
        {
            Node = node,
            Parent = parent,
            LocalSize = size,
            ToViewport = toViewport,
            Clip = clip,
            Visible = !visibility.EndsWith("Hidden"),
            Opacity = opacity,
            Content = ContentOf(node),
            TexturePath = node.ClassName == "uiImage_C" ? Prop(node, "texture") : null,
            FontSize = double.TryParse(Prop(node, "_font.Size"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fs) ? fs : 0,
            TextJustification = Prop(node, "Justification"),
            TextVerticalAlignment = Prop(node, "VerticalAlignment"),
            TintNotApplied = Prop(node, "color") != null,
        };
        box.Inputs.Add(new("Visibility", visibility, Prop(node, "Visibility") != null ? ValueSource.Stored : ValueSource.EngineDefault));
        box.Inputs.AddRange(rtInputs);

        var childClip = clip;
        if (node.ClipsToBounds)
        {
            var b = box.Bounds;
            childClip = clip is { } c ? c.Intersect(b) : b;
        }

        if (node.EmbeddedPackagePath != null && !LeafClasses.Contains(node.ClassName))
        {
            var embedded = loadEmbedded(node.EmbeddedPackagePath);
            if (embedded?.Root != null)
            {
                if (Place(embedded.Root, box, Vector2.Zero, size, toViewport, childClip, result, depth + 1) is { } inner) box.Children.Add(inner);
            }
            else if (embedded == null)
                box.Approximation = "embedded widget could not be loaded";
            return box;
        }

        switch (node.ClassName)
        {
            case "CanvasPanel":
                ArrangeCanvas(box, childClip, result, depth);
                break;
            case "ScaleBox":
                ArrangeScaleBox(box, childClip, result, depth);
                break;
            default:
                if (PassThrough.Contains(node.ClassName))
                {
                    foreach (var child in node.Children)
                        if (Place(child, box, Vector2.Zero, size, toViewport, childClip, result, depth + 1) is { } c) box.Children.Add(c);
                }
                else if (node.Children.Count > 0)
                {
                    ArrangeStack(box, childClip, result, depth);
                }
                break;
        }
        return box;
    }

    // ---- CanvasPanel (SConstraintCanvas::ArrangeChildren) ----

    private void ArrangeCanvas(LayoutBox box, RectangleF? clip, LayoutResult result, int depth)
    {
        var parentSize = box.LocalSize;
        var ordered = box.Node.Children.Select((c, i) => (c, i))
            .OrderBy(t => SlotNum(t.c, "ZOrder").Value).ThenBy(t => t.i).Select(t => t.c);
        foreach (var child in ordered)
        {
            var s = CanvasSlot(child);
            var size = new Vector2((float) s.Right.Value, (float) s.Bottom.Value);
            if (s.AutoSize.Value != 0) size = DesiredSize(child, depth + 1, out _);

            float x, y, w, h;
            if (s.MinX.Value == s.MaxX.Value)
            {
                w = size.X;
                x = (float) (parentSize.X * s.MinX.Value + s.Left.Value - s.AlignX.Value * w);
            }
            else
            {
                x = (float) (parentSize.X * s.MinX.Value + s.Left.Value);
                w = (float) (parentSize.X * s.MaxX.Value - s.Right.Value) - x;
            }
            if (s.MinY.Value == s.MaxY.Value)
            {
                h = size.Y;
                y = (float) (parentSize.Y * s.MinY.Value + s.Top.Value - s.AlignY.Value * h);
            }
            else
            {
                y = (float) (parentSize.Y * s.MinY.Value + s.Top.Value);
                h = (float) (parentSize.Y * s.MaxY.Value - s.Bottom.Value) - y;
            }

            var placed = Place(child, box, new Vector2(x, y), new Vector2(Math.Max(0, w), Math.Max(0, h)), box.ToViewport, clip, result, depth + 1);
            if (placed == null) continue;
            placed.Inputs.InsertRange(0, s.All);
            if (s.AutoSize.Value != 0) placed.Approximation ??= "auto-size estimated from content";
            box.Children.Add(placed);
        }
    }

    private sealed record CanvasSlotValues(Num Left, Num Top, Num Right, Num Bottom, Num MinX, Num MinY, Num MaxX, Num MaxY, Num AlignX, Num AlignY, Num AutoSize, Num ZOrder)
    {
        public IEnumerable<LayoutInput> All => new[]
        {
            ("Offsets.Left", Left), ("Offsets.Top", Top), ("Offsets.Right", Right), ("Offsets.Bottom", Bottom),
            ("Anchors.Minimum.X", MinX), ("Anchors.Minimum.Y", MinY), ("Anchors.Maximum.X", MaxX), ("Anchors.Maximum.Y", MaxY),
            ("Alignment.X", AlignX), ("Alignment.Y", AlignY), ("bAutoSize", AutoSize), ("ZOrder", ZOrder),
        }.Select(t => new LayoutInput("Slot." + t.Item1, t.Item2.Text, t.Item2.Source));
    }

    private CanvasSlotValues CanvasSlot(WidgetNode child)
    {
        Num F(string path) => SlotNum(child, path);
        return new(F("LayoutData.Offsets.Left"), F("LayoutData.Offsets.Top"), F("LayoutData.Offsets.Right"), F("LayoutData.Offsets.Bottom"),
            F("LayoutData.Anchors.Minimum.X"), F("LayoutData.Anchors.Minimum.Y"), F("LayoutData.Anchors.Maximum.X"), F("LayoutData.Anchors.Maximum.Y"),
            F("LayoutData.Alignment.X"), F("LayoutData.Alignment.Y"), F("bAutoSize"), F("ZOrder"));
    }

    // ---- ScaleBox (SScaleBox) ----

    private void ArrangeScaleBox(LayoutBox box, RectangleF? clip, LayoutResult result, int depth)
    {
        var child = box.Node.Children.FirstOrDefault();
        if (child == null) return;
        var stretch = Prop(box.Node, "Stretch")?.Split("::")[^1] ?? "ScaleToFit";
        var direction = Prop(box.Node, "StretchDirection")?.Split("::")[^1] ?? "Both";
        var user = double.TryParse(Prop(box.Node, "UserSpecifiedScale"), NumberStyles.Float, CultureInfo.InvariantCulture, out var us) ? us : 1.0;
        box.Inputs.Add(new("ScaleBox.Stretch", stretch, Prop(box.Node, "Stretch") != null ? ValueSource.Stored : ValueSource.EngineDefault));
        box.Inputs.Add(new("ScaleBox.StretchDirection", direction, Prop(box.Node, "StretchDirection") != null ? ValueSource.Stored : ValueSource.EngineDefault));

        var desired = DesiredSize(child, depth + 1, out var estimated);
        var area = box.LocalSize;
        double sx = desired.X > 0 ? area.X / desired.X : 1, sy = desired.Y > 0 ? area.Y / desired.Y : 1;
        double scale = stretch switch
        {
            "None" => 1,
            "UserSpecified" => user,
            "ScaleToFitX" => sx,
            "ScaleToFitY" => sy,
            "ScaleToFill" => Math.Max(sx, sy),
            "Fill" => Math.Min(sx, sy), // non-uniform in UE; reported, drawn uniform
            _ => Math.Min(sx, sy),      // ScaleToFit
        };
        if (direction == "DownOnly") scale = Math.Min(scale, 1);
        if (direction == "UpOnly") scale = Math.Max(scale, 1);
        var limiting = stretch switch
        {
            "ScaleToFitX" => "width", "ScaleToFitY" => "height", "ScaleToFill" => sx >= sy ? "width" : "height",
            "None" or "UserSpecified" => "none", _ => sx <= sy ? "width" : "height",
        };
        result.ScaleBoxes.Add(new ScaleBoxInfo(box.Node.Name, stretch, sx, sy, scale, limiting));
        box.Inputs.Add(new("ScaleBox.Scale", $"{scale:0.#####} ({limiting} limits; x {sx:0.#####}, y {sy:0.#####})", ValueSource.Computed));

        // the scaled child is aligned inside the box by its ScaleBoxSlot (padding ignored when 0)
        var scaled = desired * (float) scale;
        var hAlign = SlotText(child, "HorizontalAlignment", "HAlign_Center");
        var vAlign = SlotText(child, "VerticalAlignment", "VAlign_Center");
        float x = Align(hAlign.Value, area.X, scaled.X), y = Align(vAlign.Value, area.Y, scaled.Y);
        var childToViewport = Matrix3x2.CreateScale((float) scale) * Matrix3x2.CreateTranslation(x, y) * box.ToViewport;
        var placed = Place(child, box, Vector2.Zero, desired, childToViewport, clip, result, depth + 1);
        if (placed == null) return;
        placed.Inputs.Insert(0, new("Slot.HorizontalAlignment", hAlign.Value, hAlign.Source));
        placed.Inputs.Insert(1, new("Slot.VerticalAlignment", vAlign.Value, vAlign.Source));
        if (estimated) placed.Approximation ??= "desired size estimated";
        box.Children.Add(placed);
    }

    private static float Align(string alignment, float area, float content) => alignment switch
    {
        var a when a.EndsWith("Left") || a.EndsWith("Top") || a.EndsWith("Fill") => 0,
        var a when a.EndsWith("Right") || a.EndsWith("Bottom") => area - content,
        _ => (area - content) / 2,
    };

    // ---- other panels: simple stacking, flagged ----

    private void ArrangeStack(LayoutBox box, RectangleF? clip, LayoutResult result, int depth)
    {
        var horizontal = box.Node.ClassName.StartsWith("Horizontal") || box.Node.ClassName == "WrapBox";
        var overlay = box.Node.ClassName == "Overlay" || !Stacking.Contains(box.Node.ClassName);
        float cursor = 0;
        foreach (var child in box.Node.Children)
        {
            Vector2 pos, size;
            if (overlay) { pos = Vector2.Zero; size = box.LocalSize; }
            else
            {
                var d = DesiredSize(child, depth + 1, out _);
                size = horizontal ? new Vector2(d.X, box.LocalSize.Y) : new Vector2(box.LocalSize.X, d.Y);
                pos = horizontal ? new Vector2(cursor, 0) : new Vector2(0, cursor);
                cursor += horizontal ? d.X : d.Y;
            }
            var placed = Place(child, box, pos, size, box.ToViewport, clip, result, depth + 1);
            if (placed == null) continue;
            placed.Approximation ??= $"layout approximated ({box.Node.ClassName} not modelled)";
            box.Children.Add(placed);
        }
    }

    // ---- desired size (SConstraintCanvas::ComputeDesiredSize etc.) ----

    private Vector2 DesiredSize(WidgetNode node, int depth, out bool estimated)
    {
        estimated = false;
        if (depth > MaxDepth) return Vector2.Zero;
        if (node.EmbeddedPackagePath != null && !LeafClasses.Contains(node.ClassName))
        {
            var embedded = loadEmbedded(node.EmbeddedPackagePath);
            return embedded?.Root != null ? DesiredSize(embedded.Root, depth + 1, out estimated) : Vector2.Zero;
        }
        switch (node.ClassName)
        {
            case "CanvasPanel":
            {
                var result = Vector2.Zero;
                foreach (var c in node.Children)
                {
                    if ((Prop(c, "Visibility") ?? "").EndsWith("Collapsed")) continue;
                    var s = CanvasSlot(c);
                    var size = new Vector2((float) s.Right.Value, (float) s.Bottom.Value);
                    if (s.AutoSize.Value != 0) { size = DesiredSize(c, depth + 1, out var e); estimated |= e; }
                    bool dockedX = s.MinX.Value == s.MaxX.Value && (s.MinX.Value == 0 || s.MinX.Value == 1);
                    bool dockedY = s.MinY.Value == s.MaxY.Value && (s.MinY.Value == 0 || s.MinY.Value == 1);
                    result.X = Math.Max(result.X, size.X + (dockedX ? (float) Math.Abs(s.Left.Value) : 0));
                    result.Y = Math.Max(result.Y, size.Y + (dockedY ? (float) Math.Abs(s.Top.Value) : 0));
                }
                return result;
            }
            case "uiImage_C":
            {
                estimated = true;
                return Prop(node, "texture") is { } t && textureSize(t) is { } ts ? new Vector2(ts.W, ts.H) : Vector2.Zero;
            }
            case "uiText_C":
            {
                estimated = true;
                var fs = double.TryParse(Prop(node, "_font.Size"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 24;
                return new Vector2((float) (fs * 0.6 * Math.Max(1, node.Name.Length / 2)), (float) (fs * 1.25));
            }
            default:
                if (node.Children.Count == 0) { estimated = true; return Vector2.Zero; }
                var max = Vector2.Zero;
                foreach (var c in node.Children) { max = Vector2.Max(max, DesiredSize(c, depth + 1, out var e)); estimated |= e; }
                return max;
        }
    }

    // ---- render transform ----

    private static Matrix3x2 RenderTransform(WidgetNode node, Vector2 size, out List<LayoutInput> stored)
    {
        var inputs = new List<LayoutInput>();
        stored = inputs;
        double D(string key, double def)
        {
            var v = Prop(node, key);
            if (v == null) return def;
            inputs.Add(new LayoutInput(key, v, ValueSource.Stored));
            return double.Parse(v, CultureInfo.InvariantCulture);
        }
        double tx = D("RenderTransform.Translation.X", 0), ty = D("RenderTransform.Translation.Y", 0);
        double scx = D("RenderTransform.Scale.X", 1), scy = D("RenderTransform.Scale.Y", 1);
        double angle = D("RenderTransform.Angle", 0);
        double shx = D("RenderTransform.Shear.X", 0), shy = D("RenderTransform.Shear.Y", 0);
        double px = D("RenderTransformPivot.X", 0.5), py = D("RenderTransformPivot.Y", 0.5);
        if (tx == 0 && ty == 0 && scx == 1 && scy == 1 && angle == 0 && shx == 0 && shy == 0) return Matrix3x2.Identity;

        var pivot = new Vector2((float) (px * size.X), (float) (py * size.Y));
        var shear = new Matrix3x2(1, (float) Math.Tan(shy * Math.PI / 180), (float) Math.Tan(shx * Math.PI / 180), 1, 0, 0);
        return Matrix3x2.CreateTranslation(-pivot)
               * Matrix3x2.CreateScale((float) scx, (float) scy)
               * shear
               * Matrix3x2.CreateRotation((float) (angle * Math.PI / 180))
               * Matrix3x2.CreateTranslation(pivot)
               * Matrix3x2.CreateTranslation((float) tx, (float) ty);
    }

    // ---- value access ----

    private readonly record struct Num(double Value, string Text, ValueSource Source);
    private readonly record struct Txt(string Value, ValueSource Source);

    private Num SlotNum(WidgetNode node, string path)
    {
        if (node.Slot != null && _edits.TryGet(FieldKey.Of(node, path), out var edited))
            return new Num(edited, edited.ToString(CultureInfo.InvariantCulture), ValueSource.Edited);
        var f = node.Slot?.Fields.FirstOrDefault(x => x.Path == path);
        if (f == null) return new Num(path.EndsWith("Right") ? 100 : path.EndsWith("Bottom") ? 30 : 0, "(no slot)", ValueSource.Assumed);
        var value = f.Value is "true" ? 1 : f.Value is "false" ? 0 : double.Parse(f.Value, CultureInfo.InvariantCulture);
        return new Num(value, f.Value, f.Stored ? ValueSource.Stored : ValueSource.EngineDefault);
    }

    private static Txt SlotText(WidgetNode node, string path, string fallback)
    {
        var f = node.Slot?.Fields.FirstOrDefault(x => x.Path == path);
        return f == null ? new Txt(fallback, ValueSource.Assumed) : new Txt(f.Value, f.Stored ? ValueSource.Stored : ValueSource.EngineDefault);
    }

    private static string? Prop(WidgetNode node, string key) =>
        node.StoredProperties.FirstOrDefault(kv => kv.Key == key) is { Key: not null } kv ? kv.Value : null;

    private static ContentKind ContentOf(WidgetNode node) => node.ClassName switch
    {
        "uiImage_C" => ContentKind.Texture,
        "uiText_C" or "uiTextSimple_C" or "uiTextScroll_C" or "TextBlock" => ContentKind.Text,
        _ when node.Children.Count == 0 && node.EmbeddedPackagePath == null && !PassThrough.Contains(node.ClassName)
               && node.ClassName is not ("CanvasPanel" or "ScaleBox") => ContentKind.Unknown,
        _ => ContentKind.None,
    };
}
