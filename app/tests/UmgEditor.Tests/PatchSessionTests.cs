using System.Buffers.Binary;
using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Step 4: PatchSession (working copies under work\edits, byte plan, save with refusals, reload-and-diff, undo/redo,
/// dirty state). Every test works in its own folder under work\edits\_tests and deletes it afterwards.
/// </summary>
[TestClass]
public class PatchSessionTests
{
    private const string Base = Tv + "MatchTime/MatchTimeBase";
    private const string MatchTime = Tv + "MatchTime/MatchTime";
    private const string Right = "LayoutData.Offsets.Right";
    private const int C3Offset = 0xA64;

    private static WriteGuard Guard => new(Source.Config);
    private string _folder = null!;

    [TestInitialize]
    public void NewFolder() => _folder = Path.Combine(Guard.Root, "_tests", Guid.NewGuid().ToString("N")[..12]);

    [TestCleanup]
    public void DeleteFolder()
    {
        Guard.DeleteDirectory(_folder);
        var parent = Path.GetDirectoryName(_folder)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Guard.DeleteDirectory(parent);
    }

    private PatchSession Open(string package = Base) => PatchSession.Open(Source, package, _folder);

    private static WidgetNode Node(string package, string name) => WidgetTreeReader.Build(Source, package).Find(name)!;

    private static void Apply(PatchSession s, EditResult edit) => Assert.IsNull(s.Apply(edit));

    private static EditResult Set(PatchSession s, WidgetNode node, string path, double value) => SlotEditor.SetField(node, s.Current, path, value);

    private static byte[] StockBytes(string package) => Source.ReadPackageBytes(package).Bytes;

    private static void AssertRefused(SaveResult r, RefusalRule rule, string? contains = null)
    {
        Assert.IsFalse(r.Saved, "save must be refused");
        Assert.IsTrue(r.Refusals.Any(x => x.Rule == rule && (contains == null || x.Message.Contains(contains))),
            $"expected a {rule} refusal{(contains != null ? $" containing \"{contains}\"" : "")}; got: {r.Summary}");
        Console.WriteLine(r.Summary);
    }

    // ---- path guard ----

    [TestMethod]
    public void PathGuard_RejectsEveryPathUnderThePaksFolder()
    {
        var guard = Guard;
        var paks = Source.Config.PaksDir;
        var before = Directory.GetFileSystemEntries(paks).Order().ToList();
        string[] rejected =
        [
            paks,
            Path.Combine(paks, "pc9999_mobile_and_P.utoc"),
            Path.Combine(paks, "sub", "..", "pc9999_mobile_and_P.pak"),
            Path.Combine(guard.Root, Path.GetRelativePath(guard.Root, paks), "pc9999_mobile_and_P.ucas"), // ..\..\ traversal from edits
            paks.ToUpperInvariant() + @"\x.bin",
        ];
        foreach (var p in rejected)
            StringAssert.Contains(guard.Refusal(p), "under the game Paks folder", p);

        // the root itself, siblings of edits, and other work folders are refused too; inside edits is fine
        Assert.IsNotNull(guard.Refusal(guard.Root));
        Assert.IsNotNull(guard.Refusal(Path.Combine(Source.Config.WorkDir, "out", "x.bin")));
        Assert.IsNotNull(guard.Refusal(guard.Root + "2\\x.bin"));
        Assert.IsNull(guard.Refusal(Path.Combine(guard.Root, "a", "b.uasset")));

        // a WorkDir configured inside Paks can't be used to write there either
        var inside = new WriteGuard(Source.Config with { WorkDir = Path.Combine(paks, "work") });
        StringAssert.Contains(inside.Refusal(Path.Combine(inside.Root, "x.bin")), "under the game Paks folder");

        Assert.ThrowsException<UnauthorizedAccessException>(() => guard.WriteAllBytes(Path.Combine(paks, "pc9999_mobile_and_P.utoc"), [1]));
        Assert.ThrowsException<UnauthorizedAccessException>(() => PatchSession.Open(Source, Base, Path.Combine(paks, "edits")));
        CollectionAssert.AreEqual(before, Directory.GetFileSystemEntries(paks).Order().ToList(), "nothing was created in Paks");
    }

    // ---- working copy and byte plan ----

    [TestMethod]
    public void Open_WritesAVerifiedStockCopy_UnderWorkEdits()
    {
        var s = Open();
        Assert.IsTrue(s.StockCopyPath.StartsWith(Guard.Root + "\\", StringComparison.OrdinalIgnoreCase));
        CollectionAssert.AreEqual(StockBytes(Base), File.ReadAllBytes(s.StockCopyPath));
        Assert.IsFalse(s.IsDirty);
        Assert.IsFalse(s.CanUndo || s.CanRedo);
        Assert.IsFalse(File.Exists(s.EditedPath));
        Assert.AreEqual(24, s.ChunkIdHex.Length);
        Assert.IsTrue(s.ChunkIdHex.EndsWith("0002"), "ExportBundleData chunk");

        // the default folder mirrors the package path below /Game/
        StringAssert.EndsWith(PatchSession.DefaultFolder(Guard, Base), @"edits\Assets\ui\Data\Widget\TVGraphics\SwitchAssets_Original\MatchTime\MatchTimeBase");
    }

    [TestMethod]
    public void Plan_C3_ShowsOffsetValuesHexAndContext()
    {
        var s = Open();
        Apply(s, Set(s, Node(Base, "Img_Plate"), Right, 100));
        var plan = s.Plan();
        Assert.IsTrue(plan.Ok, string.Join("; ", plan.Refusals.Select(r => r.Message)));
        var e = plan.Entries.Single();
        var stock = StockBytes(Base);
        Assert.AreEqual("CanvasPanelSlot_1", e.SlotName);
        Assert.AreEqual(Right, e.Key.Path);
        Assert.AreEqual(C3Offset, e.Offset);
        Assert.AreEqual("0xA64", e.OffsetHex);
        Assert.AreEqual(FieldType.Float, e.Type);
        Assert.AreEqual(0.0, e.OldValue);
        Assert.AreEqual(100.0, e.NewValue);
        Assert.AreEqual("00 00 00 00", e.OldHex);
        Assert.AreEqual("00 00 C8 42", e.NewHex);
        CollectionAssert.AreEqual(stock[(C3Offset - 32)..C3Offset], e.ContextBefore);
        CollectionAssert.AreEqual(stock[(C3Offset + 4)..(C3Offset + 36)], e.ContextAfter);
        Assert.AreEqual(1, e.ContextMatches);
        CollectionAssert.AreEqual(new[] { C3Offset + 2, C3Offset + 3 }, plan.ExpectedChangedOffsets.ToArray());
        Console.WriteLine($"{e.SlotName}.{e.Key.Path} @ {e.OffsetHex}: {e.OldValue} → {e.NewValue}, {e.OldHex} → {e.NewHex}\n  context {e.ContextHex}");

        var saved = s.Save();
        Assert.IsTrue(saved.Saved, saved.Summary);
        var written = File.ReadAllBytes(s.EditedPath);
        Assert.AreEqual(stock.Length, written.Length);
        CollectionAssert.AreEqual(new[] { C3Offset + 2, C3Offset + 3 }, BytePlanner.Diff(stock, written));
        Assert.AreEqual(100f, BinaryPrimitives.ReadSingleLittleEndian(written.AsSpan(C3Offset)));
        // an independent re-parse of the written file sees the new value
        var reparsed = Source.ParsePackage(s.FileKey, written);
        Assert.AreEqual("100", WidgetTreeReader.StoredValues(reparsed.ExportsLazy[e.Key.SlotExport].Value)[Right]);
        StringAssert.Contains(File.ReadAllText(s.ManifestPath), "\"OffsetHex\": \"0xA64\"");
        Console.WriteLine(saved.Summary);
    }

    /// <summary>The session must produce exactly the chunks the canary tool built (and the game loaded).</summary>
    [TestMethod]
    public void Save_ReproducesBothInGameCanaryChunks()
    {
        foreach (var (dir, values) in new[] { ("canary", (Left: 829.0, Top: 407.0)), ("canary2", (Left: 249.0, Top: 7.0)) })
        {
            var reference = Path.Combine(Source.Config.WorkDir, "out", dir, "raw", "chunks", "4ea5d5c18de5157900000002");
            if (!File.Exists(reference)) Assert.Inconclusive($"{reference} not found");
            Guard.DeleteDirectory(_folder);
            var s = Open(MatchTime);
            Assert.AreEqual("4ea5d5c18de5157900000002", s.ChunkIdHex);
            var mask = Node(MatchTime, "Mask");
            Apply(s, Set(s, mask, "LayoutData.Offsets.Left", values.Left));
            Apply(s, Set(s, mask, "LayoutData.Offsets.Top", values.Top));
            var r = s.Save();
            Assert.IsTrue(r.Saved, r.Summary);
            CollectionAssert.AreEqual(File.ReadAllBytes(reference), File.ReadAllBytes(s.EditedPath), $"{dir}: saved chunk differs from the canary chunk");
            Console.WriteLine($"{dir}: {r.Summary}; identical to {reference}");
        }
    }

    // ---- refusals ----

    /// <summary>A copy of the stock chunk with the C3 field's ±32-byte window also written into another export.</summary>
    /// <summary>The stock chunk with C3's ±<paramref name="half"/> window (around Img_Plate's Offsets.Right) copied somewhere else.</summary>
    private static (byte[] Chunk, ZenPackageLayout Layout, int CopyAt) DuplicateC3Context(int half = 32)
    {
        var (layout, _, chunk) = Layout(Base);
        var window = chunk[(C3Offset - half)..(C3Offset + 4 + half)];
        var others = layout.Exports.Where(x => x.Size >= window.Length + 8 && (x.End <= C3Offset - half || x.Start >= C3Offset + 4 + half)).ToList();
        // a window larger than any other export goes over the start of the chunk (planning reads only the slot's export)
        var at = others.Count > 0 ? others.MaxBy(x => x.Size).Start + 4 : C3Offset > chunk.Length / 2 ? 0 : chunk.Length - window.Length;
        Assert.IsTrue(at + window.Length <= C3Offset - half || at >= C3Offset + 4 + half, "the copy must not overlap the original");
        window.CopyTo(chunk, at);
        return (chunk, layout, at);
    }

    private static EditSet C3Edit(out Func<FieldKey, double?> stockValue)
    {
        var plate = Node(Base, "Img_Plate");
        var stock = EditSet.Parse(plate.Slot!.Fields.Single(f => f.Path == Right).Value);
        stockValue = k => k.Path == Right ? stock : null;
        return EditSet.Empty.With(SlotEditor.SetField(plate, EditSet.Empty, Right, 100).Changes, _ => null);
    }

    [TestMethod]
    public void Accepts_RepeatedPm32Context_WithTheSmallestUniqueWindow()
    {
        // ±32 occurs twice (rule since C12): the location is proven by the smallest unique stock window instead
        var (chunk, layout, at) = DuplicateC3Context(32);
        var plan = BytePlanner.Plan(chunk, layout, Base, C3Edit(out var stock), stock);
        Assert.IsTrue(plan.Ok, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        var e = plan.Entries.Single();
        Assert.AreEqual(2, e.ContextMatches);
        Assert.IsTrue(e.Window is > BytePlanner.Context and <= BytePlanner.MaxAnimWindow, $"window ±{e.Window}");
        Assert.AreEqual(1, BytePlanner.CountOccurrences(chunk, chunk[(C3Offset - e.Window)..(C3Offset + 4 + e.Window)]));
        Console.WriteLine($"window copied to 0x{at:X}: unique at ±{e.Window}");
    }

    [TestMethod]
    public void Refuses_ContextNotUniqueWithin512()
    {
        var (chunk, layout, at) = DuplicateC3Context(BytePlanner.MaxAnimWindow + 16);
        var plan = BytePlanner.Plan(chunk, layout, Base, C3Edit(out var stock), stock);
        Assert.IsFalse(plan.Ok);
        Assert.AreEqual(0, plan.Entries.Count);
        var r = plan.Refusals.Single();
        Assert.AreEqual(RefusalRule.ContextNotUnique, r.Rule);
        StringAssert.Contains(r.Message, "matches 2 times");
        StringAssert.Contains(r.Message, $"no window up to ±{BytePlanner.MaxAnimWindow} bytes occurs only once");
        Console.WriteLine($"window copied to 0x{at:X}: {r.Message}");

        // the same through a session whose stock copy was altered that way: refused, nothing written
        var s = Open();
        File.WriteAllBytes(s.StockCopyPath, chunk);
        Apply(s, Set(s, Node(Base, "Img_Plate"), Right, 100));
        var save = s.Save();
        AssertRefused(save, RefusalRule.ContextNotUnique);
        AssertRefused(save, RefusalRule.StockCopyChanged);
        Assert.IsFalse(File.Exists(s.EditedPath) || File.Exists(s.EditedPath + ".tmp"));
    }

    [TestMethod]
    public void Refuses_StockBytesMismatch()
    {
        var (layout, _, chunk) = Layout(Base);
        BinaryPrimitives.WriteSingleLittleEndian(chunk.AsSpan(C3Offset), 5f);
        var plan = BytePlanner.Plan(chunk, layout, Base, C3Edit(out var stock), stock);
        var r = plan.Refusals.Single();
        Assert.AreEqual(RefusalRule.StockBytesMismatch, r.Rule);
        StringAssert.Contains(r.Message, "00 00 A0 40");

        var s = Open();
        File.WriteAllBytes(s.StockCopyPath, chunk);
        Apply(s, Set(s, Node(Base, "Img_Plate"), Right, 100));
        AssertRefused(s.Save(), RefusalRule.StockBytesMismatch, "0xA64");
        Assert.IsFalse(File.Exists(s.EditedPath));
        Assert.IsTrue(s.IsDirty, "a refused save leaves the session dirty");
    }

    [TestMethod]
    public void Refuses_SizeChange_AndKeepsThePreviousSave()
    {
        var s = Open();
        Apply(s, Set(s, Node(Base, "Img_Plate"), Right, 100));
        var first = s.Save();
        Assert.IsTrue(first.Saved, first.Summary);

        Apply(s, Set(s, Node(Base, "Img_Plate"), Right, 120));
        s.BeforeVerify = b => [.. b, 0];
        AssertRefused(s.Save(), RefusalRule.SizeChanged, $"is {StockBytes(Base).Length + 1} bytes");
        s.BeforeVerify = b => b[..^1];
        AssertRefused(s.Save(), RefusalRule.SizeChanged);

        Assert.AreEqual(first.Sha256, PatchSession.Sha256(File.ReadAllBytes(s.EditedPath)), "previous save untouched");
        Assert.IsFalse(File.Exists(s.EditedPath + ".tmp"));
        Assert.IsTrue(s.IsDirty);
    }

    [TestMethod]
    public void Refuses_InjectedExtraByte()
    {
        var s = Open();
        Apply(s, Set(s, Node(Base, "Img_Plate"), Right, 100));
        foreach (var at in new[] { 100, C3Offset + 4, C3Offset - 1 }) // header area, and the bytes right after/before the value
        {
            s.BeforeVerify = b => { b[at] ^= 0x01; return b; };
            AssertRefused(s.Save(), RefusalRule.UnexpectedByteDiff, $"0x{at:X}");
            Assert.IsFalse(File.Exists(s.EditedPath) || File.Exists(s.EditedPath + ".tmp"));
        }
        // a planned byte that doesn't change is refused too
        s.BeforeVerify = b => { b[C3Offset + 3] = 0; return b; };
        AssertRefused(s.Save(), RefusalRule.UnexpectedByteDiff, "didn't change");

        s.BeforeVerify = null;
        Assert.IsTrue(s.Save().Saved);
    }

    // ---- undo / redo / dirty ----

    [TestMethod]
    public void UndoToZero_ThenSave_IsByteIdenticalToStock()
    {
        var s = Open(MatchTime);
        var mask = Node(MatchTime, "Mask");
        var plate = Node(MatchTime, "Img_MatchTimeBg_1");
        Apply(s, Set(s, mask, "LayoutData.Offsets.Left", 249));
        Apply(s, SlotEditor.Move(plate, s.Current, 20, 0));
        Apply(s, SlotEditor.SetField(plate, s.Current, Right, 150));
        Assert.IsTrue(s.Save().Saved);
        Assert.AreNotEqual(s.StockSha256, PatchSession.Sha256(File.ReadAllBytes(s.EditedPath)));

        while (s.Undo()) { }
        Assert.AreEqual(0, s.Current.Count);
        Assert.IsTrue(s.IsDirty, "undone edits differ from the save");
        var r = s.Save();
        Assert.IsTrue(r.Saved, r.Summary);
        Assert.AreEqual(0, r.ChangedOffsets.Count);
        CollectionAssert.AreEqual(StockBytes(MatchTime), File.ReadAllBytes(s.EditedPath));
        Assert.AreEqual(s.StockSha256, r.Sha256);
        Assert.IsFalse(s.IsDirty);
    }

    [TestMethod]
    public void Redo_ReproducesTheEarlierSave()
    {
        var s = Open(MatchTime);
        var mask = Node(MatchTime, "Mask");
        Apply(s, Set(s, mask, "LayoutData.Offsets.Left", 249));
        Apply(s, Set(s, mask, "LayoutData.Offsets.Top", 20));
        Apply(s, SlotEditor.ResizeEdge(Node(MatchTime, "Img_MatchTimeBg_1"), s.Current, Edge.Right, 30));
        var first = s.Save();
        Assert.IsTrue(first.Saved, first.Summary);
        var firstBytes = File.ReadAllBytes(s.EditedPath);
        var firstManifest = File.ReadAllText(s.ManifestPath);

        while (s.Undo()) { }
        Assert.IsTrue(s.Save().Saved);
        CollectionAssert.AreEqual(StockBytes(MatchTime), File.ReadAllBytes(s.EditedPath));

        while (s.Redo()) { }
        Assert.IsTrue(s.IsDirty);
        var again = s.Save();
        Assert.IsTrue(again.Saved, again.Summary);
        CollectionAssert.AreEqual(firstBytes, File.ReadAllBytes(s.EditedPath));
        Assert.AreEqual(first.Sha256, again.Sha256);
        CollectionAssert.AreEqual(first.ChangedOffsets.ToArray(), again.ChangedOffsets.ToArray());
        Assert.AreEqual(StripTime(firstManifest), StripTime(File.ReadAllText(s.ManifestPath)));
    }

    private static string StripTime(string manifest) => string.Join("\n", manifest.Split('\n').Where(l => !l.Contains("\"SavedUtc\"")));

    [TestMethod]
    public void DirtyState_FollowsEditsUndoRedoAndSave()
    {
        var s = Open();
        var plate = Node(Base, "Img_Plate");
        var events = 0;
        s.StateChanged += (_, _) => events++;

        Assert.IsFalse(s.IsDirty);
        Apply(s, Set(s, plate, Right, 100));
        Assert.IsTrue(s.IsDirty && s.CanUndo && !s.CanRedo);
        Assert.IsTrue(s.Save().Saved);
        Assert.IsFalse(s.IsDirty);
        Assert.IsTrue(s.Undo());
        Assert.IsTrue(s.IsDirty && s.CanRedo);
        Assert.IsTrue(s.Redo());
        Assert.IsFalse(s.IsDirty, "redo back to the saved edits is clean again");

        // setting the stock value removes the entry: no edits, but the save still has one → dirty
        Apply(s, Set(s, plate, Right, 0));
        Assert.AreEqual(0, s.Current.Count);
        Assert.IsTrue(s.IsDirty);
        // a new edit after undo clears redo
        Assert.IsTrue(s.Undo());
        Apply(s, Set(s, plate, Right, 50));
        Assert.IsFalse(s.CanRedo);
        // no-op edits don't create undo steps
        var undoBefore = s.CanUndo;
        Apply(s, Set(s, plate, Right, 50));
        Assert.AreEqual(undoBefore, s.CanUndo);
        Assert.AreEqual(7, events); // apply, save, undo, redo, apply, undo, apply
    }

    [TestMethod]
    public void Apply_RefusesOtherPackagesAndUnstoredFields()
    {
        var s = Open();
        var mask = Node(MatchTime, "Mask");
        StringAssert.Contains(s.Apply(SlotEditor.SetField(mask, EditSet.Empty, "LayoutData.Offsets.Left", 1)), "belongs to MatchTime");
        var plate = Node(Base, "Img_Plate");
        // a not-stored Offsets member is addable (branch experiment, InsertEditTests); a value outside a stored struct isn't
        var unstored = plate.Slot!.Fields.First(f => !f.Stored && TagInserter.Insertable(f.Path) == null && f.Path != "bAutoSize");
        var forged = EditResult.Of([new FieldChange(FieldKey.Of(plate, unstored.Path), 0, 5)]);
        StringAssert.Contains(s.Apply(forged), "not stored");
        StringAssert.Contains(s.Apply(EditResult.Refused("greyed out")), "greyed out");
        Assert.AreEqual(0, s.Current.Count);
        Assert.IsFalse(s.CanUndo);
    }

    [TestMethod]
    public void Reopen_RestoresSavedEdits_AndFlagsAStaleSave()
    {
        var s = Open();
        Apply(s, Set(s, Node(Base, "Img_Plate"), Right, 100));
        Assert.IsTrue(s.Save().Saved);

        var resumed = Open();
        Assert.IsNull(resumed.OpenNote);
        Assert.IsTrue(resumed.Current.ContentEquals(s.Current));
        Assert.IsFalse(resumed.IsDirty);

        var bytes = File.ReadAllBytes(s.EditedPath);
        bytes[0] ^= 1;
        File.WriteAllBytes(s.EditedPath, bytes);
        var stale = Open();
        Assert.IsNotNull(stale.OpenNote);
        Assert.IsTrue(stale.IsDirty, "the edits are back, but the file on disk isn't their save");
        Assert.IsTrue(stale.Save().Saved);
        CollectionAssert.AreEqual(new[] { C3Offset + 2, C3Offset + 3 }, BytePlanner.Diff(StockBytes(Base), File.ReadAllBytes(s.EditedPath)));
    }

    [TestMethod]
    public void Reopen_RefusesAStockCopyThatNoLongerMatchesTheGame()
    {
        var s = Open();
        var bytes = File.ReadAllBytes(s.StockCopyPath);
        bytes[^1] ^= 1;
        File.WriteAllBytes(s.StockCopyPath, bytes);
        Assert.ThrowsException<InvalidDataException>(() => Open());
    }
}
