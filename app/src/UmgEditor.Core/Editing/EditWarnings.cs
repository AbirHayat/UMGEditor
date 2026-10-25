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
    /// <summary>The edits collapse the widget to zero width or height: it won't show (usually on purpose).</summary>
    Collapsed,
    /// <summary>The edits move the widget completely off-screen: it won't show (usually on purpose).</summary>
    OffScreen,
    /// <summary>Outside its clip as laid out, but the edits change keys of an animation that moves it relative to the clip: uncertain.</summary>
    AnimatedClip,
    /// <summary>The edit changes a ScaleBox scale: the whole board rescales and moves.</summary>
    Rescale,
    /// <summary>The edit moves widgets outside the edited branch (layout feedback).</summary>
    OthersMove,
    /// <summary>The edit gives a value that isn't stored: saving adds a new tag and the package grows (branch experiment).</summary>
    SizeChange,
    /// <summary>A texture edit: other widgets or packages that show (or keep) the texture (step 3).</summary>
    SharedTexture,
}

public enum Severity { Info, Warning, Danger }

public sealed record EditWarning(WarningKind Kind, Severity Severity, string Message);

/// <summary>Everything to show before editing a widget, plus the text of the first-edit confirmation.</summary>
public sealed class PreEditReport
{
    public required WidgetNode Node { get; init; }
    public required IReadOnlyList<Scoreboard> Boards { get; init; }
    /// <summary>Every other package that embeds this one (directly or through others) and changes with it.</summary>
    public required IReadOnlyList<AffectedPackage> EmbeddedBy { get; init; }
    public int DirectUsers => EmbeddedBy.Count(a => a.Via.Count(c => c == '→') == 1);
    public required IReadOnlyList<EditWarning> Warnings { get; init; }

    public string ConfirmationText
    {
        get
        {
            var pkg = GamePaths.ShortName(Node.PackagePath);
            var boards = Boards.Count == 0 ? "no scoreboard" : string.Join(", ", Boards.Select(b => b.Label));
            var lines = new List<string> { $"You are about to edit {pkg}. It is shown on: {boards}.", EditWarnings.EmbeddedByText(pkg, EmbeddedBy) };
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
    /// <summary>A box with less than this much left inside its clip (px, either axis) is as good as gone: the validator's tolerance.</summary>
    public const float VanishPx = 3f;

    // ---- on selection ----

    public PreEditReport ForSelection(WidgetPackageTree tree, WidgetNode node)
    {
        var warnings = new List<EditWarning>();
        var boards = usage.ScoreboardsUsing(node.PackagePath);
        var embeddedBy = EditImpact.AffectedPackages(usage, [node.PackagePath]).Where(a => !a.Edited).ToList();
        warnings.Add(new(WarningKind.Boards, embeddedBy.Count > 0 ? Severity.Warning : Severity.Info,
            $"Shown on: {(boards.Count == 0 ? "no scoreboard" : string.Join(", ", boards.Select(b => b.Label)))}. {EmbeddedByText(GamePaths.ShortName(node.PackagePath), embeddedBy)}"));

        // one line per kind, naming the animations, so the confirmation stays readable
        string Names(IEnumerable<AnimationUse> uses) => string.Join(", ", uses.Select(a => $"\"{a.Animation}\"").Distinct());
        var margin = node.Animations.Where(a => a.OnSlot && a.Tracks.Any(t => t.StartsWith("Margin"))).ToList();
        if (margin.Count > 0)
            warnings.Add(new(WarningKind.SlotMarginAnimation, Severity.Danger,
                $"Animation{(margin.Count > 1 ? "s" : "")} {Names(margin)} drive{(margin.Count > 1 ? "" : "s")} this slot's Offsets at runtime. " +
                "Your stored value may not show while they play, e.g. during the kick-off slide-in; in game (canaries C1/C2) the stored value showed once the slide-in had finished."));
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

        return new PreEditReport { Node = node, Boards = boards, EmbeddedBy = embeddedBy, Warnings = warnings };
    }

    // ---- for a pending edit ----

    /// <summary>Exact consequences of applying these edits: rescaling, other widgets moving, leaving a clip rect.</summary>
    /// <param name="edits">All edits with the pending one applied</param>
    /// <param name="previous">The edits just before it (null = stock), so earlier edits don't count as its consequences</param>
    public IReadOnlyList<EditWarning> ForPendingEdit(WidgetPackageTree tree, WidgetNode node, EditSet edits, EditSet? previous = null)
    {
        var before = engine.Arrange(tree, viewportWidth, viewportHeight, previous);
        var after = engine.Arrange(tree, viewportWidth, viewportHeight, edits);
        return Between(before, after, node);
    }

    /// <summary>The same checks between two layouts already arranged (a drag lays out once per step).</summary>
    public static IReadOnlyList<EditWarning> Between(LayoutResult before, LayoutResult after, WidgetNode node)
    {
        var editedBox = after.All().FirstOrDefault(b => ReferenceEquals(b.Node, node));
        return Consequences(before, after, editedBox == null ? [] : [editedBox], node.Name);
    }

    /// <summary>
    /// The same consequences for one package of the impact panel, arranged stock vs edited: the edited widgets are the
    /// boxes laid out with an edited value (every instance of an edited child inside this package).
    /// </summary>
    /// <param name="edits">The edits (null = unknown): a clip warning is uncertain when they change the keys of an
    /// animation that moves the widget relative to its clip</param>
    public static IReadOnlyList<EditWarning> ForImpact(LayoutResult before, LayoutResult after, EditSet? edits = null)
    {
        var edited = after.All().Where(b => b.Inputs.Any(i => i.Source == ValueSource.Edited)).ToList();
        var names = edited.Select(b => b.Node.Name).Distinct().ToList();
        var subject = names.Count == 0 ? "the edited widgets" : names.Count <= 3 ? string.Join(", ", names) : $"{string.Join(", ", names.Take(3))} and {names.Count - 3} more";
        return Consequences(before, after, edited, subject, edits);
    }

    /// <summary>
    /// Animations that move <paramref name="box"/> relative to its clip and whose keys the edits change: margin or 2D
    /// transform tracks on the widget or an ancestor below the clipping one (an animation on the clipper or above it moves
    /// the clip too). With such keys edited, the layout of stored values isn't where the widget rests at runtime (canary
    /// C16: Goal's Translation[1] keys hold the goal row 60 units lower than its slot), so a clip verdict is uncertain.
    /// Stock animations whose keys aren't edited don't count: canary C8's timer vanished in game although Info_On can
    /// translate it by -372.
    /// </summary>
    internal static List<string> EditedMovingAnimations(LayoutBox box, EditSet? edits, IReadOnlySet<string> clipRemoved)
    {
        var found = new List<string>();
        if (edits is not { Count: > 0 }) return found;
        for (var b = box; b != null && !ClipsIn(b, clipRemoved); b = b.Parent)
            foreach (var use in b.Node.Animations.Where(a => a.MovesWidget && a.MovingSections is { Count: > 0 }))
                if (edits.Values.Any(kv => kv.Key.Path.StartsWith("anim:", StringComparison.Ordinal) && use.MovingSections!.Contains(kv.Key.SlotExport)
                                           && kv.Key.Package.Equals(b.Node.PackagePath, StringComparison.OrdinalIgnoreCase)))
                {
                    var what = $"\"{use.Animation}\" on {(b == box ? "it" : b.Node.Name)}{(use.OnSlot ? " (its slot)" : "")}";
                    if (!found.Contains(what)) found.Add(what);
                }
        return found;
    }

    private static bool ClipsIn(LayoutBox box, IReadOnlySet<string> clipRemoved) =>
        box.Node.ClipsToBounds && !clipRemoved.Contains(LayoutEngine.ClipKey(box.Node.PackagePath, box.Node.Name));

    private static IReadOnlyList<EditWarning> Consequences(LayoutResult before, LayoutResult after, IReadOnlyList<LayoutBox> editedBoxes, string subject,
        EditSet? edits = null)
    {
        var warnings = new List<EditWarning>();
        foreach (var c in ScaleChanges(before, after))
            warnings.Add(new(WarningKind.Rescale, Severity.Warning,
                $"{c.Widget} scale ×{c.Before.ToString("0.#####", CultureInfo.InvariantCulture)} → ×{c.After.ToString("0.#####", CultureInfo.InvariantCulture)}: the whole board rescales and moves, not just {subject}."));

        // the same embedded tree is shared by every instance of a child widget, so boxes are keyed by their tree path
        var branches = editedBoxes.Select(LayoutExport.PathOf).ToList();
        var beforeByPath = new Dictionary<string, LayoutBox>();
        foreach (var b in before.All()) beforeByPath.TryAdd(LayoutExport.PathOf(b), b);
        var moved = 0;
        foreach (var b in after.All())
        {
            var path = LayoutExport.PathOf(b);
            if (branches.Any(branch => path == branch || path.StartsWith(branch + "/"))) continue;
            if (beforeByPath.TryGetValue(path, out var o) && Differs(o.Bounds, b.Bounds)) moved++;
        }
        if (moved > 0)
            warnings.Add(new(WarningKind.OthersMove, Severity.Warning, $"{moved} other widget{(moved == 1 ? "" : "s")} outside {subject} move or resize as a result."));

        // clipping: warn only when the edit cuts off more of the widget than stock already does (a widget that only
        // shrinks shows less but loses nothing to the clip)
        foreach (var (editedBox, branch) in editedBoxes.Zip(branches))
        {
            if (editedBox.Clip is not { } clip) continue;
            var name = editedBox.Node.Name;
            var r = editedBox.Bounds;
            var inside = r.Intersect(clip);
            var stockBox = beforeByPath.GetValueOrDefault(branch);
            var stockR = stockBox?.Bounds ?? r;
            var stockInside = stockBox?.Clip is { } stockClip ? stockR.Intersect(stockClip) : stockR;
            var clipper = ClipperName(editedBox, after.ClipRemoved);
            var stockShowed = $"stock showed {stockInside.Width:0.#}×{stockInside.Height:0.#} of {stockR.Width:0.#}×{stockR.Height:0.#}";
            EditWarning? w = null;
            if (inside.Width <= 0 || inside.Height <= 0)
                w = new(WarningKind.LeavesClip, Severity.Danger, $"{name} ends up completely outside {clipper}: it will vanish in game.");
            else if (r.Width - inside.Width > stockR.Width - stockInside.Width + MovedPx || r.Height - inside.Height > stockR.Height - stockInside.Height + MovedPx)
                w = inside.Width < VanishPx || inside.Height < VanishPx
                    // canary C8: Time_Timer kept 0.9 px inside Normal_Display and nothing of it showed in game
                    ? new(WarningKind.LeavesClip, Severity.Danger,
                        $"{name} ends up almost completely outside {clipper}: only {inside.Width:0.#}×{inside.Height:0.#} px of {r.Width:0.#}×{r.Height:0.#} stays visible, " +
                        $"so it will practically vanish in game ({stockShowed}).")
                    : new(WarningKind.LeavesClip, Severity.Warning,
                        $"{name} ends up partly outside {clipper}: it will be cut off (visible {inside.Width:0.#}×{inside.Height:0.#} px of {r.Width:0.#}×{r.Height:0.#}, {stockShowed}).");
            if (w is { Severity: Severity.Danger })
            {
                // a vanish the edits make on purpose, or one the layout can't decide, isn't a Danger: said as such
                var stockSize = stockBox == null || stockR.Width > MovedPx && stockR.Height > MovedPx;
                var viewport = new RectangleF(0, 0, after.ViewportWidth, after.ViewportHeight);
                var stockOnScreen = stockBox == null || stockR.IntersectsWith(viewport);
                var animated = EditedMovingAnimations(editedBox, edits, after.ClipRemoved);
                if ((r.Width <= MovedPx || r.Height <= MovedPx) && stockSize)
                    w = new(WarningKind.Collapsed, Severity.Warning,
                        $"{name} is collapsed to 0 px {(r.Width <= MovedPx ? "wide" : "high")} by the edits: it won't show (hidden on purpose?).");
                else if (!r.IntersectsWith(viewport) && stockOnScreen)
                    w = new(WarningKind.OffScreen, Severity.Warning,
                        $"{name} is moved completely off-screen by the edits (x {r.X:0.#}–{r.Right:0.#}, y {r.Y:0.#}–{r.Bottom:0.#} px; the screen is {after.ViewportWidth}×{after.ViewportHeight}): " +
                        "it won't show (parked there on purpose?).");
                else if (animated.Count > 0)
                    w = new(WarningKind.AnimatedClip, Severity.Warning,
                        $"Uncertain: an animation moves {name}. As laid out it ends up {(inside.Width <= 0 || inside.Height <= 0 ? "completely" : "almost completely")} outside {clipper}, " +
                        $"but {string.Join(", ", animated)} move{(animated.Count == 1 ? "s" : "")} it relative to that clip and the edits change {(animated.Count == 1 ? "its" : "their")} keys, " +
                        "so the stored layout isn't where it rests at runtime. Check it in game.");
            }
            if (w != null && !warnings.Contains(w)) warnings.Add(w);
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

    /// <summary>The nearest ancestor that clips this box (the one named in the clip warnings).</summary>
    internal static string ClipperName(LayoutBox box, IReadOnlySet<string>? clipRemoved = null) =>
        Ancestors(box).FirstOrDefault(a => ClipsIn(a, clipRemoved ?? new HashSet<string>()))?.Node.Name ?? "a clipping ancestor";

    private static bool Differs(RectangleF a, RectangleF b) =>
        Math.Abs(a.X - b.X) > MovedPx || Math.Abs(a.Y - b.Y) > MovedPx || Math.Abs(a.Width - b.Width) > MovedPx || Math.Abs(a.Height - b.Height) > MovedPx;

    /// <summary>
    /// "Embedded by 8 packages, which change with it: MatchTime (Offline scoreboard), MatchTimeGoal, …": names, not a count.
    /// Packages reached through another one carry their chain; online packages are marked.
    /// </summary>
    public static string EmbeddedByText(string package, IReadOnlyList<AffectedPackage> embeddedBy)
    {
        if (embeddedBy.Count == 0) return $"No other package embeds {package}; only its own layout changes.";
        static string Name(AffectedPackage a)
        {
            var notes = new List<string>();
            if (a.Board != null) notes.Add(a.Board.Label);
            else if (a.IsOnline) notes.Add("online");
            if (a.Via.Count(c => c == '→') > 1) notes.Add("via " + a.Via);
            return notes.Count == 0 ? a.Name : $"{a.Name} ({string.Join(", ", notes)})";
        }
        const int MaxNames = 12;
        var names = embeddedBy.Take(MaxNames).Select(Name).ToList();
        if (embeddedBy.Count > MaxNames) names.Add($"… and {embeddedBy.Count - MaxNames:N0} more");
        return $"Embedded by {embeddedBy.Count:N0} package{(embeddedBy.Count == 1 ? "" : "s")}, which change with it: {string.Join(", ", names)}.";
    }

    private static string Capitalize(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
