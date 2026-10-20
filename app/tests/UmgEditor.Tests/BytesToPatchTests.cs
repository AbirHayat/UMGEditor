using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>Step 6d: the bytes-to-patch panel, the typed-text cue and the save status line. Own project per test, deleted after.</summary>
[TestClass]
public class BytesToPatchTests
{
    private const string Base = Tv + "MatchTime/MatchTimeBase";
    private const string MatchTime = Tv + "MatchTime/MatchTime";
    private const string Right = "LayoutData.Offsets.Right";

    private string _project = null!;
    private static WriteGuard Edits => new(Source.Config);

    [TestInitialize]
    public void NewProject() => _project = "zztest" + Guid.NewGuid().ToString("N")[..10];

    [TestCleanup]
    public void Cleanup() => Edits.DeleteDirectory(Path.Combine(Edits.Root, _project));

    private EditorController Controller() =>
        new(Source, new UsageGraph(UiIndex), Engine, p => WidgetTreeReader.Build(Source, p), project: _project);

    private static void Edit(EditorController c, string package, string name, Func<WidgetNode, EditSet, EditResult> edit)
    {
        var tree = WidgetTreeReader.Build(Source, package);
        var node = tree.Find(name)!;
        var first = c.Apply(tree, node, e => edit(node, e));
        var outcome = first.NeedsConfirmation ? c.Apply(tree, node, e => edit(node, e), confirmed: true) : first;
        Assert.IsTrue(outcome.Applied, outcome.Refusal);
    }

    // ---- the panel's data ----

    [TestMethod]
    public void SharedPlate_RowHasSlotFieldOffsetValuesHexAndContext_AsCanaryC6()
    {
        var c = Controller();
        Assert.IsTrue(c.BytesToPatch().IsEmpty);
        StringAssert.Contains(c.BytesToPatch().Summary, "No edits in project");

        Edit(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 100));
        var report = c.BytesToPatch();
        var package = report.Packages.Single();
        Assert.AreEqual("MatchTimeBase", package.Name);
        Assert.AreEqual(3250, package.StockSize);
        Assert.IsTrue(package.Dirty && !package.Refused);
        StringAssert.Contains(package.Header, "unsaved");

        var row = package.Rows.Single();
        Assert.AreEqual("Img_Plate (CanvasPanelSlot_1)", row.Slot);
        Assert.AreEqual("Offsets.Right", row.Field);
        Assert.AreEqual("float", row.TypeText);
        // the same plan as canary C6 (proven in game): 2 bytes @ 0xA66
        Assert.AreEqual(0xA64, row.Entry.Offset);
        Assert.AreEqual("0 → 100", row.ValueText);
        Assert.AreEqual("00 00 00 00", row.Entry.OldHex);
        Assert.AreEqual("00 00 C8 42", row.Entry.NewHex);
        CollectionAssert.AreEqual(new[] { 0xA66, 0xA67 }, row.Changed.ToArray());
        Assert.AreEqual("2 of 4 bytes change: 0xA66, 0xA67", row.ChangedText);
        Assert.AreEqual(1, row.Entry.ContextMatches);
        StringAssert.Contains(row.MatchText, "found 1 time in the chunk (must be exactly 1)");

        // context: the ±32-byte window in 16-byte rows, field marked, nothing else
        var cells = row.Context.SelectMany(r => r.Cells).ToList();
        Assert.IsTrue(row.Context.All(r => r.Offset % 16 == 0 && r.Cells.Count == 16));
        Assert.AreEqual(32 + 4 + 32, cells.Count(x => x.Kind != HexCellKind.Outside));
        Assert.AreEqual("00 00 00 00", string.Join(" ", cells.Where(x => x.IsField).Select(x => x.Text)));
        CollectionAssert.AreEqual(new[] { false, false, true, true }, cells.Where(x => x.IsField).Select(x => x.Changes).ToArray());
        var window = string.Join(" ", cells.Where(x => x.Kind != HexCellKind.Outside).Select(x => x.Text));
        Assert.AreEqual(row.Entry.ContextHex.Replace("[", "").Replace("]", ""), window);
        Assert.AreEqual("0x000A60  00 00 00 00[00 00 00 00]0C 00 00 00 00 00 00 00", row.Context.Single(r => r.Offset == 0xA60).Text);

        var text = report.ToText();
        Console.WriteLine(text);
        foreach (var expected in new[] { "MatchTimeBase · unsaved", "Img_Plate (CanvasPanelSlot_1) · Offsets.Right · float", "offset   0xA64",
                     "0 → 100", "new hex  00 00 C8 42   (2 of 4 bytes change: 0xA66, 0xA67)", "[00 00 00 00]", package.FileKey })
            StringAssert.Contains(text, expected);
        Assert.IsFalse(Directory.EnumerateFiles(c.Session(Base).Folder, "*.edited.uasset").Any(), "planning writes nothing");
    }

    [TestMethod]
    public void Report_FollowsEveryEdit_UndoRedoSaveAndBackToStock()
    {
        var c = Controller();
        Edit(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 100));
        Edit(c, MatchTime, "Img_MatchTimeBg_1", (n, e) => SlotEditor.Move(n, e, 20, 0));
        var both = c.BytesToPatch();
        CollectionAssert.AreEqual(new[] { "MatchTime", "MatchTimeBase" }, both.Packages.Select(p => p.Name).ToArray());
        var box = both.Packages[0].Rows.Single();
        Assert.AreEqual("Img_MatchTimeBg_1 (CanvasPanelSlot_27)", box.Slot);
        Assert.AreEqual((0x133E23, "126 → 146"), (box.Entry.Offset, box.ValueText), "canary C1's bytes");
        StringAssert.StartsWith(both.Summary, "2 values in 2 packages");

        Edit(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 110));
        Assert.AreEqual("0 → 110", c.BytesToPatch().Packages[1].Rows.Single().ValueText);
        Assert.IsTrue(c.Undo());
        Assert.AreEqual("0 → 100", c.BytesToPatch().Packages[1].Rows.Single().ValueText);
        Assert.IsTrue(c.Undo());
        Assert.AreEqual("MatchTimeBase", c.BytesToPatch().Packages.Single().Name, "MatchTime's edit undone");
        Assert.IsTrue(c.Redo());
        Assert.AreEqual(2, c.BytesToPatch().Packages.Count);

        Assert.IsTrue(c.SaveAll().All(r => r.Result.Saved));
        Assert.IsTrue(c.BytesToPatch().Packages.All(p => !p.Dirty && p.StateText == "saved"));

        // back to stock after a save: still listed (saving would write stock back), with no values
        Edit(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 0));
        var reverted = c.BytesToPatch().Packages.Single(p => p.Name == "MatchTimeBase");
        Assert.IsTrue(reverted.Dirty && reverted.Rows.Count == 0);
        StringAssert.Contains(reverted.EmptyText, "writes the stock bytes back");
        Assert.IsTrue(c.SaveAll().All(r => r.Result.Saved));
        Assert.AreEqual("MatchTime", c.BytesToPatch().Packages.Single().Name, "saved at stock: nothing left to patch there");
    }

    [TestMethod]
    public void Refusal_IsListedWithItsRule_AndTheOtherValuesStillShow()
    {
        var c = Controller();
        Edit(c, MatchTime, "Img_MatchTimeBg_1", (n, e) => SlotEditor.Move(n, e, 20, 0));
        // a value float32 can't hold (SetField rounds; a raw EditResult doesn't)
        var plate = WidgetTreeReader.Build(Source, Base).Find("Img_Plate")!;
        Assert.IsNull(c.Session(Base).Apply(EditResult.Of([new FieldChange(FieldKey.Of(plate, Right), 0, 100.1)])));
        var report = c.BytesToPatch();
        var refused = report.Packages.Single(p => p.Name == "MatchTimeBase");
        Assert.IsTrue(refused.Refused);
        Assert.AreEqual(RefusalRule.NotRepresentable, refused.Refusals.Single().Rule);
        StringAssert.Contains(refused.StateText, "save would be refused");
        Assert.AreEqual(1, report.Packages.Single(p => p.Name == "MatchTime").Rows.Count);
        StringAssert.Contains(report.Summary, "1 refusal");
        StringAssert.Contains(report.ToText(), "REFUSED [NotRepresentable]");
    }

    [TestMethod]
    public void HexRow_Text_MarksTheFieldAndKeepsColumnsAligned()
    {
        var key = new FieldKey("/Game/x/P", 1, "LayoutData.Offsets.Top");
        byte[] before = [.. Enumerable.Range(0, 32).Select(i => (byte) i)], after = [.. Enumerable.Range(0, 32).Select(i => (byte) (0xF0 - i))];
        var entry = new PatchEntry(key, "CanvasPanelSlot_3", 0x25, FieldType.Float, 1, 2, [0, 0, 0x80, 0x3F], [0, 0, 0, 0x40], before, after, 1);
        var rows = new PatchRow(entry, "CanvasPanelSlot_3").Context;
        CollectionAssert.AreEqual(new[] { 0x00, 0x10, 0x20, 0x30, 0x40 }, rows.Select(r => r.Offset).ToArray(), "window 0x05..0x48");
        Assert.AreEqual("0x000000                 00 01 02 03 04 05 06 07 08 09 0A", rows[0].Text);
        Assert.AreEqual("0x000020  1B 1C 1D 1E 1F[00 00 80 3F]F0 EF EE ED EC EB EA", rows[2].Text);
        Assert.AreEqual("0x000040  D9 D8 D7 D6 D5 D4 D3 D2 D1", rows[4].Text, "trailing blanks trimmed");
        Assert.AreEqual(rows[1].Text.Length, rows[2].Text.Length, "brackets replace the separators, so the columns line up");
        Assert.AreEqual("CanvasPanelSlot_3", new PatchRow(entry, "CanvasPanelSlot_3").Slot, "no widget name: just the slot");
        Assert.AreEqual("2 of 4 bytes change: 0x27, 0x28", new PatchRow(entry, "W").ChangedText);
    }

    // ---- typed text vs applied value ----

    [TestMethod]
    public void Typed_SameStoredValueIsApplied_OtherNumberPending_RestInvalid()
    {
        var edited = new FieldInspection(Right, "0", true, 100, GreyReason.None, null);
        foreach (var same in new[] { "100", " 100 ", "100.0", "1e2", "100.0000001" })
            Assert.AreEqual(TypedState.Applied, edited.Typed(same), same);
        Assert.AreEqual(TypedState.Pending, edited.Typed("120"));
        Assert.AreEqual(TypedState.Pending, edited.Typed("0"), "the stock value is a change too while edited");
        Assert.AreEqual(TypedState.Invalid, edited.Typed("1,5"));
        Assert.AreEqual(TypedState.Invalid, edited.Typed(""));
        Assert.AreEqual(TypedState.Invalid, edited.Typed("abc"));
        Assert.IsNull(edited.TypedHint("100.0"));
        Assert.AreEqual("Typed 120, not applied: the value is still 100. Enter applies it, Esc reverts.", edited.TypedHint(" 120"));
        StringAssert.Contains(edited.TypedHint("1,5"), "decimal separator");

        var stock = new FieldInspection(Right, "126", true, null, GreyReason.None, null);
        Assert.AreEqual(TypedState.Applied, stock.Typed("126"));
        Assert.AreEqual(TypedState.Pending, stock.Typed("126.5"));
        var zorder = new FieldInspection("ZOrder", "3", true, null, GreyReason.None, null);
        Assert.AreEqual(TypedState.Applied, zorder.Typed("3.4"), "ZOrder stores rounded");
        Assert.AreEqual(TypedState.Pending, zorder.Typed("4"));
        var grey = new FieldInspection("LayoutData.Offsets.Left", "0", false, null, GreyReason.NotStored, "not stored");
        Assert.AreEqual(TypedState.Applied, grey.Typed("5"), "greyed fields have no text box");
    }

    // ---- save status line ----

    [TestMethod]
    public void SaveStatus_OneLinePerSave_RefusalsNamed()
    {
        var c = Controller();
        Assert.AreEqual("Nothing to save.", EditorController.SaveStatus([]));
        Edit(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 100));
        Edit(c, MatchTime, "Img_MatchTimeBg_1", (n, e) => SlotEditor.Move(n, e, 20, 0));
        c.Session(MatchTime).BeforeVerify = b => [.. b, 0];
        var refused = c.SaveAll();
        Assert.AreEqual("Save refused for MatchTime; nothing of it was written.", EditorController.SaveStatus(refused));

        c.Session(MatchTime).BeforeVerify = null;
        Edit(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 110));
        var saved = c.SaveAll();
        Assert.AreEqual("Saved MatchTimeBase (1 value, 2 bytes differ from stock), MatchTime (1 value, 2 bytes differ from stock); " +
                        "rebuilt from the stock copy, verified, size unchanged.", EditorController.SaveStatus(saved));

        Edit(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 0));
        StringAssert.StartsWith(EditorController.SaveStatus(c.SaveAll()), "Saved MatchTimeBase (back to stock);");
    }
}
