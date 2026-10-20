using System.Globalization;
using System.Numerics;
using UmgEditor.Core.Layout;

namespace UmgEditor.Core.Editing;

public enum Axis { X, Y }

public enum Edge { Left, Top, Right, Bottom }

/// <summary>Outcome of an edit request: either the exact field changes, or why it isn't allowed (shown greyed in the UI).</summary>
public sealed record EditResult(bool Allowed, string? Reason, IReadOnlyList<FieldChange> Changes)
{
    public static EditResult Refused(string reason) => new(false, reason, []);
    public static EditResult Of(IReadOnlyList<FieldChange> changes) => new(true, null, changes);
}

/// <summary>
/// Edit rules for CanvasPanelSlot values, matching LayoutEngine (UE SConstraintCanvas):
/// non-stretched axis (anchor min == max): Left/Top = position from the anchor point, Right/Bottom = size,
///   left edge = anchor·parent + Left − alignment·size;
/// stretched axis (min != max): Left/Right (Top/Bottom) are margins, alignment is not used.
/// Values stored in the file change in place (same size). Branch experiment: an Offsets member that isn't stored, in a
/// stored Offsets struct, can change too; saving adds it as a new tag (see IsInsertion). Everything else is refused with a reason.
/// All amounts are in the parent canvas's local units (design units inside the scoreboard canvas).
/// </summary>
public static class SlotEditor
{
    public const string CanvasSlot = "CanvasPanelSlot";

    private static readonly string[] Numeric =
    [
        "LayoutData.Offsets.Left", "LayoutData.Offsets.Top", "LayoutData.Offsets.Right", "LayoutData.Offsets.Bottom",
        "LayoutData.Anchors.Minimum.X", "LayoutData.Anchors.Minimum.Y", "LayoutData.Anchors.Maximum.X", "LayoutData.Anchors.Maximum.Y",
        "LayoutData.Alignment.X", "LayoutData.Alignment.Y", "ZOrder",
    ];

    // ---- editability ----

    /// <summary>Why the whole slot can't be edited, or null when it can (field-level checks come on top).</summary>
    public static string? SlotRefusal(WidgetNode node)
    {
        if (GamePaths.IsLibrary(node.PackagePath)) return LibraryRefusal(node.PackagePath);
        if (node.IsReadOnly)
            return $"This widget belongs to {GamePaths.ShortName(node.PackagePath)} and is shown here read-only. Open {GamePaths.ShortName(node.PackagePath)} to edit it.";
        if (node.Slot == null)
            return "This widget has no slot (it is the root of its widget tree), so it has no position to edit.";
        if (node.Slot.ClassName != CanvasSlot)
            return $"{node.Slot.ClassName} values aren't editable in this version; only CanvasPanelSlot position, size, anchors, alignment and ZOrder are.";
        return null;
    }

    /// <summary>Library widgets are blocked: an edit would change every package that embeds them, across the whole game.</summary>
    /// <param name="embeddedBy">How many packages embed it (when known), for the message</param>
    public static string LibraryRefusal(string packagePath, int? embeddedBy = null) =>
        $"{GamePaths.ShortName(packagePath)} is a Konami library widget ({GamePaths.LibraryFolder}), embedded by " +
        $"{(embeddedBy is { } n ? $"{n:N0} packages" : "a large part of the game's UI")}. An edit here would change all of them, so editing library widgets is blocked. " +
        "Edit the slot that places it in the package that uses it instead.";

    /// <summary>Why one field can't be edited, or null when it can.</summary>
    public static string? FieldRefusal(WidgetNode node, string path)
    {
        if (SlotRefusal(node) is { } r) return r;
        var f = node.Slot!.Fields.FirstOrDefault(x => x.Path == path);
        if (f == null) return $"{path} isn't a {node.Slot.ClassName} field.";
        if (path == "bAutoSize") return "bAutoSize is a boolean; boolean fields aren't editable in this version.";
        if (!Numeric.Contains(path)) return $"{path} isn't an editable layout value.";
        if (!f.Stored) return InsertRefusal(node, f);
        return null;
    }

    /// <summary>
    /// A value that isn't stored and that saving adds as a new 29-byte tag (branch experiment, canary C10): the package
    /// grows. Only an Offsets member whose Offsets struct is stored (another member of it is); the session checks the rest
    /// (name map, unique context) when the edit is applied.
    /// </summary>
    public static bool IsInsertion(WidgetNode node, string path) =>
        node.Slot?.Fields.FirstOrDefault(x => x.Path == path) is { Stored: false } && FieldRefusal(node, path) == null;

    private static string? InsertRefusal(WidgetNode node, SlotField f)
    {
        var head = $"{Short(f.Path)} is not stored in the file (engine default {f.Value})";
        if (Patching.TagInserter.Insertable(f.Path) is not { } at)
            return f.Path == "ZOrder"
                ? $"{head}; adding it needs a new top-level tag, which isn't supported yet (only a missing Offsets value can be added)."
                : $"{head}, and neither is the rest of its {StructOf(f.Path)} struct; adding a whole struct isn't supported yet (only a missing Offsets value can be added).";
        if (!node.Slot!.Fields.Any(x => x.Stored && x.Path.StartsWith(at.StructPath + ".", StringComparison.Ordinal)))
            return $"{head}, and neither is any other Offsets value; adding the whole Offsets struct isn't supported yet.";
        return null;
    }

    private static string StructOf(string path) => Short(path[..path.LastIndexOf('.')]);

    public static bool IsStretched(WidgetNode node, Axis axis, EditSet edits) => axis == Axis.X
        ? edits.Effective(node, "LayoutData.Anchors.Minimum.X") != edits.Effective(node, "LayoutData.Anchors.Maximum.X")
        : edits.Effective(node, "LayoutData.Anchors.Minimum.Y") != edits.Effective(node, "LayoutData.Anchors.Maximum.Y");

    // ---- edits ----

    /// <summary>Set one numeric field. Changes exactly that field and nothing else.</summary>
    public static EditResult SetField(WidgetNode node, EditSet edits, string path, double value)
    {
        if (FieldRefusal(node, path) is { } r) return EditResult.Refused(r);
        if (double.IsNaN(value) || double.IsInfinity(value)) return EditResult.Refused($"{Short(path)}: {value} is not a finite number.");
        return EditResult.Of([new FieldChange(FieldKey.Of(node, path), edits.Effective(node, path), StoredValue(path, value))]);
    }

    /// <summary>What SetField stores for a typed value: ZOrder rounded to an integer, everything else rounded to float32.</summary>
    public static double StoredValue(string path, double value) => path == "ZOrder" ? Math.Round(value) : F32(value);

    /// <summary>Move by (dx, dy). Non-stretched axis: Left/Top += d. Stretched axis: Left += d and Right −= d.</summary>
    public static EditResult Move(WidgetNode node, EditSet edits, double dx, double dy)
    {
        if (SlotRefusal(node) is { } r) return EditResult.Refused(r);
        var changes = new List<FieldChange>();
        foreach (var (axis, d) in new[] { (Axis.X, dx), (Axis.Y, dy) })
        {
            if (d == 0) continue;
            var (lo, hi) = Fields(axis);
            if (IsStretched(node, axis, edits))
            {
                if (Need(node, lo, hi) is { } why) return EditResult.Refused($"Can't move along {axis}: {why}");
                changes.Add(Change(node, edits, lo, +d));
                changes.Add(Change(node, edits, hi, -d));
            }
            else
            {
                if (Need(node, lo) is { } why) return EditResult.Refused($"Can't move along {axis}: {why}");
                changes.Add(Change(node, edits, lo, +d));
            }
        }
        return EditResult.Of(changes);
    }

    /// <summary>
    /// Move one edge by d (positive = right/down), keeping the opposite edge where it is.
    /// Non-stretched: size changes by ±d and Left/Top compensates for alignment so the opposite edge stays put.
    /// Stretched: only that edge's margin changes.
    /// </summary>
    public static EditResult ResizeEdge(WidgetNode node, EditSet edits, Edge edge, double d)
    {
        if (SlotRefusal(node) is { } r) return EditResult.Refused(r);
        if (d == 0) return EditResult.Of([]);
        var axis = edge is Edge.Left or Edge.Right ? Axis.X : Axis.Y;
        var (lo, hi) = Fields(axis);
        var near = edge is Edge.Left or Edge.Top; // the edge at the low side of the axis
        var changes = new List<FieldChange>();

        if (IsStretched(node, axis, edits))
        {
            var field = near ? lo : hi;
            if (Need(node, field) is { } why) return EditResult.Refused($"Can't move the {edge} edge: {why}");
            changes.Add(Change(node, edits, field, near ? +d : -d));
            return EditResult.Of(changes);
        }

        var align = edits.Effective(node, axis == Axis.X ? "LayoutData.Alignment.X" : "LayoutData.Alignment.Y");
        // near edge: size −= d, position += d·(1 − align); far edge: size += d, position += d·align
        var positionDelta = near ? d * (1 - align) : d * align;
        var sizeDelta = near ? -d : d;
        var needed = positionDelta != 0 ? new[] { hi, lo } : new[] { hi };
        if (Need(node, needed) is { } missing) return EditResult.Refused($"Can't move the {edge} edge: {missing}");
        if (edits.Effective(node, hi) + sizeDelta < 0) return EditResult.Refused($"Can't move the {edge} edge that far: the size would become negative.");
        changes.Add(Change(node, edits, hi, sizeDelta));
        if (positionDelta != 0) changes.Add(Change(node, edits, lo, positionDelta));
        return EditResult.Of(changes);
    }

    /// <summary>
    /// Set the alignment of one axis. With keepPosition the element stays where it is on screen (non-stretched axis:
    /// Left/Top += Δalignment·size), which needs that offset to be stored.
    /// </summary>
    public static EditResult SetAlignment(WidgetNode node, EditSet edits, Axis axis, double value, bool keepPosition)
    {
        var path = axis == Axis.X ? "LayoutData.Alignment.X" : "LayoutData.Alignment.Y";
        var set = SetField(node, edits, path, value);
        if (!set.Allowed || !keepPosition || IsStretched(node, axis, edits)) return set;
        var (lo, hi) = Fields(axis);
        if (Need(node, lo) is { } why) return EditResult.Refused($"Can't keep the position: {why}");
        var delta = set.Changes[0].NewValue - set.Changes[0].OldValue;
        return EditResult.Of([.. set.Changes, Change(node, edits, lo, delta * edits.Effective(node, hi))]);
    }

    /// <summary>
    /// Move the anchor point of a non-stretched axis (sets Minimum and Maximum together). With keepPosition the element
    /// stays put (Left/Top −= Δanchor·parentSize), which needs that offset to be stored.
    /// </summary>
    public static EditResult SetAnchorPoint(WidgetNode node, EditSet edits, Axis axis, double value, bool keepPosition, double parentSize)
    {
        var (min, max) = axis == Axis.X ? ("LayoutData.Anchors.Minimum.X", "LayoutData.Anchors.Maximum.X") : ("LayoutData.Anchors.Minimum.Y", "LayoutData.Anchors.Maximum.Y");
        if (IsStretched(node, axis, edits)) return EditResult.Refused($"The {axis} axis is stretched (Minimum ≠ Maximum); set Minimum and Maximum separately.");
        var a = SetField(node, edits, min, value);
        if (!a.Allowed) return a;
        var b = SetField(node, edits, max, value);
        if (!b.Allowed) return b;
        if (!keepPosition) return EditResult.Of([.. a.Changes, .. b.Changes]);
        var (lo, _) = Fields(axis);
        if (Need(node, lo) is { } why) return EditResult.Refused($"Can't keep the position: {why}");
        var delta = a.Changes[0].NewValue - a.Changes[0].OldValue;
        return EditResult.Of([.. a.Changes, .. b.Changes, Change(node, edits, lo, -delta * parentSize)]);
    }

    // ---- units ----

    /// <summary>Screen drag (viewport px) → the slot's units, through the parent's transform (DPI, ScaleBox, render transforms).</summary>
    public static Vector2 PixelsToUnits(LayoutBox box, float dxPx, float dyPx)
    {
        var parent = box.Parent?.ToViewport ?? Matrix3x2.Identity;
        if (!Matrix3x2.Invert(parent, out var inverse)) return Vector2.Zero;
        return Vector2.TransformNormal(new Vector2(dxPx, dyPx), inverse);
    }

    // ---- helpers ----

    private static (string Lo, string Hi) Fields(Axis axis) => axis == Axis.X
        ? ("LayoutData.Offsets.Left", "LayoutData.Offsets.Right")
        : ("LayoutData.Offsets.Top", "LayoutData.Offsets.Bottom");

    /// <summary>Reason the first of these fields can't change, or null when all can.</summary>
    private static string? Need(WidgetNode node, params string[] paths) =>
        paths.Select(p => FieldRefusal(node, p)).FirstOrDefault(r => r != null);

    private static FieldChange Change(WidgetNode node, EditSet edits, string path, double delta)
    {
        var old = edits.Effective(node, path);
        return new FieldChange(FieldKey.Of(node, path), old, F32(old + delta));
    }

    /// <summary>Round to what a float property can hold, so the preview shows exactly what will be written.</summary>
    public static double F32(double v) => (float) v;

    private static string Short(string path) => path.Replace("LayoutData.", "");

    public static string Format(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
}
