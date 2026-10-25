using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Layout;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>Step 6e: the impact panel's data. Edit sets are built in memory; nothing is written.</summary>
[TestClass]
public class ImpactReportTests
{
    private const string Base = Tv + "MatchTime/MatchTimeBase";
    private const string MatchTime = Tv + "MatchTime/MatchTime";
    private const string Alert = "/Game/Assets/ui/Data/Widget/General/Alert/AlertEventRatingChange/AlertEventRatingChange";
    private const string Right = "LayoutData.Offsets.Right";

    private static EditSet Edit(string package, string widget, Func<WidgetNode, EditSet, EditResult> edit)
    {
        var node = WidgetTreeReader.Build(Source, package).Find(widget)!;
        var r = edit(node, EditSet.Empty);
        Assert.IsTrue(r.Allowed, r.Reason);
        return EditSet.Empty.With(r.Changes, _ => null);
    }

    private static ImpactReport Report(EditSet edits, int w = 1786, int h = 1000) =>
        ImpactReport.Build("test", w, h, new EditImpact(Engine, new UsageGraph(UiIndex), w, h).Predict(edits, p => WidgetTreeReader.Build(Source, p)));

    private static ImpactBox Box(ImpactPackage p, string pathSuffix) =>
        p.Boxes.SingleOrDefault(b => b.Path.EndsWith(pathSuffix)) ?? throw new AssertFailedException($"{p.Name}: no changed box …{pathSuffix}; changed: {string.Join(" | ", p.Boxes.Select(b => b.Path))}");

    private static EdgeChange EdgeOf(ImpactBox b, string edge) => b.Edges.Single(e => e.Edge == edge);

    /// <summary>Canary C6's MatchTimeBase edit: every package it reaches, with the plate edges measured in game (C3/C6).</summary>
    [TestMethod]
    public void SharedPlate_ReachesNinePackages_OnlineTagged_EdgesMatchTheGame()
    {
        var report = Report(Edit(Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 100)));
        Console.WriteLine(report.ToText());
        CollectionAssert.AreEqual(new[] { "MatchTimeBase", "MatchTime", "MatchTimeCard", "MatchTimeGoal", "MatchTimeMemberChange" },
            report.Offline.Select(p => p.Name).ToArray());
        Assert.AreEqual(4, report.Online.Count);
        Assert.IsTrue(report.Online.All(p => p.IsOnline && p.Name.StartsWith("MatchTimeConnection")));
        Assert.IsTrue(report.Packages.Single(p => p.IsEdited).Name == "MatchTimeBase");
        StringAssert.StartsWith(report.Summary, "9 packages reached (1 edited, 4 online), 9 with changed boxes");
        StringAssert.Contains(report.Packages.Single(p => p.Name == "MatchTimeGoal").Note, "via MatchTimeBase → MatchTimeGoal");

        // in game: scoreboard plate right 296 (C3/C6), goal banner 463 (C3/C6), card and substitution pop-ups 452 (C6)
        foreach (var (package, path, stock, edited) in new[]
                 {
                     ("MatchTime", "Normal_Display/Img_MatchTimeBg/CanvasPanel_0/Img_Plate", 388.9, 296.3),
                     ("MatchTimeGoal", "Goal/Plate_MatchTimeBg/CanvasPanel_0/Img_Plate", 555.6, 463.0),
                     ("MatchTimeCard", "Stats/Plate_MatchTimeBg/CanvasPanel_0/Img_Plate", 544.4, 451.9),
                     ("MatchTimeMemberChange", "Stats/Plate_MatchTimeBg/CanvasPanel_0/Img_Plate", 544.4, 451.9),
                 })
        {
            var box = Box(report.Packages.Single(p => p.Name == package), path);
            Assert.IsTrue(box.Edited, $"{package}: the plate itself is laid out with the edited value");
            var right = EdgeOf(box, "right");
            Assert.AreEqual(stock, right.BeforePx, 0.1, package);
            Assert.AreEqual(edited, right.AfterPx, 0.1, package);
            Assert.AreEqual(-100, right.DeltaUnits, 0.01, $"{package}: 100 design units");
            Assert.IsFalse(EdgeOf(box, "left").Changed || EdgeOf(box, "top").Changed || EdgeOf(box, "bottom").Changed, package);
        }
        // every package that embeds the plate shows it 100 units shorter, online ones included
        foreach (var p in report.Packages.Where(p => !p.IsEdited))
            Assert.IsTrue(p.Boxes.Any(b => b.Edited && EdgeOf(b, "right") is { Changed: true } r && Math.Abs(r.DeltaUnits + 100) < 0.01), p.Name);
        Assert.IsFalse(report.Packages.SelectMany(p => p.Warnings).Any(w => w.Kind is WarningKind.Rescale or WarningKind.LeavesClip),
            string.Join(" | ", report.Packages.SelectMany(p => p.Warnings).Select(w => w.Message)));

        var text = report.ToText();
        StringAssert.Contains(text, "MatchTimeConnection [online]");
        StringAssert.Contains(text, "right 388.9 → 296.3 px (−92.6 px = −100 u; 420 → 320 u)");
    }

    [TestMethod]
    public void ScoreBoxMove_OnlyMatchTime_ChildrenMoveWithIt_NoOtherWidgetMoves()
    {
        var report = Report(Edit(MatchTime, "Img_MatchTimeBg_1", (n, e) => SlotEditor.Move(n, e, 20, 0)));
        var board = report.Packages.Single();
        Assert.IsTrue(board.IsEdited && !report.HasOnline, "no other package embeds MatchTime");
        var box = Box(board, "Normal_Display/Img_MatchTimeBg_1");
        Assert.AreEqual(161.1, EdgeOf(box, "left").BeforePx, 0.2, "canary C2 measured 161 at stock");
        Assert.AreEqual(20, EdgeOf(box, "left").DeltaUnits, 0.01);
        Assert.AreEqual(179.6, EdgeOf(box, "left").AfterPx, 0.2, "canary C1 measured 180");
        Assert.AreEqual("edited", box.Kind);
        // its own children move with it (listed as "moved"), but nothing outside it does: no warning
        CollectionAssert.AreEquivalent(new[] { "Img_MatchTimeBg_1/CanvasPanel_0", "Img_MatchTimeBg_1/CanvasPanel_0/Img_Plate" },
            board.Boxes.Where(b => b.Kind == "moved").Select(b => b.Path[(b.Path.IndexOf("Img_MatchTimeBg_1/"))..]).ToArray());
        Assert.AreEqual(0, board.Warnings.Count, string.Join(" | ", board.Warnings.Select(w => w.Message)));
        Assert.AreEqual("3 boxes change", board.ChangeText);
    }

    /// <summary>C6 = both edits: in MatchTime both the score box and the shared plate are edited, so neither counts as "other".</summary>
    [TestMethod]
    public void CanaryC6_BothEdits_TwoEditedBranchesInTheBoard()
    {
        var plate = Edit(Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 100));
        var box = Edit(MatchTime, "Img_MatchTimeBg_1", (n, e) => SlotEditor.SetField(n, e, "LayoutData.Offsets.Left", 146));
        var report = Report(plate.With(box.Values.Select(kv => new FieldChange(kv.Key, double.NaN, kv.Value)), _ => null));
        StringAssert.StartsWith(report.Summary, "9 packages reached (2 edited, 4 online)");
        var board = report.Packages.Single(p => p.Name == "MatchTime");
        Assert.AreEqual(2, board.Boxes.Count(b => b.Edited));
        Assert.AreEqual(340.7 - 50, EdgeOf(Box(board, "Normal_Display/Img_MatchTimeBg_1"), "right").AfterPx, 0.1, "C6 prediction 340.7 in screenshot px");
        Assert.AreEqual(346.3 - 50, EdgeOf(Box(board, "Normal_Display/Img_MatchTimeBg/CanvasPanel_0/Img_Plate"), "right").AfterPx, 0.1);
        Assert.IsFalse(board.Warnings.Any(), string.Join(" | ", board.Warnings.Select(w => w.Message)));
    }

    /// <summary>Canary 1 in game: Mask Left 129→829 made the score digits vanish; the impact panel warns for that package.</summary>
    [TestMethod]
    public void MaskOutsideItsClip_LeavesClipDanger()
    {
        var report = Report(Edit(MatchTime, "Mask", (n, e) => SlotEditor.SetField(n, e, "LayoutData.Offsets.Left", 829)));
        var w = report.Packages.Single().Warnings.Single(x => x.Kind == WarningKind.LeavesClip);
        Assert.AreEqual(Severity.Danger, w.Severity);
        StringAssert.Contains(w.Message, "Mask ends up completely outside");
    }

    // ---- item 3: which clip verdicts are Danger ----

    private static EditSet C8Timer() => Edit(MatchTime, "Time_Timer", (n, e) => SlotEditor.SetField(n, e, "LayoutData.Offsets.Left", 491));

    /// <summary>An edit of the first key of a widget's moving animation (margin or 2D transform) in MatchTime.</summary>
    private static EditSet WithAnimKeyOf(EditSet edits, string widget, string animation)
    {
        var use = WidgetTreeReader.Build(Source, MatchTime).Find(widget)!.Animations.First(a => a.Animation == animation && a.MovingSections is { Count: > 0 });
        return edits.With([new FieldChange(new FieldKey(MatchTime, use.MovingSections![0], "anim:Translation[0]#0"), 0, 5)], _ => null);
    }

    private static EditWarning TimerWarning(EditSet edits, IReadOnlySet<string>? clipRemoved = null) =>
        ImpactReport.Build("test", 1786, 1000, new EditImpact(Engine, new UsageGraph(UiIndex), 1786, 1000).Predict(edits, p => WidgetTreeReader.Build(Source, p), clipRemoved))
            .Packages.Single(p => p.Name == "MatchTime").Warnings.Single(w => w.Message.Contains("Time_Timer"));

    /// <summary>
    /// The C8 timer has its own transform tracks (Info_On holds it at -372) and the MatchTime canvas above it has Aspect_*
    /// margin tracks, yet it vanished in game: stock animations don't make a clip verdict uncertain. Only keys the edits
    /// change do, and only on the widget or an ancestor below its clip (another widget's edited keys don't count).
    /// </summary>
    [TestMethod]
    public void C8_StaysDanger_UnlessTheEditsChangeKeysThatMoveTheTimer()
    {
        Assert.AreEqual(Severity.Danger, TimerWarning(C8Timer()).Severity);
        Assert.AreEqual(Severity.Danger, TimerWarning(WithAnimKeyOf(C8Timer(), "Img_Logo", "Info_Show")).Severity, "Img_Logo's keys don't move the timer");

        var uncertain = TimerWarning(WithAnimKeyOf(C8Timer(), "Time_Timer", "Info_Show"));
        Assert.AreEqual((WarningKind.AnimatedClip, Severity.Warning), (uncertain.Kind, uncertain.Severity));
        StringAssert.StartsWith(uncertain.Message, "Uncertain: an animation moves Time_Timer");
        StringAssert.Contains(uncertain.Message, "\"Info_Show\" on it");
    }

    /// <summary>A recipe layer that removes Normal_Display's ClipToBounds (C15) leaves nothing to vanish behind.</summary>
    [TestMethod]
    public void C8_WithNormalDisplaysClippingRemoved_HasNoClipWarning()
    {
        var removed = new HashSet<string> { LayoutEngine.ClipKey(MatchTime, "Normal_Display") };
        var report = ImpactReport.Build("test", 1786, 1000, new EditImpact(Engine, new UsageGraph(UiIndex), 1786, 1000).Predict(C8Timer(), p => WidgetTreeReader.Build(Source, p), removed));
        var board = report.Packages.Single(p => p.Name == "MatchTime");
        Assert.IsFalse(board.Warnings.Any(w => w.Kind is WarningKind.LeavesClip or WarningKind.AnimatedClip), string.Join(" | ", board.Warnings.Select(w => w.Message)));
        Assert.IsNull(Box(board, "Normal_Display/Time_Timer").Cut, "laid out without the removed clip");
    }

    /// <summary>A widget the edit collapses to 0 px or parks off-screen won't show either: said as such, a Warning, not Danger.</summary>
    [TestMethod]
    public void Mask_Collapsed_OrParkedOffScreen_IsAWarningThatSaysSo_829StaysDanger()
    {
        var w829 = Report(Edit(MatchTime, "Mask", (n, e) => SlotEditor.SetField(n, e, "LayoutData.Offsets.Left", 829))).Packages.Single().Warnings
            .Single(w => w.Message.Contains("Mask"));
        Assert.AreEqual((WarningKind.LeavesClip, Severity.Danger), (w829.Kind, w829.Severity), "canary 1: on screen, outside its clip, vanished in game");

        var parked = Report(Edit(MatchTime, "Mask", (n, e) => SlotEditor.SetField(n, e, "LayoutData.Offsets.Left", 3000))).Packages.Single().Warnings
            .Single(w => w.Message.Contains("Mask"));
        Assert.AreEqual((WarningKind.OffScreen, Severity.Warning), (parked.Kind, parked.Severity), parked.Message);
        StringAssert.Contains(parked.Message, "moved completely off-screen");

        var collapsed = Report(Edit(MatchTime, "Mask", (n, e) => SlotEditor.SetField(n, e, Right, 0))).Packages.Single().Warnings
            .Single(w => w.Message.Contains("Mask"));
        Assert.AreEqual((WarningKind.Collapsed, Severity.Warning), (collapsed.Kind, collapsed.Severity), collapsed.Message);
        StringAssert.Contains(collapsed.Message, "collapsed to 0 px wide");
    }

    /// <summary>
    /// Canary C8 in game: Time_Timer Left 368→491 left 0.9 px of it inside Normal_Display (clips at x 500) and the timer
    /// was gone. Danger, and the timer's boxes are marked cut off; the away name, cut 1.9 px at the bottom in stock as
    /// well, isn't marked.
    /// </summary>
    [TestMethod]
    public void C8_TimerPushedOutOfNormalDisplay_DangerAndCutOffBoxes()
    {
        var edits = Edit(MatchTime, "Time_Timer", (n, e) => SlotEditor.SetField(n, e, "LayoutData.Offsets.Left", 491))
            .With(Edit(MatchTime, "Text_TeamName_Away", (n, e) => SlotEditor.SetField(n, e, "LayoutData.Offsets.Left", 371)).Values
                .Select(kv => new FieldChange(kv.Key, double.NaN, kv.Value)), _ => null);
        var board = Report(edits).Packages.Single(p => p.Name == "MatchTime");
        Console.WriteLine(board.ToText());

        var w = board.Warnings.Single(x => x.Kind == WarningKind.LeavesClip);
        Assert.AreEqual(Severity.Danger, w.Severity);
        StringAssert.Contains(w.Message, "Time_Timer ends up almost completely outside Normal_Display");
        StringAssert.Contains(w.Message, "practically vanish");

        var timer = Box(board, "Normal_Display/Time_Timer");
        Assert.IsTrue(timer.CutOff);
        Assert.AreEqual("cut off by Normal_Display: 0.9×57.4 of 118.5×59.3 px visible, practically gone", timer.CutText);
        Assert.IsTrue(timer.Cut!.PracticallyGone);
        Assert.IsFalse(timer.Cut.EdgeShows("left"), "0.9 px left: the left edge isn't a timer edge on screen");
        StringAssert.Contains(timer.ToText(), "[cut off by Normal_Display");
        Assert.AreEqual("outside Normal_Display: not visible", Box(board, "Timer/Text_Colon").CutText);

        var away = Box(board, "Normal_Display/Text_TeamName_Away");
        Assert.IsNotNull(away.Cut, "its bottom is clipped, in stock too");
        Assert.IsFalse(away.CutOff, "cut no more than in stock: not marked");
        Assert.IsNull(away.CutText);
        Assert.IsTrue(away.Cut!.EdgeShows("left") && away.Cut.EdgeShows("right") && !away.Cut.EdgeShows("bottom"));
    }

    [TestMethod]
    public void ClipCut_EdgesTextAndStockComparison()
    {
        var box = new RectangleF(100, 20, 50, 40);
        // right half cut, stock uncut
        var half = new ClipCut("Clip", box, new RectangleF(100, 20, 25, 40), box, box);
        Assert.IsTrue(half.CutsMoreThanStock);
        Assert.IsFalse(half.PracticallyGone);
        Assert.IsTrue(half.EdgeShows("left") && half.EdgeShows("top") && half.EdgeShows("bottom"));
        Assert.IsFalse(half.EdgeShows("right"));
        Assert.AreEqual("cut off by Clip: 25×40 of 50×40 px visible", half.Text);
        // the same cut as stock: not more
        Assert.IsFalse((half with { StockVisible = new RectangleF(100, 20, 25, 40) }).CutsMoreThanStock);
        // less than VanishPx left: practically gone, no edge counts
        var sliver = half with { Visible = new RectangleF(100, 20, 2.5f, 40) };
        Assert.IsTrue(sliver.PracticallyGone);
        Assert.IsFalse(sliver.EdgeShows("left"));
        StringAssert.EndsWith(sliver.Text, "practically gone");
        Assert.AreEqual("outside Clip: not visible", (half with { Visible = new RectangleF(150, 20, 0, 40) }).Text);
    }

    [TestMethod]
    public void SizeDrivingChild_RescaleWarning_AndTheWholeBoardMoves()
    {
        var report = Report(Edit(Alert, "Window", (n, e) => SlotEditor.ResizeEdge(n, e, Edge.Right, 12)), 1920, 1080);
        var alert = report.Packages.First(p => p.IsEdited);
        StringAssert.Contains(alert.Warnings.Single(w => w.Kind == WarningKind.Rescale).Message, "ScaleBox_37 scale ×1 → ×0.99379");
        StringAssert.Contains(alert.Warnings.Single(w => w.Kind == WarningKind.OthersMove).Message, "outside Window move");
        Assert.IsTrue(alert.Boxes.Count(b => !b.Edited) > 10, "rescaling moves the whole board");
    }

    [TestMethod]
    public void Controller_ImpactOf_EmptyAndEdited()
    {
        var c = new EditorController(Source, new UsageGraph(UiIndex), Engine, p => WidgetTreeReader.Build(Source, p), project: "zztest" + Guid.NewGuid().ToString("N")[..10]);
        var empty = c.ImpactOf(c.Edits);
        Assert.IsTrue(empty.IsEmpty);
        StringAssert.StartsWith(empty.Summary, "No edits in project");
        var report = c.ImpactOf(Edit(Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 100)));
        Assert.AreEqual((1786, 1000, 9), (report.ViewportWidth, report.ViewportHeight, report.Packages.Count));
        Assert.IsFalse(Directory.Exists(c.ProjectFolder), "the impact panel only lays out; it writes nothing");
    }

    [TestMethod]
    public void EdgeChange_TextInPxAndUnits()
    {
        var e = new EdgeChange("right", 388.8889, 296.2963, 0.925926, 0.925926);
        Assert.AreEqual("right 388.9 → 296.3 px (−92.6 px = −100 u; 420 → 320 u)", e.Text);
        Assert.AreEqual("296.3 px = 320 u", e.AfterText);
        Assert.AreEqual("−92.6 px = −100 u", e.DeltaText);
        var same = new EdgeChange("top", 25, 25.004, 0.925926, 0.925926);
        Assert.IsFalse(same.Changed);
        Assert.AreEqual("top 25 px = 27 u (unchanged)", same.Text);
        Assert.AreEqual("+18.5", EdgeChange.S(18.52).Replace("−", "-"));
    }
}
