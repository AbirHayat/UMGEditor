using UmgEditor.Core;
using UmgEditor.Core.Editing;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>Step 3: each pre-edit warning type appears for a known example from the game data.</summary>
[TestClass]
public class EditWarningsTests
{
    private const int W = 1786, H = 1000;
    private const string Alert = "/Game/Assets/ui/Data/Widget/General/Alert/AlertEventRatingChange/AlertEventRatingChange";

    private static EditWarnings Warnings => new(Engine, new UsageGraph(UiIndex), W, H);
    private static WidgetPackageTree MatchTime => WidgetTreeReader.Build(Source, Tv + "MatchTime/MatchTime");

    private static EditWarning Single(IEnumerable<EditWarning> list, WarningKind kind, string? contains = null)
    {
        var hits = list.Where(w => w.Kind == kind && (contains == null || w.Message.Contains(contains))).ToList();
        Assert.IsTrue(hits.Count >= 1, $"no {kind} warning{(contains != null ? $" containing \"{contains}\"" : "")}; got: {string.Join(" | ", list.Select(w => $"{w.Kind}: {w.Message}"))}");
        return hits[0];
    }

    private static EditSet Edits(EditResult r)
    {
        Assert.IsTrue(r.Allowed, r.Reason);
        return EditSet.Empty.With(r.Changes, _ => null);
    }

    [TestMethod]
    public void Boards_SharedChild_ListsOfflineAndOnline()
    {
        var tree = WidgetTreeReader.Build(Source, Tv + "MatchTime/MatchTimeBase");
        var report = Warnings.ForSelection(tree, tree.Find("Img_Plate")!);
        CollectionAssert.AreEqual(new[] { "Offline scoreboard", "Online scoreboard" }, report.Boards.Select(b => b.Label).ToArray());
        StringAssert.Contains(Single(report.Warnings, WarningKind.Boards).Message, "Offline scoreboard, Online scoreboard");
    }

    [TestMethod]
    public void SlotMarginAnimation_MatchTimeCanvas_IsDanger()
    {
        var tree = MatchTime;
        var w = Single(Warnings.ForSelection(tree, tree.Find("MatchTime")!).Warnings, WarningKind.SlotMarginAnimation);
        Assert.AreEqual(Severity.Danger, w.Severity);
        foreach (var aspect in new[] { "Aspect_0403", "Aspect_1609", "Aspect_Wide" }) StringAssert.Contains(w.Message, aspect);
        StringAssert.Contains(w.Message, "this slot's Offsets at runtime");
    }

    [TestMethod]
    public void TransformAndVisibilityAnimations_ScoreText()
    {
        var tree = MatchTime;
        var report = Warnings.ForSelection(tree, tree.Find("Text_Score_Home")!);
        StringAssert.Contains(Single(report.Warnings, WarningKind.TransformAnimation).Message, "additive");
        StringAssert.Contains(Single(report.Warnings, WarningKind.OtherAnimation).Message, "Visibility");
    }

    [TestMethod]
    public void AncestorAnimation_ScoreBoxes_MoveWithNormal()
    {
        var tree = MatchTime;
        StringAssert.Contains(Single(Warnings.ForSelection(tree, tree.Find("Img_MatchTimeBg_1")!).Warnings, WarningKind.AncestorAnimation, "Normal ").Message, "moves with it");
    }

    [TestMethod]
    public void ClippingParent_ScoreTextInsideMask()
    {
        var tree = MatchTime;
        var report = Warnings.ForSelection(tree, tree.Find("Text_Score_Home")!);
        Single(report.Warnings, WarningKind.ClippingParent, "Mask clips");
        Single(report.Warnings, WarningKind.ClippingParent, "Normal_Display clips");
    }

    /// <summary>Canary 1 in game: Mask Left 129→829 made the score digits vanish. The warning must predict that.</summary>
    [TestMethod]
    public void LeavesClip_Canary1_PredictsVanish()
    {
        var tree = MatchTime; var mask = tree.Find("Mask")!;
        var w = Single(Warnings.ForPendingEdit(tree, mask, Edits(SlotEditor.SetField(mask, EditSet.Empty, "LayoutData.Offsets.Left", 829))), WarningKind.LeavesClip);
        Assert.AreEqual(Severity.Danger, w.Severity);
        StringAssert.Contains(w.Message, "will vanish");
    }

    /// <summary>
    /// Canary 2 in game: Left 129→249 kept the digits visible. Mask's bottom 2 units are already clipped in stock
    /// (64 tall at Top 7 in a 69-tall parent); the edit doesn't hide more, so no warning.
    /// </summary>
    [TestMethod]
    public void LeavesClip_Canary2_NoWarning()
    {
        var tree = MatchTime; var mask = tree.Find("Mask")!;
        var pending = Warnings.ForPendingEdit(tree, mask, Edits(SlotEditor.SetField(mask, EditSet.Empty, "LayoutData.Offsets.Left", 249)));
        Assert.IsFalse(pending.Any(w => w.Kind == WarningKind.LeavesClip), string.Join(" | ", pending.Select(w => w.Message)));
    }

    /// <summary>Narrowing Mask shows less of it but cuts nothing more off (its bottom 2 units are clipped in stock already).</summary>
    [TestMethod]
    public void LeavesClip_Shrinking_NoWarning()
    {
        var tree = MatchTime; var mask = tree.Find("Mask")!;
        var pending = Warnings.ForPendingEdit(tree, mask, Edits(SlotEditor.ResizeEdge(mask, EditSet.Empty, Edge.Right, -50)));
        Assert.IsFalse(pending.Any(w => w.Kind == WarningKind.LeavesClip), string.Join(" | ", pending.Select(w => w.Message)));
    }

    [TestMethod]
    public void LeavesClip_PartlyOutside_IsCutOff()
    {
        var tree = MatchTime; var mask = tree.Find("Mask")!; // 114 wide inside the 492-wide Normal_Display
        var w = Single(Warnings.ForPendingEdit(tree, mask, Edits(SlotEditor.SetField(mask, EditSet.Empty, "LayoutData.Offsets.Left", 429))), WarningKind.LeavesClip);
        Assert.AreEqual(Severity.Warning, w.Severity);
        StringAssert.Contains(w.Message, "cut off");
    }

    /// <summary>Canary C8 in game: Time_Timer Left 368→491 kept 0.9 px inside Normal_Display and was gone. Under 3 px left = Danger.</summary>
    [TestMethod]
    public void LeavesClip_C8_SliverLeft_IsDanger()
    {
        var tree = MatchTime; var timer = tree.Find("Time_Timer")!;
        var w = Single(Warnings.ForPendingEdit(tree, timer, Edits(SlotEditor.SetField(timer, EditSet.Empty, "LayoutData.Offsets.Left", 491))), WarningKind.LeavesClip);
        Assert.AreEqual(Severity.Danger, w.Severity);
        StringAssert.Contains(w.Message, "only 0.9×57.4 px of 118.5×59.3 stays visible");
        // 4 units further left, ≈ 4.6 px stay inside: still only cut off
        var cut = Single(Warnings.ForPendingEdit(tree, timer, Edits(SlotEditor.SetField(timer, EditSet.Empty, "LayoutData.Offsets.Left", 487))), WarningKind.LeavesClip);
        Assert.AreEqual(Severity.Warning, cut.Severity, cut.Message);
    }

    [TestMethod]
    public void Rescale_PendingEdit_ShowsScaleBeforeAfter_AndWholeBoardMoves()
    {
        var tree = WidgetTreeReader.Build(Source, Alert); var window = tree.Find("Window")!;
        var pending = new EditWarnings(Engine, new UsageGraph(UiIndex), 1920, 1080)
            .ForPendingEdit(tree, window, Edits(SlotEditor.ResizeEdge(window, EditSet.Empty, Edge.Right, 12)));
        var w = Single(pending, WarningKind.Rescale);
        StringAssert.Contains(w.Message, "ScaleBox_37 scale ×1 → ×0.99379");
        StringAssert.Contains(w.Message, "the whole board rescales and moves");
        StringAssert.Contains(Single(pending, WarningKind.OthersMove).Message, "other widget outside Window move");
    }

    [TestMethod]
    public void Rescale_OnSelection_ProbeFlagsSizeDrivingChild()
    {
        var tree = WidgetTreeReader.Build(Source, Alert);
        var report = new EditWarnings(Engine, new UsageGraph(UiIndex), 1920, 1080).ForSelection(tree, tree.Find("Window")!);
        StringAssert.Contains(Single(report.Warnings, WarningKind.Rescale, "Changing its width").Message, "whole board rescales and moves");
    }

    [TestMethod]
    public void NoRescaleWarning_ForCanaryC1Target()
    {
        var tree = MatchTime;
        var report = Warnings.ForSelection(tree, tree.Find("Img_MatchTimeBg_1")!);
        Assert.IsFalse(report.Warnings.Any(w => w.Kind == WarningKind.Rescale), string.Join(" | ", report.Warnings.Select(w => w.Message)));
    }

    [TestMethod]
    public void FirstEditGate_AsksOncePerPackage_AndNamesBoardsAndAllWarnings()
    {
        var tree = MatchTime; var node = tree.Find("Text_Score_Home")!;
        var report = Warnings.ForSelection(tree, node);
        var gate = new FirstEditGate();
        Assert.IsTrue(gate.NeedsConfirmation(node.PackagePath));
        var text = report.ConfirmationText;
        StringAssert.Contains(text, "You are about to edit MatchTime");
        StringAssert.Contains(text, "Offline scoreboard");
        foreach (var w in report.Warnings.Where(w => w.Kind != WarningKind.Boards))
            StringAssert.Contains(text, w.Message, $"confirmation is missing: {w.Message}");
        StringAssert.Contains(text, "game files are never changed");

        gate.Confirm(node.PackagePath);
        Assert.IsFalse(gate.NeedsConfirmation(node.PackagePath));
        Assert.IsTrue(gate.NeedsConfirmation(Tv + "MatchTime/MatchTimeBase"), "another package asks again");
        Console.WriteLine(text);
    }
}
