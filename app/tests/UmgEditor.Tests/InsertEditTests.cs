using System.Security.Cryptography;
using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Branch experiment, step 3d: values that aren't stored are editable in the editor and umgmod, and saving adds them as
/// new tags (TagInserter, device-tested by canary C10). Each test uses its own folder under work\edits\_tests or its own
/// project (work\edits\zztest&lt;id&gt;) and deletes it.
/// </summary>
[TestClass]
public class InsertEditTests
{
    private const string MatchTime = Tv + "MatchTime/MatchTime";
    private const string Base = Tv + "MatchTime/MatchTimeBase";
    private const string Left = "LayoutData.Offsets.Left", Top = "LayoutData.Offsets.Top", Right = "LayoutData.Offsets.Right";
    /// <summary>The chunk canary C10 shipped (work\out\mods\C10\build.json), which loaded on the device.</summary>
    private const string C10ChunkSha256 = "69fde3309e907969b6dea25f29a20d42f6c1cf1e2d3c1c86306bd8cdf27473e2";

    private static WriteGuard Guard => new(Source.Config);
    private string _folder = null!, _project = null!;

    [TestInitialize]
    public void NewFolder()
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        _folder = Path.Combine(Guard.Root, "_tests", id);
        _project = "zztest" + id;
    }

    [TestCleanup]
    public void Cleanup()
    {
        Guard.DeleteDirectory(_folder);
        var parent = Path.GetDirectoryName(_folder)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Guard.DeleteDirectory(parent);
        Guard.DeleteDirectory(Path.Combine(Guard.Root, _project));
    }

    private PatchSession Open(string package = MatchTime) => PatchSession.Open(Source, package, _folder);
    private static WidgetPackageTree Tree(string package) => WidgetTreeReader.Build(Source, package);
    private static void Apply(PatchSession s, EditResult edit) => Assert.IsNull(s.Apply(edit));
    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private EditorController Controller() =>
        new(Source, new UsageGraph(UiIndex), Engine, p => WidgetTreeReader.Build(Source, p), project: _project);

    // ---- the save path ----

    [TestMethod]
    public void Save_InsertedLeft20_IsByteIdenticalToTheDeviceTestedC10Chunk()
    {
        var s = Open();
        var home = Tree(MatchTime).Find("Text_Score_Home")!;
        Assert.IsTrue(SlotEditor.IsInsertion(home, Left));
        Apply(s, SlotEditor.SetField(home, s.Current, Left, 20));

        var plan = s.Plan();
        Assert.IsTrue(plan.Ok, string.Join(" | ", plan.Refusals.Select(r => r.Message)));
        Assert.AreEqual(0, plan.Entries.Count);
        Assert.AreEqual(1, plan.Insertions.Count);
        Assert.AreEqual("Right", plan.Insertions[0].Before);
        Assert.AreEqual(1, plan.Insertions[0].ContextMatches);
        Assert.AreEqual(s.StockSize + 29, plan.EditedSize);

        var saved = s.Save();
        Assert.IsTrue(saved.Saved, saved.Summary);
        Console.WriteLine(saved.Summary);
        StringAssert.Contains(saved.Summary, "(1 as new tag)");
        StringAssert.Contains(saved.Summary, $"size {s.StockSize} → {s.StockSize + 29}");
        var file = File.ReadAllBytes(s.EditedPath);
        Assert.AreEqual(C10ChunkSha256, Sha(file));
        // header writes (2 struct sizes, the export's size, 1,461 later offsets) differ; the inserted tag is cut out first
        Assert.AreEqual(plan.ExpectedChangedOffsets.Count, saved.ChangedOffsets.Count);

        var (chunk, why) = s.ReadSavedChunk();
        Assert.IsNull(why);
        Assert.AreEqual(C10ChunkSha256, Sha(chunk!));
    }

    [TestMethod]
    public void Save_TwoTagsInOneStruct_AnotherSlot_AndAStoredValue_AllReadBack()
    {
        var s = Open();
        var tree = Tree(MatchTime);
        var home = tree.Find("Text_Score_Home")!;
        // another slot with a missing Offsets member in a stored Offsets struct
        var other = tree.All().First(n => n != home && !n.IsReadOnly && n.Slot?.ClassName == SlotEditor.CanvasSlot
            && n.Slot.Fields.Any(f => f.Path == Top && !f.Stored) && SlotEditor.IsInsertion(n, Top));
        Apply(s, SlotEditor.SetField(home, s.Current, Top, -3));
        Apply(s, SlotEditor.SetField(home, s.Current, Left, 12.5));
        Apply(s, SlotEditor.SetField(home, s.Current, Right, 60));
        Apply(s, SlotEditor.SetField(other, s.Current, Top, 7));

        var plan = s.Plan();
        Assert.IsTrue(plan.Ok, string.Join(" | ", plan.Refusals.Select(r => r.Message)));
        Assert.AreEqual(1, plan.Entries.Count, "Right is stored: patched in place");
        Assert.AreEqual(3, plan.Insertions.Count);
        CollectionAssert.AreEqual(new[] { "LayoutData.Offsets.Left", "LayoutData.Offsets.Top" },
            plan.Insertions.Where(i => i.ExportIndex == home.Slot!.ExportIndex).Select(i => i.Path).ToArray(), "declaration order at the same insertion point");

        var saved = s.Save();
        Assert.IsTrue(saved.Saved, saved.Summary);
        Console.WriteLine(saved.Summary);
        var bytes = File.ReadAllBytes(s.EditedPath);
        Assert.AreEqual(s.StockSize + 3 * 29, bytes.Length);

        // an independent read: CUE4Parse parses the saved chunk and the tree reader sees the values as stored
        var parsed = Source.ParsePackage(s.FileKey, bytes);
        var homeValues = WidgetTreeReader.StoredValues(parsed.ExportsLazy[home.Slot!.ExportIndex].Value);
        Assert.AreEqual(12.5, EditSet.Parse(homeValues[Left]));
        Assert.AreEqual(-3, EditSet.Parse(homeValues[Top]));
        Assert.AreEqual(60, EditSet.Parse(homeValues[Right]));
        Assert.AreEqual(7, EditSet.Parse(WidgetTreeReader.StoredValues(parsed.ExportsLazy[other.Slot!.ExportIndex].Value)[Top]));

        // the same edits planned again give the same bytes (rebuilt from stock, not from the previous save)
        Assert.IsTrue(s.Undo() && s.Redo());
        Assert.IsTrue(s.Save().Saved);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(s.EditedPath));
    }

    [TestMethod]
    public void Edit_BackToTheDefault_RemovesTheInsertion()
    {
        var s = Open();
        var home = Tree(MatchTime).Find("Text_Score_Home")!;
        Assert.AreEqual(0, s.BaseValue(FieldKey.Of(home, Left)));
        Assert.IsNull(s.StockValue(FieldKey.Of(home, Left)));
        Apply(s, SlotEditor.SetField(home, s.Current, Left, 20));
        Assert.AreEqual(1, s.Current.Count);
        Apply(s, SlotEditor.SetField(home, s.Current, Left, 0));
        Assert.AreEqual(0, s.Current.Count, "the default typed again leaves the field out");
        Assert.IsFalse(s.Plan().ChangesSize);
    }

    [TestMethod]
    public void Verify_RefusesATagThatIsntWherePlanned_AndAWrongSize()
    {
        var s = Open();
        var home = Tree(MatchTime).Find("Text_Score_Home")!;
        Apply(s, SlotEditor.SetField(home, s.Current, Left, 20));
        var plan = s.Plan();
        var stock = Source.ReadPackageBytes(MatchTime).Bytes;
        var good = BytePlanner.Apply(stock, plan);
        Assert.AreEqual(0, BytePlanner.Verify(stock, good, plan).Count);

        var shifted = (byte[]) good.Clone();
        shifted[plan.Insertions[0].InsertAt + 25] ^= 1; // the inserted float's first byte
        Assert.IsTrue(BytePlanner.Verify(stock, shifted, plan).Any(r => r.Rule == RefusalRule.UnexpectedByteDiff));
        var header = (byte[]) good.Clone();
        header[plan.Derived.First(d => d.Kind == DerivedKind.ExportSize).Offset] ^= 1; // an export size not as planned
        Assert.IsTrue(BytePlanner.Verify(stock, header, plan).Any(r => r.Rule == RefusalRule.UnexpectedByteDiff));
        Assert.IsTrue(BytePlanner.Verify(stock, stock, plan).Single().Rule == RefusalRule.SizeChanged);
    }

    // ---- what stays refused ----

    [TestMethod]
    public void NotInsertable_AnchorsAlignmentZOrder_AndAMissingOffsetsStruct_SayWhy()
    {
        var census = Census();
        var (anchors, _) = census.First(x => x.Path.StartsWith("LayoutData.Anchors"));
        StringAssert.Contains(SlotEditor.FieldRefusal(anchors, anchors.Slot!.Fields.First(f => !f.Stored && f.Path.StartsWith("LayoutData.Anchors")).Path), "adding a whole struct isn't supported yet");
        if (census.FirstOrDefault(x => x.Path == "ZOrder") is { Node: not null } z)
            StringAssert.Contains(SlotEditor.FieldRefusal(z.Node, "ZOrder"), "new top-level tag");
        if (census.FirstOrDefault(x => x.Path.StartsWith("LayoutData.Offsets") && SlotEditor.FieldRefusal(x.Node, x.Path) != null) is { Node: not null } o)
            StringAssert.Contains(SlotEditor.FieldRefusal(o.Node, o.Path), "whole Offsets struct");

        // a forged edit of a field that can't be added is refused by the session too
        var s = Open(anchors.PackagePath);
        var path = anchors.Slot!.Fields.First(f => !f.Stored && f.Path.StartsWith("LayoutData.Anchors")).Path;
        StringAssert.Contains(s.Apply(EditResult.Of([new FieldChange(FieldKey.Of(anchors, path), 0, 0.5)])), "only a missing Offsets value can be added");
        Assert.AreEqual(0, s.Current.Count);
    }

    /// <summary>Every not-stored CanvasPanelSlot value of the MatchTime family, and how many of them can be added now.</summary>
    private static List<(WidgetNode Node, string Path)> Census()
    {
        var all = new List<(WidgetNode, string)>();
        foreach (var package in UiIndex.Where(e => e.Kind == UiAssetKind.Widget).Select(e => GamePaths.ToPackagePath(e.Path))
                     .Where(p => p.StartsWith(Tv + "MatchTime/", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
            foreach (var n in Tree(package).All().Where(n => !n.IsReadOnly && n.Slot?.ClassName == SlotEditor.CanvasSlot && !GamePaths.IsLibrary(n.PackagePath)))
                all.AddRange(n.Slot!.Fields.Where(f => !f.Stored && f.Path != "bAutoSize").Select(f => (n, f.Path)));
        return all;
    }

    [TestMethod]
    public void Census_MatchTimeFamily_NotStoredValues()
    {
        var census = Census();
        var groups = census.GroupBy(x => (Field: x.Path.Replace("LayoutData.", ""), Editable: SlotEditor.FieldRefusal(x.Node, x.Path) == null))
            .OrderBy(g => g.Key.Field).Select(g => $"{g.Key.Field,-22} {(g.Key.Editable ? "editable" : "refused "),-9} {g.Count(),5}").ToList();
        Console.WriteLine($"{census.Count} not-stored CanvasPanelSlot values in the MatchTime family:\n" + string.Join("\n", groups));
        Assert.IsTrue(census.Any(x => SlotEditor.IsInsertion(x.Node, x.Path)));
        Assert.IsTrue(census.All(x => SlotEditor.IsInsertion(x.Node, x.Path) == (TagInserter.Insertable(x.Path) != null && SlotEditor.FieldRefusal(x.Node, x.Path) == null)));
    }

    // ---- controller, bytes panel, export ----

    [TestMethod]
    public void Controller_NotStoredLeft_EditableWithASizeWarning_InBytesAndExport()
    {
        var c = Controller();
        var tree = Tree(MatchTime);
        var home = tree.Find("Text_Score_Home")!;
        var field = c.Inspect(tree, home).Fields.Single(f => f.Path == Left);
        Assert.IsTrue(field.Editable && field.IsInsertion && !field.IsEdited);
        Assert.AreEqual("default", field.State);
        StringAssert.Contains(field.ToolTip, "29-byte tag");

        var first = c.SetValue(tree, home, Left, "20");
        Assert.IsTrue(first.NeedsConfirmation);
        var applied = c.SetValue(tree, home, Left, "20", confirmed: true);
        Assert.IsTrue(applied.Applied, applied.Refusal);
        var warning = applied.PendingWarnings.Single(w => w.Kind == WarningKind.SizeChange);
        Assert.AreEqual(Severity.Warning, warning.Severity);
        StringAssert.Contains(warning.Message, "Offsets.Left isn't stored in MatchTime");
        Assert.AreEqual("new tag", c.Inspect(tree, home).Fields.Single(f => f.Path == Left).State);
        Assert.IsFalse(c.SetValue(tree, home, Right, "60").PendingWarnings.Any(w => w.Kind == WarningKind.SizeChange), "a stored value adds no tag");

        var bytes = c.BytesToPatch();
        var pkg = bytes.Packages.Single();
        Assert.AreEqual(1, pkg.Rows.Count);
        var row = pkg.Insertions.Single();
        Assert.AreEqual("Offsets.Left", row.Field);
        Assert.AreEqual("default 0 → 20", row.ValueText);
        StringAssert.Contains(row.Slot, "Text_Score_Home");
        StringAssert.Contains(pkg.Header, "(+29: 1 new tag)");
        StringAssert.Contains(pkg.HeaderChanges, "CookedSerialSize 271 → 300");
        StringAssert.Contains(pkg.HeaderChanges, "1,461 later exports' CookedSerialOffset +29");
        Assert.IsTrue(row.Context.Any(l => l.Contains('|')), string.Join("\n", row.Context));
        Assert.AreEqual(2, bytes.ValueCount);
        StringAssert.Contains(bytes.Summary, "1 value that isn't stored is added as a new 29-byte tag");
        StringAssert.Contains(bytes.ToText(), "new float tag (29 bytes)");

        var save = c.SaveAll();
        Assert.IsTrue(save.Single().Result.Saved, EditorController.SaveReport(save));
        StringAssert.Contains(EditorController.SaveStatus(save), "1 new tag: +29 bytes");

        var plan = c.PlanExport(new ExportRequest("test-" + _project, "pc9999_mobile_and_P", [MatchTime], Source.Config.RetocPath));
        Assert.AreEqual(2, plan.Edits.Count, string.Join("\n", plan.Edits));
        var inserted = plan.Edits.Single(e => e.Field.EndsWith("(new tag)"));
        Assert.AreEqual("(not stored)", inserted.OldHex);
        Assert.AreEqual(20, inserted.Value);
        Assert.AreEqual(Source.ReadPackageBytes(MatchTime).Bytes.Length + 29, plan.Packages.Single().Chunk.Length);

        // undo back to stock, then discard brings the saved insertion back
        Assert.IsTrue(c.Undo() && c.Undo());
        Assert.AreEqual(0, c.Edits.Count);
        Assert.IsTrue(c.DiscardUnsaved());
        Assert.AreEqual(2, c.Edits.Count);
    }
}
