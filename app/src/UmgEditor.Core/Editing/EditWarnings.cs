using System.Globalization;
using UmgEditor.Core.Layout;

namespace UmgEditor.Core.Editing;

public enum WarningKind
{
    /// <summary>Which boards show this package.</summary>
    Boards,
    /// <summary>A margin track drives this slot's Offsets at runtime.</summary>
    SlotMarginAnimation,
    /// <summary>A 2D-transform track moves the widget on top of its slot position.</summary>
    TransformAnimation,
    /// <summary>Visibility / opacity / colour tracks: the widget may be hidden or faded.</summary>
    OtherAnimation,
    /// <summary>An ancestor is moved by an animation, so the whole branch moves with it.</summary>
    AncestorAnimation,
    /// <summary>An ancestor clips to its bounds.</summary>
    ClippingParent,
    /// <summary>The pending edit puts the widget (partly) outside a clipping ancestor.</summary>
    LeavesClip,
    /// <summary>The edit changes a ScaleBox scale: the whole board rescales and moves.</summary>
    Rescale,
    /// <summary>The edit moves widgets outside the edited branch (layout feedback).</summary>
    OthersMove,
}

public enum Severity { Info, Warning, Danger }

public sealed record EditWarning(WarningKind Kind, Severity Severity, string Message);

/// <summary>Everything to show before editing a widget, plus the text of the first-edit confirmation.</summary>
public sealed class PreEditReport
{
    public required WidgetNode Node { get; init; }
    public required IReadOnlyList<Scoreboard> Boards { get; init; }
    public required int DirectUsers { get; init; }
    public required IReadOnlyList<EditWarning> Warnings { get; init; }

    public string ConfirmationText
    {
        get
        {
            var pkg = GamePaths.ShortName(Node.PackagePath);
            var boards = Boards.Count == 0 ? "no scoreboard" : string.Join(", ", Boards.Select(b => b.Label));
            var lines = new List<string> { $"You are about to edit {pkg}. It is shown on: {boards} (used directly by {DirectUsers} package{(DirectUsers == 1 ? "" : "s")})." };
            var rest = Warnings.Where(w => w.Kind != WarningKind.Boards).ToList();
            lines.Add(rest.Count == 0 ? "No warnings." : "Warnings:");
            lines.AddRange(rest.Select(w => $"  [{w.Severity}] {w.Message}"));
            lines.Add("Edits go to a copy in work\\edits; the game files are never changed.");
            return string.Join(Environment.NewLine, lines);
        }
    }
}

/// <summary>Asks once per package before the first edit; the confirmation names the boards and every warning.</summary>
public sealed class FirstEditGate
{
    private readonly HashSet<string> _confirmed = new(StringComparer.OrdinalIgnoreCase);
    public bool NeedsConfirmation(string package) => !_confirmed.Contains(package);
    public void Confirm(string package) => _confirmed.Add(package);
}

/// <summary>Pre-edit warnings for a selected widget and for a pending edit.</summary>
public sealed class EditWarnings(LayoutEngine engine, UsageGraph usage, int viewportWidth, int viewportHeight)
{
    private const float MovedPx = 0.01f;

    // ---- on selection ----

    public PreEditReport ForSelection(WidgetPackageTree tree, WidgetNode node)
    {
        var warnings = new List<EditWarning>();
        var boards = usage.ScoreboardsUsing(node.PackagePath);
        var direct = usage.DirectUsers(node.PackagePath).Count;
        warnings.Add(new(WarningKind.Boards, Severity.Info,
            $"Shown on: {(boards.Count == 0 ? "no scoreboard" : string.Join(", ", boards.Select(b => b.Label)))}; used directly by {direct} package{(direct == 1 ? "" : "s")}."));

        // one line per kind, naming the animations, so the confirmation stays readable
        string Names(IEnumerable<AnimationUse> uses) => string.Join(", ", uses.Select(a => $"\"{a.Animation}\"").Distinct());
        var margin = node.Animations.Where(a => a.OnSlot && a.Tracks.Any(t => t.StartsWith("Margin"))).ToList();
        if (margin.Count > 0)
            warnings.Add(new(WarningKind.SlotMarginAnimation, Severity.Danger,
                $"Animation{(margin.Count > 1 ? "s" : "")} {Names(margin)} drive{(margin.Count > 1 ? "" : "s")} this slot's Offsets at runtime; your value may only show when they aren't overriding it."));
        var transform = node.Animations.Where(a => a.Tracks.Any(t => t.StartsWith("2DTransform"))).ToList();
        if (transform.Count > 0)
            warnings.Add(new(WarningKind.TransformAnimation, Severity.Warning,
                $"Animation{(transform.Count > 1 ? "s" : "")} {Names(transform)} move{(transform.Count > 1 ? "" : "s")} this widget with a render transform, on top of your position (additive)."));
        foreach (var group in node.Animations
                     .Select(a => (Anim: a, Other: string.Join(", ", a.Tracks.Where(t => !t.StartsWith("Margin") && !t.StartsWith("2DTransform")).Distinct())))
                     .Where(t => t.Other.Length > 0).GroupBy(t => t.Other))
            warnings.Add(new(WarningKind.OtherAnimation, Severity.Info,
                $"Animation{(group.Count() > 1 ? "s" : "")} {Names(group.Select(g => g.Anim))} change{(group.Count() > 1 ? "" : "s")} {group.Key}; the widget may be hidden or faded at times."));

        var layout = engine.Arrange(tree, viewportWidth, viewportHeight);
        var box = layout.All().FirstOrDefault(b => ReferenceEquals(b.Node, node));
        for (var p = box?.Parent; p != null; p = p.Parent)
        {
            var moving = p.Node.Animations.Where(a => a.MovesWidget).Select(a => a.Animation).Distinct().ToList();
            if (moving.Count > 0)
                warnings.Add(new(WarningKind.AncestorAnimation, Severity.Info,
                    $"Ancestor {p.Node.Name} is moved by animation{(moving.Count > 1 ? "s" : "")} {string.Join(", ", moving.Select(m => $"\"{m}\""))}; this widget moves with it."));
            if (p.Node.ClipsToBounds)
                warnings.Add(new(WarningKind.ClippingParent, Severity.Warning,
                    $"Ancestor {p.Node.Name} clips to its bounds: anything moved outside it is cut off or vanishes."));
        }

        // would moving/resizing rescale the board? probe 1 unit per operation and look at ScaleBox scales
        if (SlotEditor.SlotRefusal(node) == null && box != null)
        {
            var probes = new (string What, EditResult Edit)[]
            {
                ("moving it horizontally", SlotEditor.Move(node, EditSet.Empty, 1, 0)),
                ("moving it vertically", SlotEditor.Move(node, EditSet.Empty, 0, 1)),
                ("changing its width", SlotEditor.ResizeEdge(node, EditSet.Empty, Edge.Right, 1)),
                ("changing its height", SlotEditor.ResizeEdge(node, EditSet.Empty, Edge.Bottom, 1)),
            };
            foreach (var (what, edit) in probes.Where(p => p.Edit.Allowed && p.Edit.Changes.Count > 0))
            {
                var after = engine.Arrange(tree, viewportWidth, viewportHeight, EditSet.Empty.With(edit.Changes, _ => null));
                if (ScaleChanges(layout, after).FirstOrDefault() is { } change)
                    warnings.Add(new(WarningKind.Rescale, Severity.Warning,
                        $"{Capitalize(what)} changes {change.Widget}'s scale (this widget sets its canvas's size): the whole board rescales and moves."));
            }
        }

        return new PreEditReport { Node = node, Boards = boards, DirectUsers = direct, Warnings = warnings };
    }

    // ---- for a pending edit ----

    /// <summary>Exact consequences of applying these edits: rescaling, other widgets moving, leaving a clip rect.</summary>
    public IReadOnlyList<EditWarning> ForPendingEdit(WidgetPackageTree tree, WidgetNode node, EditSet edits)
    {
        var warnings = new List<EditWarning>();
        var before = engine.Arrange(tree, viewportWidth, viewportHeight);
        var after = engine.Arrange(tree, viewportWidth, viewportHeight, edits);

        foreach (var c in ScaleChanges(before, after))
            warnings.Add(new(WarningKind.Rescale, Severity.Warning,
                $"{c.Widget} scale ×{c.Before.ToString("0.#####", CultureInfo.InvariantCulture)} → ×{c.After.ToString("0.#####", CultureInfo.InvariantCulture)}: the whole board rescales and moves, not just {node.Name}."));

        // the same embedded tree is shared by every instance of a child widget, so boxes are keyed by their tree path
        var editedBox = after.All().FirstOrDefault(b => ReferenceEquals(b.Node, node));
        var branchPrefix = editedBox == null ? null : LayoutExport.PathOf(editedBox);
        var beforeByPath = new Dictionary<string, LayoutBox>();
        foreach (var b in before.All()) beforeByPath.TryAdd(LayoutExport.PathOf(b), b);
        var moved = 0;
        foreach (var b in after.All())
        {
            var path = LayoutExport.PathOf(b);
            if (branchPrefix != null && (path == branchPrefix || path.StartsWith(branchPrefix + "/"))) continue;
            if (beforeByPath.TryGetValue(path, out var o) && Differs(o.Bounds, b.Bounds)) moved++;
        }
        if (moved > 0)
            warnings.Add(new(WarningKind.OthersMove, Severity.Warning, $"{moved} other widget{(moved == 1 ? "" : "s")} outside {node.Name} move or resize as a result."));

        // clipping: warn only when the edit hides more of the widget than stock already does
        if (editedBox?.Clip is { } clip)
        {
            var r = editedBox.Bounds;
            var inside = r.Intersect(clip);
            var stockBox = branchPrefix != null ? beforeByPath.GetValueOrDefault(branchPrefix) : null;
            var stockInside = stockBox == null ? r : stockBox.Clip is { } stockClip ? stockBox.Bounds.Intersect(stockClip) : stockBox.Bounds;
            var clipper = Ancestors(editedBox).FirstOrDefault(a => a.Node.ClipsToBounds)?.Node.Name ?? "a clipping ancestor";
            if (inside.Width <= 0 || inside.Height <= 0)
                warnings.Add(new(WarningKind.LeavesClip, Severity.Danger, $"{node.Name} ends up completely outside {clipper}: it will vanish in game."));
            else if (inside.Width < stockInside.Width - MovedPx || inside.Height < stockInside.Height - MovedPx)
                warnings.Add(new(WarningKind.LeavesClip, Severity.Warning,
                    $"{node.Name} ends up partly outside {clipper}: it will be cut off (visible {inside.Width:0.#}×{inside.Height:0.#} px of {r.Width:0.#}×{r.Height:0.#}, stock showed {stockInside.Width:0.#}×{stockInside.Height:0.#})."));
        }
        return warnings;
    }

    private sealed record ScaleChange(string Widget, double Before, double After);

    private static IEnumerable<ScaleChange> ScaleChanges(LayoutResult before, LayoutResult after) =>
        before.ScaleBoxes.Zip(after.ScaleBoxes)
            .Where(p => p.First.Widget == p.Second.Widget && Math.Abs(p.First.Applied - p.Second.Applied) > 1e-6)
            .Select(p => new ScaleChange(p.First.Widget, p.First.Applied, p.Second.Applied));

    private static IEnumerable<LayoutBox> Ancestors(LayoutBox box)
    {
        for (var p = box.Parent; p != null; p = p.Parent) yield return p;
    }

    private static bool Differs(RectangleF a, RectangleF b) =>
        Math.Abs(a.X - b.X) > MovedPx || Math.Abs(a.Y - b.Y) > MovedPx || Math.Abs(a.Width - b.Width) > MovedPx || Math.Abs(a.Height - b.Height) > MovedPx;

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
