using UmgEditor.Core.Layout;

namespace UmgEditor.Core.Editing;

/// <summary>A package whose layout an edit can change: an edited package itself, or one that embeds it (directly or through others).</summary>
/// <param name="Via">The chain from the edited package, e.g. "MatchTimeBase → MatchTimeGoal"; just the name for an edited package</param>
public sealed record AffectedPackage(string PackagePath, string Via, bool Edited, Scoreboard? Board)
{
    public string Name => GamePaths.ShortName(PackagePath);
    public bool IsOnline => PackagePath.Contains("/MatchTimeConnection/", StringComparison.OrdinalIgnoreCase);
    public string Label => Board?.Label ?? Name;
    /// <summary>Reached through another package (the chain has more than one step).</summary>
    public bool IsIndirect => Via.Count(c => c == '→') > 1;
    /// <summary>Short note for lists: the board it is, or the chain when indirect.</summary>
    public string Note => Board?.Label ?? (IsIndirect ? "via " + Via : "");
}

/// <summary>One widget box that differs between the stock and the edited layout (game-viewport px).</summary>
public sealed record BoxChange(string Path, string OwnerPackage, bool Visible, RectangleF Before, RectangleF After)
{
    public float DLeft => After.X - Before.X;
    public float DRight => After.Right - Before.Right;
    public float DTop => After.Y - Before.Y;
    public float DBottom => After.Bottom - Before.Bottom;
}

/// <param name="Edits">The edits the edited layout was arranged with (the clip warnings look at their animation keys)</param>
public sealed record PackageImpact(AffectedPackage Package, LayoutResult Stock, LayoutResult Edited, IReadOnlyList<BoxChange> Changes, string? Error = null,
    EditSet? Edits = null);

/// <summary>
/// Where an EditSet shows up: every edited package plus every package that embeds one of them (UsageGraph, transitively),
/// each arranged stock vs edited at the given viewport. Edits to a child package apply to all its embedded instances,
/// so a shared child (e.g. MatchTimeBase) changes the goal/card/substitution pop-ups and the online board too.
/// </summary>
public sealed class EditImpact(LayoutEngine engine, UsageGraph usage, int viewportWidth, int viewportHeight)
{
    private const float MovedPx = 0.01f;

    /// <summary>Edited packages first, then their users breadth-first (nearest first), each once.</summary>
    public static IReadOnlyList<AffectedPackage> AffectedPackages(UsageGraph usage, IEnumerable<string> editedPackages)
    {
        var result = new List<AffectedPackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Package, string Via)>();
        foreach (var p in editedPackages)
            if (seen.Add(p))
            {
                result.Add(new AffectedPackage(p, GamePaths.ShortName(p), true, BoardOf(p)));
                queue.Enqueue((p, GamePaths.ShortName(p)));
            }
        while (queue.Count > 0)
        {
            var (package, via) = queue.Dequeue();
            foreach (var user in usage.DirectUsers(package).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!seen.Add(user)) continue;
                var chain = via + " → " + GamePaths.ShortName(user);
                result.Add(new AffectedPackage(user, chain, false, BoardOf(user)));
                queue.Enqueue((user, chain));
            }
        }
        return result;
    }

    private static Scoreboard? BoardOf(string package) =>
        UsageGraph.Scoreboards.FirstOrDefault(b => b.PackagePath.Equals(package, StringComparison.OrdinalIgnoreCase));

    /// <summary>Arranges every affected package stock vs edited and lists the boxes that change.</summary>
    /// <param name="clipRemoved">Widgets whose Clipping a recipe layer removes (<see cref="LayoutEngine.ClipKey"/>): not clipping in the edited layout</param>
    public IReadOnlyList<PackageImpact> Predict(EditSet edits, Func<string, WidgetPackageTree> load, IReadOnlySet<string>? clipRemoved = null)
    {
        var edited = edits.Values.Select(kv => kv.Key.Package).Distinct(StringComparer.OrdinalIgnoreCase);
        var result = new List<PackageImpact>();
        foreach (var affected in AffectedPackages(usage, edited))
        {
            WidgetPackageTree tree;
            try { tree = load(affected.PackagePath); }
            catch (Exception e)
            {
                result.Add(new PackageImpact(affected, null!, null!, [], $"{e.GetType().Name}: {e.Message}"));
                continue;
            }
            var before = engine.Arrange(tree, viewportWidth, viewportHeight);
            var after = engine.Arrange(tree, viewportWidth, viewportHeight, edits, clipRemoved);
            result.Add(new PackageImpact(affected, before, after, Diff(before, after), Edits: edits));
        }
        return result;
    }

    /// <summary>Boxes whose rect differs, matched by tree path (embedded trees are shared between instances).</summary>
    public static IReadOnlyList<BoxChange> Diff(LayoutResult before, LayoutResult after)
    {
        var byPath = new Dictionary<string, LayoutBox>();
        foreach (var b in before.All()) byPath.TryAdd(LayoutExport.PathOf(b), b);
        var changes = new List<BoxChange>();
        foreach (var a in after.All())
        {
            var path = LayoutExport.PathOf(a);
            if (byPath.TryGetValue(path, out var b) && Differs(b.Bounds, a.Bounds))
                changes.Add(new BoxChange(path, GamePaths.ShortName(a.Node.PackagePath), a.Visible, b.Bounds, a.Bounds));
        }
        return changes;
    }

    private static bool Differs(RectangleF a, RectangleF b) =>
        Math.Abs(a.X - b.X) > MovedPx || Math.Abs(a.Y - b.Y) > MovedPx || Math.Abs(a.Width - b.Width) > MovedPx || Math.Abs(a.Height - b.Height) > MovedPx;
}
