using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Layout;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>Step 2: edit rules, checked against the preview layout (viewport px, tolerance 0.01 px).</summary>
[TestClass]
public class SlotEditorTests
{
    private const double Tol = 0.01;
    private const int W = 1786, H = 1000; // the validated LDPlayer game view

    private static WidgetPackageTree MatchTime => WidgetTreeReader.Build(Source, Tv + "MatchTime/MatchTime");

    // ---- helpers ----

    private static (LayoutBox Before, LayoutBox After, double Ppu) Apply(WidgetPackageTree tree, WidgetNode node, EditResult edit, int w = W, int h = H)
    {
        Assert.IsTrue(edit.Allowed, edit.Reason);
        var edits = EditSet.Empty.With(edit.Changes, _ => null);
        var before = Box(Engine.Arrange(tree, w, h), node);
        var after = Box(Engine.Arrange(tree, w, h, edits), node);
        return (before, after, before.Parent!.ToViewport.M11); // px per unit of the slot's (parent) space
    }

    private static LayoutBox Box(LayoutResult layout, WidgetNode node) =>
        layout.All().Single(b => ReferenceEquals(b.Node, node));

    private static void Near(double expected, double actual, string what) =>
        Assert.AreEqual(expected, actual, Tol, $"{what}: expected {expected:0.####}, got {actual:0.####}");

    /// <summary>First node across all widgets matching a predicate (deterministic order), so cases use real game data.</summary>
    private static (WidgetPackageTree Tree, WidgetNode Node) FindReal(Func<WidgetNode, bool> match, string what)
    {
        foreach (var e in UiIndex.Where(e => e.Kind == UiAssetKind.Widget).OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            WidgetPackageTree tree;
            try { tree = WidgetTreeReader.Build(Source, GamePaths.ToPackagePath(e.Path)); } catch { continue; }
            if (tree.All().FirstOrDefault(n => n.Slot?.ClassName == SlotEditor.CanvasSlot && match(n)) is { } node)
            {
                Console.WriteLine($"{what}: {e.Name}/{node.Name} ({node.Slot!.Name})");
                return (tree, node);
            }
        }
        Assert.Inconclusive($"no widget in the game has {what}");
        return default;
    }

    private static bool Stored(WidgetNode n, string path) => n.Slot!.Fields.Any(f => f.Path == path && f.Stored);
    private static double Value(WidgetNode n, string path) => EditSet.Empty.Effective(n, path);

    // ---- non-stretched axis ----

    [TestMethod]
    public void Move_NonStretched_X_ShiftsByDeltaTimesScale_SizeUnchanged()
    {
        var tree = MatchTime; var node = tree.Find("Img_MatchTimeBg_1")!;
        var (b, a, ppu) = Apply(tree, node, SlotEditor.Move(node, EditSet.Empty, 20, 0));
        Near(b.Bounds.X + 20 * ppu, a.Bounds.X, "X");
        Near(b.Bounds.Width, a.Bounds.Width, "width");
        Near(b.Bounds.Y, a.Bounds.Y, "Y");
    }

    [TestMethod]
    public void Move_NonStretched_Y_ShiftsByDeltaTimesScale()
    {
        var tree = MatchTime; var node = tree.Find("Img_MatchTimeBg_1")!;
        var (b, a, ppu) = Apply(tree, node, SlotEditor.Move(node, EditSet.Empty, 0, 10));
        Near(b.Bounds.Y + 10 * ppu, a.Bounds.Y, "Y");
        Near(b.Bounds.Height, a.Bounds.Height, "height");
        Near(b.Bounds.X, a.Bounds.X, "X");
    }

    [TestMethod]
    public void ResizeRightEdge_NonStretched_Alignment0_KeepsLeftEdge()
    {
        var tree = MatchTime; var node = tree.Find("Img_MatchTimeBg_1")!;
        var edit = SlotEditor.ResizeEdge(node, EditSet.Empty, Edge.Right, 30);
        CollectionAssert.AreEqual(new[] { "LayoutData.Offsets.Right" }, edit.Changes.Select(c => c.Key.Path).ToArray(), "alignment 0: only the size changes");
        var (b, a, ppu) = Apply(tree, node, edit);
        Near(b.Bounds.X, a.Bounds.X, "left edge");
        Near(b.Bounds.Right + 30 * ppu, a.Bounds.Right, "right edge");
    }

    [TestMethod]
    public void ResizeLeftEdge_NonStretched_Alignment0_KeepsRightEdge()
    {
        var tree = MatchTime; var node = tree.Find("Img_MatchTimeBg_1")!;
        var (b, a, ppu) = Apply(tree, node, SlotEditor.ResizeEdge(node, EditSet.Empty, Edge.Left, -15));
        Near(b.Bounds.X - 15 * ppu, a.Bounds.X, "left edge");
        Near(b.Bounds.Right, a.Bounds.Right, "right edge");
    }

    /// <summary>
    /// With alignment ≠ 0 the opposite edge must stay put *relative to the anchor point* (the slot's own frame).
    /// Measured in parent-local units because the parent itself can change size (see the ScaleBox feedback test).
    /// </summary>
    [TestMethod]
    public void Resize_NonStretched_WithAlignment_KeepsOppositeEdge_InTheSlotFrame()
    {
        var (tree, node) = FindReal(n => !SlotEditor.IsStretched(n, Axis.X, EditSet.Empty)
            && Stored(n, "LayoutData.Alignment.X") && Value(n, "LayoutData.Alignment.X") is > 0 and < 1
            && Stored(n, "LayoutData.Offsets.Left") && Stored(n, "LayoutData.Offsets.Right") && Value(n, "LayoutData.Offsets.Right") > 20,
            "a non-stretched slot with stored alignment between 0 and 1 and stored Left/Right");
        var align = Value(node, "LayoutData.Alignment.X");

        var right = SlotEditor.ResizeEdge(node, EditSet.Empty, Edge.Right, 12);
        Assert.AreEqual(2, right.Changes.Count, "size and position both change when alignment ≠ 0");
        var (l0, r0, l1, r1) = EdgesInSlotFrame(tree, node, right);
        Near(l0, l1, $"left edge (alignment {align})");
        Near(r0 + 12, r1, "right edge");

        (l0, r0, l1, r1) = EdgesInSlotFrame(tree, node, SlotEditor.ResizeEdge(node, EditSet.Empty, Edge.Left, 9));
        Near(l0 + 9, l1, "left edge");
        Near(r0, r1, $"right edge (alignment {align})");
    }

    /// <summary>Left/right edge before and after, in parent-local units relative to the slot's anchor point.</summary>
    private static (double L0, double R0, double L1, double R1) EdgesInSlotFrame(WidgetPackageTree tree, WidgetNode node, EditResult edit)
    {
        Assert.IsTrue(edit.Allowed, edit.Reason);
        var edits = EditSet.Empty.With(edit.Changes, _ => null);
        (double L, double R) Edges(LayoutResult layout, EditSet e)
        {
            var box = Box(layout, node);
            var toParent = box.ToViewport;
            System.Numerics.Matrix3x2.Invert(box.Parent!.ToViewport, out var parentInverse);
            var local = toParent * parentInverse; // box local → parent local
            var anchor = e.Effective(node, "LayoutData.Anchors.Minimum.X") * box.Parent.LocalSize.X;
            return (local.M31 - anchor, local.M31 + box.LocalSize.X * local.M11 - anchor);
        }
        var (l0, r0) = Edges(Engine.Arrange(tree, 1920, 1080), EditSet.Empty);
        var (l1, r1) = Edges(Engine.Arrange(tree, 1920, 1080, edits), edits);
        return (l0, r0, l1, r1);
    }

    /// <summary>
    /// Real UMG behaviour the editor must surface (Step 3 warning): a child that sets its canvas's desired size under a
    /// ScaleBox changes the ScaleBox scale when resized, so the whole frame rescales on screen.
    /// </summary>
    [TestMethod]
    public void Resize_ChildThatSizesAScaleBoxCanvas_RescalesTheFrame()
    {
        var tree = WidgetTreeReader.Build(Source, "/Game/Assets/ui/Data/Widget/General/Alert/AlertEventRatingChange/AlertEventRatingChange");
        var window = tree.Find("Window")!;
        var edits = EditSet.Empty.With(SlotEditor.ResizeEdge(window, EditSet.Empty, Edge.Right, 12).Changes, _ => null);
        var before = Engine.Arrange(tree, 1920, 1080).ScaleBoxes.Single(s => s.Widget == "ScaleBox_37");
        var after = Engine.Arrange(tree, 1920, 1080, edits).ScaleBoxes.Single(s => s.Widget == "ScaleBox_37");
        Near(1.0, before.Applied, "stock scale");
        Near(1920.0 / 1932.0, after.Applied, "scale after the canvas grows from 1920 to 1932 units");
    }

    [TestMethod]
    public void Resize_WithAlignment_RefusedWhenLeftNotStored_Window()
    {
        var tree = MatchTime; var window = tree.Find("Window")!;
        Assert.AreEqual(0.5, Value(window, "LayoutData.Alignment.X"));
        var r = SlotEditor.ResizeEdge(window, EditSet.Empty, Edge.Right, 10);
        Assert.IsFalse(r.Allowed);
        StringAssert.Contains(r.Reason, "Offsets.Left is not stored");
    }

    // ---- stretched axis ----

    [TestMethod]
    public void ResizeRightEdge_Stretched_ChangesMarginOnly_AndScaleBoxRescales()
    {
        var tree = MatchTime; var scaleBox = tree.Find("ScaleBox_0")!;
        Assert.IsTrue(SlotEditor.IsStretched(scaleBox, Axis.X, EditSet.Empty));
        var edit = SlotEditor.ResizeEdge(scaleBox, EditSet.Empty, Edge.Right, -200); // right edge 200 units to the left
        CollectionAssert.AreEqual(new[] { "LayoutData.Offsets.Right" }, edit.Changes.Select(c => c.Key.Path).ToArray());
        Assert.AreEqual(200.0, edit.Changes[0].NewValue, "stretched right margin grows by 200");

        var (b, a, ppu) = Apply(tree, scaleBox, edit);
        Near(b.Bounds.X, a.Bounds.X, "left edge");
        Near(b.Bounds.Right - 200 * ppu, a.Bounds.Right, "right edge");

        var stock = Engine.Arrange(tree, W, H).ScaleBoxes.Single(s => s.Widget == "ScaleBox_0");
        var edited = Engine.Arrange(tree, W, H, EditSet.Empty.With(edit.Changes, _ => null)).ScaleBoxes.Single(s => s.Widget == "ScaleBox_0");
        var areaW = W / ppu - 200; var areaH = H / ppu;
        Near(Math.Min(areaW / 1920, areaH / 1080), edited.Applied, "ScaleToFit scale after the margin edit");
        Assert.AreEqual("height", stock.LimitingAxis);
        Assert.AreEqual("width", edited.LimitingAxis, "a narrower area makes width the limiting axis");
    }

    [TestMethod]
    public void Move_Stretched_ShiftsBothEdges()
    {
        var (tree, node) = FindReal(n => SlotEditor.IsStretched(n, Axis.X, EditSet.Empty)
            && Stored(n, "LayoutData.Offsets.Left") && Stored(n, "LayoutData.Offsets.Right"),
            "a stretched-X slot with both margins stored");
        var edit = SlotEditor.Move(node, EditSet.Empty, 25, 0);
        CollectionAssert.AreEquivalent(new[] { "LayoutData.Offsets.Left", "LayoutData.Offsets.Right" }, edit.Changes.Select(c => c.Key.Path).ToArray());
        var (b, a, ppu) = Apply(tree, node, edit, 1920, 1080);
        Near(b.Bounds.X + 25 * ppu, a.Bounds.X, "left edge");
        Near(b.Bounds.Right + 25 * ppu, a.Bounds.Right, "right edge");
    }

    [TestMethod]
    public void Move_Stretched_RefusedWhenAMarginIsNotStored_ScaleBox()
    {
        var scaleBox = MatchTime.Find("ScaleBox_0")!;
        var r = SlotEditor.Move(scaleBox, EditSet.Empty, 10, 0);
        Assert.IsFalse(r.Allowed);
        StringAssert.Contains(r.Reason, "Offsets.Left is not stored");
    }

    // ---- alignment and anchors (numeric only) ----

    [TestMethod]
    public void Alignment_Window_0_5_to_0_45_ShiftsBy5PercentOfWidth()
    {
        var tree = MatchTime; var window = tree.Find("Window")!;
        var edit = SlotEditor.SetAlignment(window, EditSet.Empty, Axis.X, 0.45, keepPosition: false);
        var (b, a, ppu) = Apply(tree, window, edit);
        Near(b.Bounds.X + (0.5 - SlotEditor.F32(0.45)) * 1920 * ppu, a.Bounds.X, "X");
    }

    [TestMethod]
    public void Alignment_KeepPosition_RefusedWhenLeftNotStored_Window()
    {
        var window = MatchTime.Find("Window")!;
        var r = SlotEditor.SetAlignment(window, EditSet.Empty, Axis.X, 0.45, keepPosition: true);
        Assert.IsFalse(r.Allowed);
        StringAssert.Contains(r.Reason, "Can't keep the position");
        StringAssert.Contains(r.Reason, "Offsets.Left is not stored");
    }

    [TestMethod]
    public void AnchorPoint_Window_0_5_to_0_55_ShiftsBy5PercentOfParent()
    {
        var tree = MatchTime; var window = tree.Find("Window")!;
        var parentWidth = Box(Engine.Arrange(tree, W, H), window).Parent!.LocalSize.X;
        Assert.AreEqual(1920f, parentWidth, "Main is laid out at its 1920-unit desired width");
        var edit = SlotEditor.SetAnchorPoint(window, EditSet.Empty, Axis.X, 0.55, keepPosition: false, parentWidth);
        CollectionAssert.AreEquivalent(new[] { "LayoutData.Anchors.Minimum.X", "LayoutData.Anchors.Maximum.X" }, edit.Changes.Select(c => c.Key.Path).ToArray());
        var (b, a, ppu) = Apply(tree, window, edit);
        Near(b.Bounds.X + (SlotEditor.F32(0.55) - 0.5) * parentWidth * ppu, a.Bounds.X, "X");
    }

    // ---- greyed-out reasons ----

    [TestMethod]
    public void Greyed_NonStoredField_SaysNotStoredAndFileSize()
    {
        var node = MatchTime.Find("Text_Score_Home")!; // Left/Top not stored, Right/Bottom stored
        var reason = SlotEditor.FieldRefusal(node, "LayoutData.Offsets.Left");
        StringAssert.Contains(reason, "Offsets.Left is not stored in the file (engine default 0)");
        StringAssert.Contains(reason, "would change the file size");
        Assert.IsNull(SlotEditor.FieldRefusal(node, "LayoutData.Offsets.Right"), "a stored field on the same slot stays editable");
        Assert.IsFalse(SlotEditor.SetField(node, EditSet.Empty, "LayoutData.Offsets.Left", 5).Allowed);
        Assert.IsFalse(SlotEditor.Move(node, EditSet.Empty, 5, 0).Allowed, "moving along X needs Left");
    }

    [TestMethod]
    public void Greyed_EmbeddedReadOnlyRow_PointsToItsOwnPackage()
    {
        var embedded = WidgetTreeReader.Build(Source, Tv + "MatchTime/MatchTimeTimer", readOnly: true);
        var node = embedded.All().First(n => n.Slot?.ClassName == SlotEditor.CanvasSlot && n.Slot.Fields.Any(f => f.Stored));
        var reason = SlotEditor.FieldRefusal(node, node.Slot!.Fields.First(f => f.Stored).Path);
        StringAssert.Contains(reason, "belongs to MatchTimeTimer");
        StringAssert.Contains(reason, "read-only");
        StringAssert.Contains(reason, "Open MatchTimeTimer to edit it");
        Assert.IsFalse(SlotEditor.Move(node, EditSet.Empty, 1, 0).Allowed);
    }

    [TestMethod]
    public void Greyed_NonCanvasSlot_NamesTheSlotClass()
    {
        var main = MatchTime.Find("Main")!; // sits in ScaleBoxSlot_0
        Assert.AreEqual("ScaleBoxSlot", main.Slot!.ClassName);
        var reason = SlotEditor.SlotRefusal(main);
        StringAssert.Contains(reason, "ScaleBoxSlot values aren't editable");
        Assert.IsFalse(SlotEditor.SetField(main, EditSet.Empty, "HorizontalAlignment", 0).Allowed);
    }

    [TestMethod]
    public void Greyed_RootWidget_HasNoSlot()
    {
        var root = MatchTime.Root!;
        StringAssert.Contains(SlotEditor.SlotRefusal(root), "has no slot");
    }

    [TestMethod]
    public void Greyed_BooleanField()
    {
        var (_, node) = FindReal(n => Stored(n, "bAutoSize"), "a stored bAutoSize");
        StringAssert.Contains(SlotEditor.FieldRefusal(node, "bAutoSize"), "boolean");
    }

    // ---- a numeric edit changes nothing else ----

    [TestMethod]
    public void NumericEdit_ChangesExactlyTheEditedField_AndNothingElse()
    {
        foreach (var board in new[] { "MatchTime/MatchTime", "MatchScore/MatchScore", "MatchStats/MatchStats" })
        {
            var tree = WidgetTreeReader.Build(Source, Tv + board);
            var all = tree.All().Where(n => n.Slot != null).SelectMany(n => n.Slot!.Fields.Select(f => (Node: n, f.Path))).ToList();
            string Snapshot() => string.Join("|", tree.All().Select(n => n.Name + ":" + string.Join(",", n.Slot?.Fields.Select(f => $"{f.Path}={f.Value}{f.Stored}") ?? [])));
            var stockSnapshot = Snapshot();
            int edited = 0;

            foreach (var node in tree.All().Where(n => SlotEditor.SlotRefusal(n) == null))
            foreach (var field in node.Slot!.Fields.Where(f => SlotEditor.FieldRefusal(node, f.Path) == null))
            {
                var result = SlotEditor.SetField(node, EditSet.Empty, field.Path, EditSet.Parse(field.Value) + 7);
                Assert.IsTrue(result.Allowed, result.Reason);
                Assert.AreEqual(1, result.Changes.Count, $"{node.Name}.{field.Path}");
                Assert.AreEqual(FieldKey.Of(node, field.Path), result.Changes[0].Key);

                var edits = EditSet.Empty.With(result.Changes, _ => null);
                foreach (var (other, path) in all)
                {
                    if (ReferenceEquals(other, node) && path == field.Path) continue;
                    if (path is "HorizontalAlignment" or "VerticalAlignment" || !double.TryParse(other.Slot!.Fields.First(f => f.Path == path).Value,
                            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _) && path != "bAutoSize") continue;
                    Assert.AreEqual(EditSet.Empty.Effective(other, path), edits.Effective(other, path), $"editing {node.Name}.{field.Path} changed {other.Name}.{path}");
                }
                edited++;
            }
            Assert.AreEqual(stockSnapshot, Snapshot(), $"{board}: stock tree was modified");
            Console.WriteLine($"{board}: {edited} editable stored fields each changed alone");
            Assert.IsTrue(edited > 0);
        }
    }

    // ---- units and drag ----

    [TestMethod]
    public void Drag_PixelsToUnits_MovesExactlyThoseScreenPixels()
    {
        var tree = MatchTime; var node = tree.Find("Img_MatchTimeBg_1")!;
        var box = Box(Engine.Arrange(tree, W, H), node);
        var units = SlotEditor.PixelsToUnits(box, 37, -11);
        var (b, a, _) = Apply(tree, node, SlotEditor.Move(node, EditSet.Empty, units.X, units.Y));
        Near(b.Bounds.X + 37, a.Bounds.X, "X");
        Near(b.Bounds.Y - 11, a.Bounds.Y, "Y");
    }

    [TestMethod]
    public void Drag_InTenSteps_EqualsOneStep()
    {
        var tree = MatchTime; var node = tree.Find("Img_MatchTimeBg_1")!;
        var edits = EditSet.Empty;
        for (int i = 0; i < 10; i++)
            edits = edits.With(SlotEditor.Move(node, edits, 3.7, 0).Changes, _ => null);
        var once = EditSet.Empty.With(SlotEditor.Move(node, EditSet.Empty, 37, 0).Changes, _ => null);
        Assert.AreEqual(once.Effective(node, "LayoutData.Offsets.Left"), edits.Effective(node, "LayoutData.Offsets.Left"), 1e-3);
    }

    [TestMethod]
    public void EditBackToStock_LeavesAnEmptyEditSet()
    {
        var node = MatchTime.Find("Img_MatchTimeBg_1")!;
        double? Stock(FieldKey k) => k.Path == "LayoutData.Offsets.Left" ? 126 : null;
        var edits = EditSet.Empty.With(SlotEditor.SetField(node, EditSet.Empty, "LayoutData.Offsets.Left", 140).Changes, Stock);
        Assert.AreEqual(1, edits.Count);
        edits = edits.With(SlotEditor.SetField(node, edits, "LayoutData.Offsets.Left", 126).Changes, Stock);
        Assert.AreEqual(0, edits.Count);
    }
}
