using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>Step 6a: the UI-independent editor model. Each test uses its own project (work\edits\zztest&lt;id&gt;) and deletes it.</summary>
[TestClass]
public class EditorControllerTests
{
    private const string Base = Tv + "MatchTime/MatchTimeBase";
    private const string MatchTime = Tv + "MatchTime/MatchTime";
    private const string UiImage = "/Game/Assets/ui/library/uiImage";
    private const string Right = "LayoutData.Offsets.Right";

    private string _project = null!;
    private static WriteGuard Edits => new(Source.Config);

    [TestInitialize]
    public void NewProject() => _project = "zztest" + Guid.NewGuid().ToString("N")[..10];

    [TestCleanup]
    public void Cleanup()
    {
        Edits.DeleteDirectory(Path.Combine(Edits.Root, _project));
        var mods = WriteGuard.ForMods(Source.Config);
        mods.DeleteDirectory(Path.Combine(mods.Root, "test-" + _project));
    }

    private EditorController Controller(string? project = null) =>
        new(Source, new UsageGraph(UiIndex), Engine, p => WidgetTreeReader.Build(Source, p), project: project ?? _project);

    private static (WidgetPackageTree Tree, WidgetNode Node) Find(string package, string name)
    {
        var tree = WidgetTreeReader.Build(Source, package);
        return (tree, tree.Find(name)!);
    }

    private static EditOutcome Confirmed(EditorController c, string package, string name, Func<WidgetNode, EditSet, EditResult> edit)
    {
        var (tree, node) = Find(package, name);
        var first = c.Apply(tree, node, e => edit(node, e));
        var outcome = first.NeedsConfirmation ? c.Apply(tree, node, e => edit(node, e), confirmed: true) : first;
        Assert.IsTrue(outcome.Applied, outcome.Refusal);
        return outcome;
    }

    // ---- projects ----

    [TestMethod]
    public void Projects_DefaultPlusNamedSets_AndNameRules()
    {
        var c = Controller();
        Confirmed(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 100));
        var projects = EditorController.Projects(Source.Config);
        Assert.AreEqual(EditorController.DefaultProject, projects[0]);
        CollectionAssert.Contains(projects.ToList(), _project);
        Assert.IsFalse(projects.Any(p => p.StartsWith('_') || p is "Assets" or "Platforms"), string.Join(", ", projects));
        Assert.IsTrue(c.ProjectFolder.EndsWith(@"edits\" + _project));
        foreach (var bad in new[] { "Assets", "default", "_x", "a/b", "..", "" })
            Assert.IsNotNull(EditorController.ProjectNameRefusal(bad), bad);
        Assert.IsNull(EditorController.ProjectNameRefusal("C7"));
    }

    [TestMethod]
    public void SwitchProject_RefusedWhileDirty_AndReopenLoadsSavedEdits()
    {
        var c = Controller();
        Confirmed(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 100));
        StringAssert.Contains(c.SwitchProject(EditorController.DefaultProject), "unsaved edits in MatchTimeBase");
        Assert.IsTrue(c.SaveAll().All(r => r.Result.Saved));
        Assert.IsNull(c.SwitchProject(EditorController.DefaultProject));
        Assert.IsFalse(c.Edits.Values.Any(kv => kv.Key.Package == Base && kv.Value == 100), "the default project doesn't have this project's edits");

        var reopened = Controller();
        Assert.AreEqual(1, reopened.Edits.Count, "saved edits are loaded with the project");
        Assert.IsFalse(reopened.AnyDirty);
        Assert.IsFalse(reopened.CanUndo, "undo history doesn't survive a reopen");
    }

    // ---- inspection (6b data) ----

    [TestMethod]
    public void Inspect_SharedPlate_FieldsGreyGroupsAndEmbeddingPackages()
    {
        var c = Controller();
        var (tree, plate) = Find(Base, "Img_Plate");
        var i = c.Inspect(tree, plate);
        Assert.IsNull(i.SlotReason);
        var right = i.Fields.Single(f => f.Path == Right);
        Assert.IsTrue(right.Editable && right.Stored && !right.IsEdited);
        Assert.AreEqual("stored", right.State);
        var left = i.Fields.Single(f => f.Path == "LayoutData.Offsets.Left");
        Assert.AreEqual(GreyReason.NotStored, left.Grey);
        StringAssert.Contains(left.Reason, "not stored");
        var notStored = i.GreyGroups.Single(g => g.Reason == GreyReason.NotStored);
        CollectionAssert.Contains(notStored.Fields.ToList(), "Offsets.Left");
        StringAssert.Contains(notStored.Text, "file size");
        Assert.AreEqual(GreyReason.Boolean, i.Fields.Single(f => f.Path == "bAutoSize").Grey);

        CollectionAssert.AreEquivalent(new[] { "MatchTime", "MatchTimeCard", "MatchTimeGoal", "MatchTimeMemberChange" }, i.EmbeddedByOffline.Select(a => a.Name).ToArray());
        Assert.AreEqual(4, i.EmbeddedByOnline.Count);
        Assert.IsTrue(i.EmbeddedByOnline.All(a => a.IsOnline && a.Name.StartsWith("MatchTimeConnection")));
        Assert.IsFalse(Directory.Exists(c.ProjectFolder), "inspecting must not create a working copy");
    }

    [TestMethod]
    public void Inspect_LibraryWidget_IsBlockedWithTheReason()
    {
        var c = Controller();
        var tree = WidgetTreeReader.Build(Source, UiImage);
        var node = tree.All().FirstOrDefault(n => n.Slot != null) ?? tree.Root!;
        var i = c.Inspect(tree, node);
        Assert.AreEqual(GreyReason.Library, i.SlotGrey);
        StringAssert.Contains(i.SlotReason, "uiImage is a Konami library widget");
        StringAssert.Contains(i.SlotReason, "editing library widgets is blocked");
        Assert.IsTrue(i.Report.EmbeddedBy.Count > 2000, i.Report.EmbeddedBy.Count.ToString());
        Assert.IsTrue(i.Fields.All(f => !f.Editable && f.Grey == GreyReason.Library));
        StringAssert.Contains(i.Report.Warnings.Single(w => w.Kind == WarningKind.Boards).Message, "more."); // truncated list
        Console.WriteLine(i.SlotReason);

        var outcome = c.Apply(tree, node, e => SlotEditor.Move(node, e, 1, 0));
        Assert.IsFalse(outcome.Applied);
        StringAssert.Contains(outcome.Refusal, "library widget");
        Assert.IsNotNull(SlotEditor.SlotRefusal(node), "the CLI is blocked too");
    }

    [TestMethod]
    public void Inspect_EmbeddedRow_IsReadOnlyWithTheOwningPackage()
    {
        var c = Controller();
        var board = WidgetTreeReader.Build(Source, MatchTime);
        var embedded = WidgetTreeReader.Build(Source, Base, readOnly: true).Find("Img_Plate")!;
        var i = c.Inspect(board, embedded);
        Assert.AreEqual(GreyReason.EmbeddedReadOnly, i.SlotGrey);
        StringAssert.Contains(i.SlotReason, "Open MatchTimeBase to edit it");
    }

    [TestMethod]
    public void MarginAnimatedSlot_DangerWarning_MentionsTheSlideIn()
    {
        var (tree, box) = Find(MatchTime, "Img_MatchTimeBg_1");
        var w = Controller().Inspect(tree, box).Warnings.Single(x => x.Kind == WarningKind.SlotMarginAnimation);
        Assert.AreEqual(Severity.Danger, w.Severity);
        StringAssert.Contains(w.Message, "may not show while they play, e.g. during the kick-off slide-in");
    }

    // ---- edits ----

    [TestMethod]
    public void Apply_FirstEditAsks_ThenAppliesAndShowsInEveryEmbeddingPackage()
    {
        var c = Controller();
        var (tree, plate) = Find(Base, "Img_Plate");
        var first = c.Apply(tree, plate, e => SlotEditor.SetField(plate, e, Right, 100));
        Assert.IsTrue(first.NeedsConfirmation);
        StringAssert.Contains(first.ConfirmationText, "MatchTimeGoal");
        Assert.AreEqual(0, c.Edits.Count, "nothing applied before confirming");

        Assert.IsTrue(c.Apply(tree, plate, e => SlotEditor.SetField(plate, e, Right, 100), confirmed: true).Applied);
        var again = c.Apply(tree, plate, e => SlotEditor.SetField(plate, e, Right, 110));
        Assert.IsTrue(again.Applied, "asked once per package");
        Assert.AreEqual(110.0, c.Inspect(tree, plate).Fields.Single(f => f.Path == Right).EditedValue);
        Assert.AreEqual("edited", c.Inspect(tree, plate).Fields.Single(f => f.Path == Right).State);

        // the edit to the shared child shows inside the offline board (preview uses Edits)
        var board = WidgetTreeReader.Build(Source, MatchTime);
        var layout = Engine.Arrange(board, 1786, 1000, c.Edits);
        var boardPlate = layout.All().Single(b => b.Node.Name == "Img_Plate" && UmgEditor.Core.Layout.LayoutExport.PathOf(b).EndsWith("Normal_Display/Img_MatchTimeBg/CanvasPanel_0/Img_Plate"));
        Assert.AreEqual(388.9 - 110 * 0.92593, boardPlate.Bounds.Right, 0.2);
        Assert.IsTrue(c.Impact().Count(i => i.Changes.Count > 0) >= 8);
    }

    [TestMethod]
    public void UndoRedo_IsProjectWide_InEditOrder()
    {
        var c = Controller();
        Confirmed(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 100));
        Confirmed(c, MatchTime, "Img_MatchTimeBg_1", (n, e) => SlotEditor.Move(n, e, 20, 0));
        Assert.AreEqual(2, c.Edits.Count);

        Assert.IsTrue(c.Undo());
        Assert.IsFalse(c.Edits.Values.Any(kv => kv.Key.Package == MatchTime), "the last edit (MatchTime) is undone first");
        Assert.IsTrue(c.Undo());
        Assert.AreEqual(0, c.Edits.Count);
        Assert.IsFalse(c.Undo());
        Assert.IsTrue(c.Redo() && c.Redo());
        Assert.AreEqual(2, c.Edits.Count);
        Assert.IsTrue(c.AnyDirty);
    }

    // ---- 6c: typed values, new projects, discard, save report ----

    [TestMethod]
    public void ParseNumber_InvariantDotDecimal_RefusesTheRest()
    {
        Assert.AreEqual(146.5, EditorController.ParseNumber(" 146.5 ").Value);
        Assert.AreEqual(-20, EditorController.ParseNumber("-20").Value);
        Assert.AreEqual(100, EditorController.ParseNumber("1e2").Value);
        foreach (var (text, why) in new[] { ("", "Type a number"), ("1,5", "decimal separator"), ("abc", "not a number"), ("NaN", "not a number"), ("Infinity", "not a number") })
            StringAssert.Contains(EditorController.ParseNumber(text).Refusal, why, text);
    }

    [TestMethod]
    public void SetValue_TypedValue_AsksFirst_RefusesBadInputAndGreyFields_SkipsNoOps()
    {
        var c = Controller();
        var (tree, plate) = Find(Base, "Img_Plate");
        StringAssert.Contains(c.SetValue(tree, plate, Right, "abc").Refusal, "not a number");
        var first = c.SetValue(tree, plate, Right, "100");
        Assert.IsTrue(first.NeedsConfirmation);
        StringAssert.Contains(first.ConfirmationText, "MatchTimeConnectionGoal");
        Assert.IsTrue(c.SetValue(tree, plate, Right, "100", confirmed: true).Applied);
        Assert.AreEqual(100.0, c.Inspect(tree, plate).Fields.Single(f => f.Path == Right).EditedValue);
        StringAssert.Contains(c.Inspect(tree, plate).Fields.Single(f => f.Path == Right).ToolTip, "Stock value");

        StringAssert.Contains(c.SetValue(tree, plate, "LayoutData.Offsets.Left", "5").Refusal, "not stored");
        StringAssert.Contains(c.SetValue(tree, plate, "bAutoSize", "1").Refusal, "boolean");
        var again = c.SetValue(tree, plate, Right, "100.0");
        Assert.IsFalse(again.Applied || again.NeedsConfirmation || again.Refusal != null, "the same value again is a no-op");
        Assert.IsTrue(c.Undo());
        Assert.IsFalse(c.CanUndo, "the no-op added no undo step");

        // a fresh package: typing its current value doesn't trigger the first-edit prompt
        var (board, box) = Find(MatchTime, "Img_MatchTimeBg_1");
        var stockLeft = box.Slot!.Fields.Single(f => f.Path == "LayoutData.Offsets.Left").Value;
        Assert.IsFalse(c.SetValue(board, box, "LayoutData.Offsets.Left", stockLeft).NeedsConfirmation);
    }

    [TestMethod]
    public void CreateProject_EmptySetUnderEdits_SwitchesToIt_RefusesDuplicatesAndDirty()
    {
        var c = Controller(EditorController.DefaultProject);
        Assert.IsNull(c.CreateProject(" " + _project + " "));
        Assert.AreEqual(_project, c.Project);
        Assert.IsTrue(Directory.Exists(c.ProjectFolder) && c.ProjectFolder.EndsWith(@"edits\" + _project));
        Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(c.ProjectFolder).Count());
        CollectionAssert.Contains(EditorController.Projects(Source.Config).ToList(), _project);
        StringAssert.Contains(c.CreateProject(_project.ToUpperInvariant()), "already exists");
        StringAssert.Contains(c.CreateProject("Assets"), "reserved");
        StringAssert.Contains(c.CreateProject("a/b"), "letters, digits");

        Confirmed(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 100));
        StringAssert.Contains(c.CreateProject(_project + "x"), "unsaved edits in MatchTimeBase");
        Assert.IsFalse(Directory.Exists(Path.Combine(Edits.Root, _project + "x")));
    }

    [TestMethod]
    public void DiscardUnsaved_BackToTheLastSave_AndUndoBringsTheEditsBack()
    {
        var c = Controller();
        Confirmed(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 100));
        Assert.IsTrue(c.SaveAll().Single().Result.Saved);
        Assert.IsFalse(c.DiscardUnsaved(), "nothing unsaved");
        Confirmed(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 110));
        Confirmed(c, MatchTime, "Img_MatchTimeBg_1", (n, e) => SlotEditor.Move(n, e, 20, 0));
        Assert.IsTrue(c.IsDirty(Base) && c.IsDirty(MatchTime));
        Assert.AreEqual("MatchTimeBase, MatchTime", c.UnsavedPackagesText);

        Assert.IsTrue(c.DiscardUnsaved(Base));
        Assert.IsFalse(c.IsDirty(Base));
        Assert.IsTrue(c.IsDirty(MatchTime), "only that package");
        var (tree, plate) = Find(Base, "Img_Plate");
        Assert.AreEqual(100.0, c.Inspect(tree, plate).Fields.Single(f => f.Path == Right).EditedValue, "the saved value, not stock");
        Assert.AreEqual(Base, c.NextUndo);
        Assert.IsTrue(c.Undo());
        Assert.AreEqual(110.0, c.Inspect(tree, plate).Fields.Single(f => f.Path == Right).EditedValue);

        Assert.IsTrue(c.DiscardUnsaved());
        Assert.IsFalse(c.AnyDirty);
        Assert.IsFalse(c.Edits.Values.Any(kv => kv.Key.Package == MatchTime), "MatchTime had no save: back to stock");
    }

    [TestMethod]
    public void SaveReport_NamesEachPackage_AndEveryRefusalRule()
    {
        var c = Controller();
        Confirmed(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 100));
        Confirmed(c, MatchTime, "Img_MatchTimeBg_1", (n, e) => SlotEditor.Move(n, e, 20, 0));
        c.Session(MatchTime).BeforeVerify = b => [.. b, 0];
        var results = c.SaveAll();
        var report = EditorController.SaveReport(results);
        Console.WriteLine(report);
        StringAssert.StartsWith(report, "1 of 2 packages were refused");
        StringAssert.Contains(report, "MatchTimeBase: saved 1 value");
        StringAssert.Contains(report, "MatchTime: REFUSED");
        StringAssert.Contains(report, "[SizeChanged]");
        Assert.IsTrue(c.IsDirty(MatchTime) && !c.IsDirty(Base));
        Assert.AreEqual("Nothing to save.", EditorController.SaveReport([]));

        c.Session(MatchTime).BeforeVerify = null;
        Assert.AreEqual(0, c.SavePackage(Base).Count, "nothing unsaved there");
        Assert.IsTrue(c.SavePackage(MatchTime).Single().Result.Saved);
        Assert.IsFalse(c.AnyDirty);
    }

    [TestMethod]
    public void BuildMod_RefusedWhileDirty_ThenPacksEverySavedPackage()
    {
        var c = Controller();
        Confirmed(c, Base, "Img_Plate", (n, e) => SlotEditor.SetField(n, e, Right, 100));
        Confirmed(c, MatchTime, "Img_MatchTimeBg_1", (n, e) => SlotEditor.Move(n, e, 20, 0));
        var retoc = new RetocProcess(Source.Config.RetocPath);
        StringAssert.Contains(c.BuildMod("test-" + _project, "pc9999_mobile_and_P", retoc).Refusals.Single(), "unsaved");
        Assert.IsTrue(c.SaveAll().All(r => r.Result.Saved));
        var built = c.BuildMod("test-" + _project, "pc9999_mobile_and_P", retoc);
        Assert.IsTrue(built.Built, string.Join(" | ", built.Refusals));
        StringAssert.Contains(built.Log, "2 packages");
    }
}
