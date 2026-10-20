using System.Numerics;
using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>Step 6f: dragging in the preview. Each test uses its own projects (work\edits\zztest&lt;id&gt;…) and deletes them.</summary>
[TestClass]
public class SlotDragTests
{
    private const string Base = Tv + "MatchTime/MatchTimeBase";
    private const string MatchTime = Tv + "MatchTime/MatchTime";
    private const string Alert = "/Game/Assets/ui/Data/Widget/General/Alert/AlertEventRatingChange/AlertEventRatingChange";
    private const int W = 1786, H = 1000;

    private readonly List<string> _projects = [];
    private static WriteGuard Edits => new(Source.Config);

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var p in _projects) Edits.DeleteDirectory(Path.Combine(Edits.Root, p));
    }

    private EditorController Controller()
    {
        var name = "zztest" + Guid.NewGuid().ToString("N")[..10];
        _projects.Add(name);
        return new EditorController(Source, new UsageGraph(UiIndex), Engine, p => WidgetTreeReader.Build(Source, p), project: name);
    }

    private static string PackageNamed(string name) =>
        GamePaths.ToPackagePath(UiIndex.Single(e => e.Kind == UiAssetKind.Widget && e.Name == name && !e.Path.Contains("/library/")).Path);

    private static (WidgetPackageTree Tree, WidgetNode Node) Find(string package, string name)
    {
        var tree = WidgetTreeReader.Build(Source, package);
        return (tree, tree.Find(name) ?? throw new AssertFailedException($"{name} not in {package}"));
    }

    /// <summary>Viewport px for a drag of these units (the inverse of SlotEditor.PixelsToUnits), plus a jitter that snapping removes.</summary>
    private static (float Dx, float Dy) Px(SlotDrag drag, float ux, float uy, float jitterPx = 0.3f)
    {
        var perPx = SlotEditor.PixelsToUnits(drag.StartBox!, 1, 1);
        return (ux / perPx.X + (ux != 0 ? jitterPx : 0), uy / perPx.Y + (uy != 0 ? jitterPx : 0));
    }

    /// <summary>Drags in one controller: start, a few intermediate steps, the final position, mouse-up (first-edit gate confirmed).</summary>
    private static DragStep Drag(EditorController c, string package, string widget, DragHandle handle, float ux, float uy)
    {
        var (tree, node) = Find(package, widget);
        var (drag, why) = c.BeginDrag(tree, node, handle, W, H);
        Assert.IsNotNull(drag, why);
        var (dx, dy) = Px(drag, ux, uy);
        foreach (var f in new[] { 0.25f, 0.5f, 0.75f }) drag.Update(dx * f, dy * f, free: false);
        var step = drag.Update(dx, dy, free: false);
        Assert.AreEqual(new Vector2(ux, uy), step.Units, "snapped to whole units");
        var first = c.CommitDrag(tree, drag);
        var outcome = first.NeedsConfirmation ? c.CommitDrag(tree, drag, confirmed: true) : first;
        Assert.IsTrue(outcome.Applied, outcome.Refusal);
        return step;
    }

    private static void Type(EditorController c, string package, string widget, string path, string text)
    {
        var (tree, node) = Find(package, widget);
        var first = c.SetValue(tree, node, path, text);
        var outcome = first.NeedsConfirmation ? c.SetValue(tree, node, path, text, confirmed: true) : first;
        Assert.IsTrue(outcome.Applied, outcome.Refusal);
    }

    private static Dictionary<string, double> Values(EditorController c) =>
        c.Edits.Values.ToDictionary(kv => $"{GamePaths.ShortName(kv.Key.Package)}#{kv.Key.SlotExport}.{kv.Key.Path}", kv => kv.Value);

    private static int UndoSteps(EditorController c)
    {
        var n = 0;
        while (c.Undo()) n++;
        return n;
    }

    /// <summary>Same stored values, same planned bytes, byte-identical saved files, same undo history.</summary>
    private static void AssertSameAsTyping(EditorController dragged, EditorController typed, string package, int expectedUndoSteps = 1)
    {
        CollectionAssert.AreEquivalent(Values(typed).ToList(), Values(dragged).ToList(), "stored values");
        var (a, b) = (dragged.Session(package).Plan(), typed.Session(package).Plan());
        Assert.IsTrue(a.Ok && b.Ok);
        CollectionAssert.AreEqual(b.Entries.Select(e => (e.Offset, e.OldHex, e.NewHex)).ToList(), a.Entries.Select(e => (e.Offset, e.OldHex, e.NewHex)).ToList(), "bytes to patch");

        Assert.AreEqual(typed.NextUndo, dragged.NextUndo);
        var (sa, sb) = (dragged.SavePackage(package).Single().Result, typed.SavePackage(package).Single().Result);
        Assert.IsTrue(sa.Saved && sb.Saved);
        Assert.AreEqual(sb.Sha256, sa.Sha256, "the saved .uasset files are byte-identical");

        Assert.AreEqual(expectedUndoSteps, UndoSteps(typed), "typed");
        Assert.AreEqual(1, UndoSteps(dragged), "a drag is one undo step");
        Assert.AreEqual(0, dragged.Edits.Count, "undo goes back to stock");
        Assert.IsTrue(dragged.Redo() && !dragged.Redo(), "and redo replays the drag as one step");
    }

    // ---- the required equivalence: drag == typing the number ----

    /// <summary>Non-stretched axis, move: Left 126 → 146 (canary C1).</summary>
    [TestMethod]
    public void Move_NonStretched_SameAsTypingLeft()
    {
        var (dragged, typed) = (Controller(), Controller());
        var step = Drag(dragged, MatchTime, "Img_MatchTimeBg_1", DragHandle.Move, 20, 0);
        Assert.AreEqual("Offsets.Left 126 → 146 (+20 u)", step.Text);
        Type(typed, MatchTime, "Img_MatchTimeBg_1", "LayoutData.Offsets.Left", "146");
        Assert.AreEqual(0x133E23, dragged.Session(MatchTime).Plan().Entries.Single().Offset, "canary C1's offset");
        AssertSameAsTyping(dragged, typed, MatchTime);
    }

    /// <summary>Non-stretched axis, far edge: the size (Right 120 → 150, canary C2); alignment 0, so Left doesn't move.</summary>
    [TestMethod]
    public void RightEdge_NonStretched_SameAsTypingTheSize()
    {
        var (dragged, typed) = (Controller(), Controller());
        var step = Drag(dragged, MatchTime, "Img_MatchTimeBg_1", DragHandle.Right, 30, 0);
        Assert.AreEqual("Offsets.Right 120 → 150 (+30 u)", step.Text);
        Type(typed, MatchTime, "Img_MatchTimeBg_1", "LayoutData.Offsets.Right", "150");
        AssertSameAsTyping(dragged, typed, MatchTime);
    }

    /// <summary>Stretched axis, edge: the right margin (Right 0 → 100, canary C6's 00 00 C8 42 at 0xA64).</summary>
    [TestMethod]
    public void RightEdge_Stretched_SameAsTypingTheMargin()
    {
        var (dragged, typed) = (Controller(), Controller());
        var step = Drag(dragged, Base, "Img_Plate", DragHandle.Right, -100, 0);
        Assert.AreEqual("Offsets.Right 0 → 100 (+100 u)", step.Text, "dragging the right edge left grows the right margin");
        Type(typed, Base, "Img_Plate", "LayoutData.Offsets.Right", "100");
        var entry = dragged.Session(Base).Plan().Entries.Single();
        Assert.AreEqual((0xA64, "00 00 C8 42"), (entry.Offset, entry.NewHex));
        AssertSameAsTyping(dragged, typed, Base);
    }

    /// <summary>
    /// Stretched axis, move: both margins change (Left +5, Right −5), so the typed equivalent is two numbers. Values and
    /// bytes are the same; the drag is one undo step where typing is two.
    /// </summary>
    [TestMethod]
    public void Move_Stretched_BothMarginsInOneStep()
    {
        var shadow = PackageNamed("StadiumAssetPreview");
        var (dragged, typed) = (Controller(), Controller());
        var step = Drag(dragged, shadow, "Img_Shadow", DragHandle.Move, 5, 0);
        Assert.AreEqual("Offsets.Left -3 → 2 (+5 u), Offsets.Right -3 → -8 (−5 u)", step.Text);
        Type(typed, shadow, "Img_Shadow", "LayoutData.Offsets.Left", "2");
        Type(typed, shadow, "Img_Shadow", "LayoutData.Offsets.Right", "-8");
        AssertSameAsTyping(dragged, typed, shadow, expectedUndoSteps: 2);
    }

    // ---- snapping, free movement, live warnings, cancel ----

    [TestMethod]
    public void Snapping_RoundsToWholeUnits_FreeKeepsTheFraction()
    {
        var c = Controller();
        var (tree, node) = Find(MatchTime, "Img_MatchTimeBg_1");
        var (drag, _) = c.BeginDrag(tree, node, DragHandle.Move, W, H);
        var perPx = SlotEditor.PixelsToUnits(drag!.StartBox!, 1, 0).X;
        Assert.AreEqual(1 / 0.925926, perPx, 1e-4, "1 design unit = 0.9259 px at 1786×1000");
        Assert.AreEqual(new Vector2(20, 0), drag.UnitsFor(20.4f / perPx, 0, free: false));
        Assert.AreEqual(new Vector2(21, 0), drag.UnitsFor(20.6f / perPx, 0, free: false));
        Assert.AreEqual(20.4f, drag.UnitsFor(20.4f / perPx, 3, free: true).X, 1e-3);
        Assert.AreEqual(146.4, drag.Update(20.4f / perPx, 0, free: true).Edit.Changes.Single().NewValue, 1e-3);
        Assert.AreEqual(new Vector2(0, 0), new SlotDrag(Engine, tree, node, DragHandle.Right, EditSet.Empty, c.DragHandles(node), W, H).UnitsFor(0, 50, false),
            "an edge only follows its own axis");
    }

    [TestMethod]
    public void LiveWarnings_LeavingTheClip_RescaleAndOthersMoving()
    {
        var c = Controller();
        var (board, mask) = Find(MatchTime, "Mask");
        var drag = c.BeginDrag(board, mask, DragHandle.Move, W, H).Drag!;
        var (dx, _) = Px(drag, 700, 0);
        var step = drag.Update(dx, 0, false);
        Assert.AreEqual(Severity.Danger, step.Warnings.Single(w => w.Kind == WarningKind.LeavesClip).Severity, "canary 1: Left 129 → 829 vanishes");
        Assert.AreEqual(812f, step.Layout.All().Single(b => ReferenceEquals(b.Node, mask)).Bounds.X, 0.5f, "the step's layout shows the dragged position");
        Assert.AreEqual(0, drag.Update(Px(drag, 120, 0).Dx, 0, false).Warnings.Count(w => w.Kind == WarningKind.LeavesClip), "canary 2: Left 249 stays visible");

        var (alert, window) = Find(Alert, "Window");
        var resize = new SlotDrag(Engine, alert, window, DragHandle.Right, EditSet.Empty, c.DragHandles(window), 1920, 1080);
        var grown = resize.Update(Px(resize, 12, 0).Dx, 0, false);
        StringAssert.Contains(grown.Warnings.Single(w => w.Kind == WarningKind.Rescale).Message, "ScaleBox_37 scale ×1 → ×0.99379");
        StringAssert.Contains(grown.Warnings.Single(w => w.Kind == WarningKind.OthersMove).Message, "outside Window move");
    }

    [TestMethod]
    public void Cancel_ChangesNothing_AndAZeroDragIsNoEdit()
    {
        var c = Controller();
        var (tree, node) = Find(MatchTime, "Img_MatchTimeBg_1");
        var (drag, _) = c.BeginDrag(tree, node, DragHandle.Move, W, H);
        drag!.Update(Px(drag, 40, 0).Dx, 0, false);
        // Esc: the drag is dropped without CommitDrag
        Assert.AreEqual(0, c.Edits.Count);
        Assert.IsFalse(c.CanUndo || c.AnyDirty);
        Assert.IsFalse(Directory.Exists(c.ProjectFolder), "a drag writes nothing until it is committed");

        var back = c.BeginDrag(tree, node, DragHandle.Move, W, H).Drag!;
        back.Update(Px(back, 3, 0).Dx, 0, false);
        back.Update(0.2f, 0, false); // back where it started
        var outcome = c.CommitDrag(tree, back);
        Assert.IsFalse(outcome.Applied || outcome.NeedsConfirmation || outcome.Refusal != null, "no edit, no first-edit prompt");
        Assert.IsFalse(c.CanUndo);
    }

    [TestMethod]
    public void FirstDragOfAPackage_AsksLikeTyping()
    {
        var c = Controller();
        var (tree, node) = Find(Base, "Img_Plate");
        var (drag, _) = c.BeginDrag(tree, node, DragHandle.Right, W, H);
        drag!.Update(Px(drag, -100, 0).Dx, 0, false);
        var first = c.CommitDrag(tree, drag);
        Assert.IsTrue(first.NeedsConfirmation);
        StringAssert.Contains(first.ConfirmationText, "MatchTimeGoal");
        Assert.AreEqual(0, c.Edits.Count);
        Assert.IsTrue(c.CommitDrag(tree, drag, confirmed: true).Applied);
        Assert.AreEqual(1, c.Edits.Count);
    }

    // ---- handles and the reasons instead of them ----

    [TestMethod]
    public void Handles_StretchedPlate_OnlyStoredEdges()
    {
        var c = Controller();
        var h = c.DragHandles(Find(Base, "Img_Plate").Node);
        Assert.IsTrue(h.Any && h.Allows(DragHandle.Right) && h.Allows(DragHandle.Bottom));
        Assert.IsFalse(h.Allows(DragHandle.Move) || h.Allows(DragHandle.Left) || h.Allows(DragHandle.Top));
        StringAssert.Contains(h.Refusals[DragHandle.Move], "Offsets.Left is not stored");
        StringAssert.Contains(h.Refusals[DragHandle.Left], "Offsets.Left is not stored");

        var box = c.DragHandles(Find(MatchTime, "Img_MatchTimeBg_1").Node);
        Assert.IsTrue(box.MoveX && box.MoveY && Enum.GetValues<DragHandle>().All(box.Allows));

        // non-stretched X with alignment 0.5: both X edges also move Left, which isn't stored; Y is stretched with both margins stored
        var fill = c.DragHandles(Find(PackageNamed("CodeList"), "Window_Fill").Node);
        Assert.IsFalse(fill.MoveX || fill.Allows(DragHandle.Left) || fill.Allows(DragHandle.Right));
        Assert.IsTrue(fill.MoveY && fill.Allows(DragHandle.Top) && fill.Allows(DragHandle.Bottom));
    }

    [TestMethod]
    public void Handles_LibraryAndEmbeddedWidgets_ShowTheReasonInstead()
    {
        var c = Controller();
        var image = WidgetTreeReader.Build(Source, "/Game/Assets/ui/library/uiImage");
        var lib = c.DragHandles(image.All().FirstOrDefault(n => n.Slot != null) ?? image.Root!);
        Assert.IsFalse(lib.Any);
        StringAssert.Contains(lib.Reason, "Konami library widget");

        var embedded = WidgetTreeReader.Build(Source, Base, readOnly: true).Find("Img_Plate")!;
        var ro = c.DragHandles(embedded);
        Assert.IsFalse(ro.Any);
        StringAssert.Contains(ro.Reason, "Open MatchTimeBase to edit it");
        var (tree, _) = Find(MatchTime, "Img_MatchTimeBg_1");
        Assert.IsNull(c.BeginDrag(tree, embedded, DragHandle.Right, W, H).Drag);
    }

    // ---- arrow keys ----

    [TestMethod]
    public void Nudge_OneUnitPerPress_OneUndoStepEach_RefusedAxisSaysWhy()
    {
        var c = Controller();
        var (tree, node) = Find(MatchTime, "Img_MatchTimeBg_1");
        var first = c.Nudge(tree, node, 1, 0);
        Assert.IsTrue(first.NeedsConfirmation);
        Assert.IsTrue(c.Nudge(tree, node, 1, 0, confirmed: true).Applied);
        Assert.IsTrue(c.Nudge(tree, node, 1, 0).Applied && c.Nudge(tree, node, 0, -1).Applied);
        Assert.AreEqual(128, Values(c).Single(kv => kv.Key.EndsWith("Offsets.Left")).Value);
        Assert.AreEqual(8, Values(c).Single(kv => kv.Key.EndsWith("Offsets.Top")).Value, "Top 9 → 8");
        Assert.AreEqual(3, UndoSteps(c));

        var (baseTree, plate) = Find(Base, "Img_Plate");
        StringAssert.Contains(c.Nudge(baseTree, plate, 1, 0).Refusal, "Offsets.Left is not stored");
    }
}
