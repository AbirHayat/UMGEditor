using System.Numerics;
using UmgEditor.Core.Layout;

namespace UmgEditor.Core.Editing;

/// <summary>What a preview drag grabs: the box (move) or one edge (resize).</summary>
public enum DragHandle { Move, Left, Top, Right, Bottom }

/// <summary>
/// Which handles a widget offers, each with the reason when it can't be used. Probed with 1-unit edits through
/// SlotEditor (Move along each axis, each edge grown by one unit), so the rules are exactly the editing rules.
/// </summary>
public sealed record DragAvailability(bool MoveX, bool MoveY, IReadOnlyDictionary<DragHandle, string?> Refusals)
{
    public static readonly DragHandle[] Edges = [DragHandle.Left, DragHandle.Top, DragHandle.Right, DragHandle.Bottom];

    public bool Allows(DragHandle handle) => Refusals.TryGetValue(handle, out var why) && why == null;
    public bool Any => Refusals.Values.Any(r => r == null);
    /// <summary>Shown instead of handles when none can be used: the slot's reason, or each handle's.</summary>
    public string? Reason => Any ? null
        : Refusals.Values.Distinct().Count() == 1 ? Refusals.Values.First()
        : string.Join(" ", Refusals.Values.Distinct());

    public static DragAvailability Blocked(string reason) =>
        new(false, false, Enum.GetValues<DragHandle>().ToDictionary(h => h, _ => (string?) reason));

    public static DragAvailability For(WidgetNode node, EditSet edits)
    {
        if (SlotEditor.SlotRefusal(node) is { } blocked) return Blocked(blocked);
        var x = SlotEditor.Move(node, edits, 1, 0);
        var y = SlotEditor.Move(node, edits, 0, 1);
        var refusals = new Dictionary<DragHandle, string?>
        {
            [DragHandle.Move] = x.Allowed || y.Allowed ? null : $"{x.Reason} {y.Reason}",
        };
        foreach (var edge in Edges)
        {
            // probe by growing the box one unit, so a zero size never refuses the probe
            var grow = edge is DragHandle.Left or DragHandle.Top ? -1 : 1;
            var r = SlotEditor.ResizeEdge(node, edits, ToEdge(edge), grow);
            refusals[edge] = r.Allowed ? null : r.Reason;
        }
        return new DragAvailability(x.Allowed, y.Allowed, refusals);
    }

    internal static Edge ToEdge(DragHandle h) => h switch
    {
        DragHandle.Left => Edge.Left, DragHandle.Top => Edge.Top, DragHandle.Right => Edge.Right, DragHandle.Bottom => Edge.Bottom,
        _ => throw new ArgumentOutOfRangeException(nameof(h)),
    };
}

/// <summary>One position of a drag: the units moved, the edit it makes, the layout with it, and the warnings for it.</summary>
public sealed record DragStep(Vector2 Units, EditResult Edit, LayoutResult Layout, IReadOnlyList<EditWarning> Warnings)
{
    public bool IsNoOp => Edit.Allowed && Edit.Changes.All(c => c.OldValue.Equals(c.NewValue));

    /// <summary>"Offsets.Left 126 → 146 (+20 u)", or why the edit is refused.</summary>
    public string Text => !Edit.Allowed ? Edit.Reason ?? "not allowed"
        : Edit.Changes.Count == 0 ? "no change"
        : string.Join(", ", Edit.Changes.Select(c =>
            $"{c.Key.Path.Replace("LayoutData.", "")} {SlotEditor.Format(c.OldValue)} → {SlotEditor.Format(c.NewValue)} ({EdgeChange.S(c.NewValue - c.OldValue)} u)"));
}

/// <summary>
/// A drag in the preview, from mouse-down to mouse-up. It never changes the project: each Update lays the edit out on
/// top of the edits the drag started from (live preview + warnings), and the controller applies the final step as one
/// ordinary edit (one undo step), the same edit SlotEditor makes for typed numbers. Cancelling is just not committing.
/// Pixel deltas are converted to the slot's units through its parent's transform and, unless free, rounded to whole
/// units (the delta is rounded, so a stretched move keeps the width and fractional stored values keep their fraction).
/// </summary>
public sealed class SlotDrag
{
    private readonly LayoutEngine _engine;
    private readonly WidgetPackageTree _tree;
    private readonly int _width, _height;
    private DragStep? _last;

    public SlotDrag(LayoutEngine engine, WidgetPackageTree tree, WidgetNode node, DragHandle handle, EditSet edits, DragAvailability availability, int viewportWidth, int viewportHeight)
    {
        _engine = engine;
        _tree = tree;
        Node = node;
        Handle = handle;
        StartEdits = edits;
        Availability = availability;
        (_width, _height) = (viewportWidth, viewportHeight);
        Before = engine.Arrange(tree, viewportWidth, viewportHeight, edits);
        StartBox = Before.All().FirstOrDefault(b => ReferenceEquals(b.Node, node));
    }

    public WidgetNode Node { get; }
    public DragHandle Handle { get; }
    /// <summary>The project's edits when the drag started (the drag's edit is computed on top of them).</summary>
    public EditSet StartEdits { get; }
    public DragAvailability Availability { get; }
    public LayoutResult Before { get; }
    public LayoutBox? StartBox { get; }
    public DragStep? Last => _last;

    /// <summary>The slot's units for a drag of (dx, dy) viewport px: snapped to whole units unless free, limited to the handle's axis.</summary>
    public Vector2 UnitsFor(float dxPx, float dyPx, bool free)
    {
        if (StartBox == null) return Vector2.Zero;
        var u = SlotEditor.PixelsToUnits(StartBox, dxPx, dyPx);
        if (!free) u = new Vector2(MathF.Round(u.X, MidpointRounding.AwayFromZero), MathF.Round(u.Y, MidpointRounding.AwayFromZero));
        return Handle switch
        {
            DragHandle.Move => new Vector2(Availability.MoveX ? u.X : 0, Availability.MoveY ? u.Y : 0),
            DragHandle.Left or DragHandle.Right => new Vector2(u.X, 0),
            _ => new Vector2(0, u.Y),
        };
    }

    /// <summary>The edit for a drag of these units, on any EditSet (the controller applies it to the package's session).</summary>
    public EditResult EditFor(EditSet edits, Vector2 units) => Handle == DragHandle.Move
        ? SlotEditor.Move(Node, edits, units.X, units.Y)
        : SlotEditor.ResizeEdge(Node, edits, DragAvailability.ToEdge(Handle), Handle is DragHandle.Left or DragHandle.Right ? units.X : units.Y);

    /// <summary>The drag at (dx, dy) viewport px from where it started. Same units as last time: the same step (no re-layout).</summary>
    public DragStep Update(float dxPx, float dyPx, bool free)
    {
        var units = UnitsFor(dxPx, dyPx, free);
        if (_last != null && _last.Units == units) return _last;
        var edit = EditFor(StartEdits, units);
        if (!edit.Allowed || StartBox == null)
            return _last = new DragStep(units, StartBox == null ? EditResult.Refused($"{Node.Name} isn't laid out in this preview.") : edit, Before, []);
        var after = _engine.Arrange(_tree, _width, _height, StartEdits.With(edit.Changes, _ => null));
        return _last = new DragStep(units, edit, after, EditWarnings.Between(Before, after, Node));
    }
}
