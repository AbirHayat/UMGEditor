using UmgEditor.Core;
using UmgEditor.Core.Editing;
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
