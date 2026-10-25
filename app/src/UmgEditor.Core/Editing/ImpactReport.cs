using System.Globalization;
using System.Text;
using UmgEditor.Core.Layout;

namespace UmgEditor.Core.Editing;

/// <summary>
/// One edge of a changed box, stock vs edited, in viewport px and in design units (px ÷ the box's px per unit in that
/// layout, measured from the viewport's top-left; for a ScaleToFit board this is the 1920×1080 design canvas).
/// </summary>
public sealed record EdgeChange(string Edge, double BeforePx, double AfterPx, double BeforePixelsPerUnit, double AfterPixelsPerUnit)
{
    private const double MovedPx = 0.01;

    public double DeltaPx => AfterPx - BeforePx;
    public double BeforeUnits => BeforePx / BeforePixelsPerUnit;
    public double AfterUnits => AfterPx / AfterPixelsPerUnit;
    public double DeltaUnits => AfterUnits - BeforeUnits;
    public bool Changed => Math.Abs(DeltaPx) > MovedPx;

    public string BeforeText => $"{F(BeforePx)} px = {F(BeforeUnits)} u";
    public string AfterText => Changed ? $"{F(AfterPx)} px = {F(AfterUnits)} u" : "unchanged";
    public string DeltaText => Changed ? $"{S(DeltaPx)} px = {S(DeltaUnits)} u" : "";
    /// <summary>"right 388.9 → 296.3 px (−92.6 px = −100 u)", or "top 25 px (unchanged)".</summary>
    public string Text => Changed
        ? $"{Edge} {F(BeforePx)} → {F(AfterPx)} px ({S(DeltaPx)} px = {S(DeltaUnits)} u; {F(BeforeUnits)} → {F(AfterUnits)} u)"
        : $"{Edge} {F(BeforePx)} px = {F(BeforeUnits)} u (unchanged)";

    internal static string F(double v) => Math.Round(v, 1).ToString("0.#", CultureInfo.InvariantCulture);
    internal static string S(double v) => (v >= 0 ? "+" : "−") + F(Math.Abs(v));
}

/// <summary>
/// How much of a box a clipping ancestor cuts off in the edited layout (game px): what is left visible, and how much
/// stock already lost to a clip (e.g. the board's texts lose their bottom 1.9 px in stock too).
/// </summary>
public sealed record ClipCut(string Clipper, RectangleF Box, RectangleF Visible, RectangleF StockBox, RectangleF StockVisible)
{
    private const float EdgePx = 0.5f;
    private const float MovedPx = 0.01f;

    /// <summary>The edit cuts more of it off than stock does (the same test as the LeavesClip warning).</summary>
    public bool CutsMoreThanStock =>
        Box.Width - Visible.Width > StockBox.Width - StockVisible.Width + MovedPx || Box.Height - Visible.Height > StockBox.Height - StockVisible.Height + MovedPx;

    public bool Gone => Visible.Width <= 0 || Visible.Height <= 0;
    /// <summary>Less than <see cref="EditWarnings.VanishPx"/> left in either axis: nothing of it shows (canary C8).</summary>
    public bool PracticallyGone => Gone || Visible.Width < EditWarnings.VanishPx || Visible.Height < EditWarnings.VanishPx;

    /// <summary>Whether this edge of the box is on screen where predicted (not cut away, and the box doesn't vanish).</summary>
    public bool EdgeShows(string edge) => !PracticallyGone && edge switch
    {
        "left" => Math.Abs(Visible.X - Box.X) < EdgePx,
        "right" => Math.Abs(Visible.Right - Box.Right) < EdgePx,
        "top" => Math.Abs(Visible.Y - Box.Y) < EdgePx,
        "bottom" => Math.Abs(Visible.Bottom - Box.Bottom) < EdgePx,
        _ => false,
    };

    /// <summary>"cut off by Normal_Display: 0.9×57.4 of 118.5×59.3 px visible, practically gone".</summary>
    public string Text => Gone ? $"outside {Clipper}: not visible"
        : $"cut off by {Clipper}: {EdgeChange.F(Visible.Width)}×{EdgeChange.F(Visible.Height)} of {EdgeChange.F(Box.Width)}×{EdgeChange.F(Box.Height)} px visible" +
          (PracticallyGone ? ", practically gone" : "");

    /// <summary>The cut of a laid-out box, or null when nothing clips it (beyond rounding).</summary>
    public static ClipCut? Of(LayoutBox box, LayoutBox? stock)
    {
        if (box.Clip is not { } clip) return null;
        var r = box.Bounds;
        var inside = r.Intersect(clip);
        if (r.Width - inside.Width <= MovedPx && r.Height - inside.Height <= MovedPx) return null;
        var stockR = stock?.Bounds ?? r;
        var stockInside = stock?.Clip is { } stockClip ? stockR.Intersect(stockClip) : stockR;
        return new ClipCut(EditWarnings.ClipperName(box), r, inside, stockR, stockInside);
    }
}

/// <summary>A widget box that differs between the stock and the edited layout of one package.</summary>
/// <param name="Cut">Set when a clipping ancestor cuts the edited box (partly or entirely), even as much as in stock</param>
/// <param name="Hidden">How the edits hide the box by layout ("collapsed to 0 px wide", "moved completely off-screen …")
/// when stock gave it a size on screen; null otherwise (see <see cref="HiddenReason"/>)</param>
public sealed record ImpactBox(BoxChange Change, IReadOnlyList<EdgeChange> Edges, bool Edited, ClipCut? Cut = null, string? Hidden = null)
{
    /// <summary>The edit makes a clip cut off more of it than in stock: marked in the panel, the instructions and umgmod.</summary>
    public bool CutOff => Cut?.CutsMoreThanStock == true;
    public string? CutText => CutOff ? Cut!.Text : null;

    public string Path => Change.Path;
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];
    /// <summary>The last three path segments ("…/CanvasPanel_0/Img_Plate"), enough to tell instances apart.</summary>
    public string ShortPath
    {
        get
        {
            var parts = Path.Split('/');
            return parts.Length <= 3 ? Path : "…/" + string.Join("/", parts[^3..]);
        }
    }
    public bool Visible => Change.Visible;
    public string Kind => Edited ? "edited" : "moved";
    public string SizeText => $"{EdgeChange.F(Change.Before.Width)}×{EdgeChange.F(Change.Before.Height)} → {EdgeChange.F(Change.After.Width)}×{EdgeChange.F(Change.After.Height)} px";

    public string ToText() =>
        $"    {Path}{(Edited ? "  [edited]" : "")}{(Visible ? "" : "  [hidden in the stored pose]")}{(CutOff ? $"  [{Cut!.Text}]" : "")}{(Hidden != null ? $"  [{Hidden}]" : "")}\n" +
        string.Join("", Edges.Select(e => $"      {e.Text}\n"));

    private const float MovedPx = 0.01f;

    /// <summary>
    /// The edits hide this box by layout, clip or no clip: collapsed to 0 px (stock had a size) or moved completely
    /// off-screen (stock was on screen). The same tests as the Collapsed / OffScreen warnings, which only look at
    /// clipped widgets. A collapsed panel still draws children that keep a size; those aren't hidden.
    /// </summary>
    public static string? HiddenReason(BoxChange change, int viewportWidth, int viewportHeight)
    {
        RectangleF b = change.Before, a = change.After;
        if (b.Width <= MovedPx || b.Height <= MovedPx) return null;
        if (a.Width <= MovedPx || a.Height <= MovedPx) return $"collapsed to 0 px {(a.Width <= MovedPx ? "wide" : "high")}";
        var viewport = new RectangleF(0, 0, viewportWidth, viewportHeight);
        return !a.IntersectsWith(viewport) && b.IntersectsWith(viewport)
            ? $"moved completely off-screen (x {EdgeChange.F(a.X)}–{EdgeChange.F(a.Right)}, y {EdgeChange.F(a.Y)}–{EdgeChange.F(a.Bottom)} px)"
            : null;
    }
}

/// <summary>
/// A widget the edits hide on purpose (collapsed or parked off-screen), with how many boxes inside it are hidden
/// with it. Listed in the build log and the test instructions so each can be confirmed absent on the device.
/// </summary>
public sealed record HiddenWidget(string Package, bool Online, ImpactBox Box, int Inside)
{
    public string Text => $"{Package}{(Online ? " (online)" : "")}: `{Box.ShortPath}` {Box.Hidden}" +
                          (Inside > 0 ? $" (and {Inside} widget{(Inside == 1 ? "" : "s")} inside it)" : "") +
                          (Box.Visible ? "" : "; hidden in the stored pose too");
}

/// <summary>One package the edits reach: its changed boxes and the warnings for it.</summary>
public sealed record ImpactPackage(AffectedPackage Package, IReadOnlyList<ImpactBox> Boxes, IReadOnlyList<EditWarning> Warnings, string? Error)
{
    public string PackagePath => Package.PackagePath;
    public string Name => Package.Name;
    public bool IsOnline => Package.IsOnline;
    public bool IsEdited => Package.Edited;
    /// <summary>The board it is, how the edit reaches it, or that it is edited itself.</summary>
    public string Note => Package.Edited ? "edited in this project" + (Package.Board != null ? $" · {Package.Board.Label}" : "")
        : Package.Board?.Label is { } board ? $"{board} · via {Package.Via}" : "via " + Package.Via;
    public int HiddenCount => Boxes.Count(b => !b.Visible);

    /// <summary>The boxes the edits hide (<see cref="ImpactBox.Hidden"/>), outermost: a hidden box inside another one is counted there.</summary>
    public IReadOnlyList<HiddenWidget> HiddenByEdits
    {
        get
        {
            var hidden = Boxes.Where(b => b.Hidden != null).ToList();
            static bool Inside(ImpactBox b, ImpactBox outer) => b.Path.StartsWith(outer.Path + "/", StringComparison.Ordinal);
            return hidden.Where(b => !hidden.Any(o => Inside(b, o)))
                .Select(b => new HiddenWidget(Name, IsOnline, b, hidden.Count(i => Inside(i, b)))).ToList();
        }
    }

    public string ChangeText => Error != null ? "couldn't be laid out: " + Error
        : Boxes.Count == 0 ? "no box changes"
        : $"{Boxes.Count} box{(Boxes.Count == 1 ? "" : "es")} change{(Boxes.Count == 1 ? "s" : "")}" + (HiddenCount > 0 ? $" ({HiddenCount} hidden in the stored pose)" : "");

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Name}{(IsOnline ? " [online]" : "")} · {Note} · {ChangeText}");
        foreach (var w in Warnings) sb.AppendLine($"  [{w.Severity}] {w.Message}");
        foreach (var b in Boxes) sb.Append(b.ToText());
        return sb.ToString();
    }
}

/// <summary>What the impact panel shows: every package the project's edits reach, stock vs edited at one viewport.</summary>
public sealed record ImpactReport(string Project, int ViewportWidth, int ViewportHeight, IReadOnlyList<ImpactPackage> Packages)
{
    public IReadOnlyList<ImpactPackage> Offline => Packages.Where(p => !p.IsOnline).ToList();
    public IReadOnlyList<ImpactPackage> Online => Packages.Where(p => p.IsOnline).ToList();
    public bool HasOnline => Online.Count > 0;
    public string OnlineHeader => $"Online ({Online.Count})";
    public int ChangedCount => Packages.Count(p => p.Boxes.Count > 0);
    public int WarningCount => Packages.Sum(p => p.Warnings.Count);
    public bool IsEmpty => Packages.Count == 0;
    /// <summary>Every widget the edits hide on purpose, offline packages first.</summary>
    public IReadOnlyList<HiddenWidget> HiddenByEdits => Packages.OrderBy(p => p.IsOnline).SelectMany(p => p.HiddenByEdits).ToList();

    public string Summary => IsEmpty
        ? $"No edits in project {Project}: nothing is affected."
        : $"{Packages.Count} package{(Packages.Count == 1 ? "" : "s")} reached ({Packages.Count(p => p.IsEdited)} edited, {Online.Count} online), " +
          $"{ChangedCount} with changed boxes, {WarningCount} warning{(WarningCount == 1 ? "" : "s")} · stock vs edited at {ViewportWidth}×{ViewportHeight}";

    public string ToText() => $"Impact: {Summary}\n\n" + string.Join("\n", Packages.Select(p => p.ToText()));

    /// <summary>The panel's data from EditImpact.Predict: per package its changed boxes (edges in px and units) and warnings.</summary>
    public static ImpactReport Build(string project, int viewportWidth, int viewportHeight, IEnumerable<PackageImpact> impacts)
    {
        var packages = impacts.Select(i =>
        {
            if (i.Error != null) return new ImpactPackage(i.Package, [], [], i.Error);
            var stock = ByPath(i.Stock);
            var edited = ByPath(i.Edited);
            var boxes = i.Changes.Select(c =>
            {
                double before = stock.TryGetValue(c.Path, out var s) ? s.PixelsPerUnit : 1, after = edited.TryGetValue(c.Path, out var e) ? e.PixelsPerUnit : before;
                EdgeChange Edge(string name, float b, float a) => new(name, b, a, before, after);
                var edges = new[]
                {
                    Edge("left", c.Before.X, c.After.X), Edge("right", c.Before.Right, c.After.Right),
                    Edge("top", c.Before.Y, c.After.Y), Edge("bottom", c.Before.Bottom, c.After.Bottom),
                };
                var hidden = ImpactBox.HiddenReason(c, viewportWidth, viewportHeight);
                // as with the clip warnings: only animations whose keys the edits change make the stored pose uncertain; a 0 px
                // widget only comes back through a margin track on its own slot (a translation moves it but keeps it 0 px)
                if (hidden != null && e != null && EditWarnings.EditedMovingAnimations(e, i.Edits, i.Edited.ClipRemoved, ownSlotOnly: hidden.StartsWith("collapsed")) is { Count: > 0 } animated)
                    hidden += $"; uncertain: {string.Join(", ", animated)} move{(animated.Count == 1 ? "s" : "")} it and the edits change {(animated.Count == 1 ? "its" : "their")} keys";
                return new ImpactBox(c, edges, e?.Inputs.Any(x => x.Source == ValueSource.Edited) == true, e == null ? null : ClipCut.Of(e, s), hidden);
            }).ToList();
            return new ImpactPackage(i.Package, boxes, EditWarnings.ForImpact(i.Stock, i.Edited, i.Edits), null);
        }).ToList();
        return new ImpactReport(project, viewportWidth, viewportHeight, packages);
    }

    private static Dictionary<string, LayoutBox> ByPath(LayoutResult layout)
    {
        var map = new Dictionary<string, LayoutBox>();
        foreach (var b in layout.All()) map.TryAdd(LayoutExport.PathOf(b), b);
        return map;
    }
}
