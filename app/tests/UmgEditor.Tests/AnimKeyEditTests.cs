using System.Text.Json;
using UmgEditor.Core;
using UmgEditor.Core.Animation;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Branch experiment, canary C11: animation key edits through the ordinary edit set (both copies of every key), the
/// animation-keys-only location rule (smallest unique stock window, refused beyond ±512), and the hard checks (exact
/// byte diff, full re-parse). Each test uses its own folder under work\edits\_tests or its own project and deletes it.
/// </summary>
[TestClass]
public class AnimKeyEditTests
{
    private const string MatchTime = Tv + "MatchTime/MatchTime";
    private static readonly string[] Info = ["Info_Show", "Info_On", "Info_Hide"];

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

    private PatchSession Open() => PatchSession.Open(Source, MatchTime, _folder);

    private static AnimationFlatten.Result Flatten(PatchSession s, TrackParts parts) =>
        AnimationFlatten.Plan(MatchTime, s.AnimKeys.Animations, Info, "Info_Hide", "Show", parts, File.ReadAllBytes(s.StockCopyPath));

    /// <summary>The edit key of a visibility key in an animation (e.g. Info_On, Img_HomeColor_1, key 0).</summary>
    private static FieldKey VisibilityKey(PatchSession s, string anim, string widget, int key = 0)
    {
        var t = s.AnimKeys.Animations.Single(a => a.Name == anim).Tracks.Single(t => t.Widget == widget && t.Track == "ByteTrack");
        return AnimKeyIndex.KeyOf(MatchTime, t, t.Channels.Single(), key);
    }

    [TestMethod]
    public void Flatten_C11_AndItsVariants_PlanTheSameKeys()
    {
        var s = Open();
        var all = Flatten(s, TrackParts.All);
        Assert.AreEqual(0, all.Refusals.Count, string.Join("\n", all.Refusals));
        Assert.AreEqual(63, all.Changes.Count);
        var width = Flatten(s, TrackParts.Width).Changes;
        var visibility = Flatten(s, TrackParts.Visibility).Changes;
        var positions = Flatten(s, TrackParts.Positions).Changes;
        Assert.AreEqual(3, width.Count);
        Assert.AreEqual(33, visibility.Count);
        Assert.AreEqual(27, positions.Count);
        CollectionAssert.AreEquivalent(all.Changes.ToList(), width.Concat(visibility).Concat(positions).ToList(), "the variants split C11 exactly");
        Assert.IsTrue(width.All(c => c.NewValue == 492 && c.OldValue == 180), "board width 180 → 492");
        Assert.IsTrue(visibility.All(c => c.NewValue == 3 && c.OldValue == 1), "Collapsed → HitTestInvisible");
    }

    [TestMethod]
    public void Save_C11_BothCopies_ExactDiff_Reparse_AndReadBack()
    {
        var s = Open();
        var flat = Flatten(s, TrackParts.All);
        Assert.IsNull(s.Apply(EditResult.Of(flat.Changes)));
        var plan = s.Plan();
        Assert.IsTrue(plan.Ok, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        Assert.AreEqual(63, plan.AnimKeyCount);
        Assert.AreEqual(99, plan.AnimWrites.Count);
        Assert.IsTrue(plan.AnimWrites.All(w => w.Window is >= BytePlanner.Context and <= BytePlanner.MaxAnimWindow));
        Assert.IsTrue(plan.AnimWrites.Any(w => w.Window > BytePlanner.Context && w.Context32Matches > 1), "some keys need more than ±32");
        // every compiled write has its section twin with the same old and new bytes
        foreach (var c in plan.AnimWrites.Where(w => w.Copy == "compiled"))
        {
            var twin = plan.AnimWrites.Single(w => w.Copy == "section" && w.Key == c.Key);
            CollectionAssert.AreEqual(twin.OldBytes, c.OldBytes);
            CollectionAssert.AreEqual(twin.NewBytes, c.NewBytes);
        }

        var saved = s.Save();
        Assert.IsTrue(saved.Saved, saved.Summary);
        Assert.AreEqual(126, saved.ChangedOffsets.Count);
        StringAssert.Contains(saved.Summary, "size 1269266 unchanged");

        // read back the saved file: the three animations hold the normal values in both copies, everything else is stock
        var bytes = File.ReadAllBytes(s.EditedPath);
        var after = AnimationReader.Read(Source, MatchTime, chunk: bytes).ToDictionary(a => a.Name);
        var stock = AnimationReader.Read(Source, MatchTime).ToDictionary(a => a.Name);
        foreach (var a in stock.Values)
        {
            string Text(AnimationInfo x) => string.Join("\n", x.Tracks.SelectMany(t => AnimationText.Describe(t).Select(d => $"{AnimationText.Target(t)} {d.Name}: {d.Text}")));
            if (!Info.Contains(a.Name)) { Assert.AreEqual(Text(a), Text(after[a.Name]), $"{a.Name} must be unchanged"); continue; }
            foreach (var t in after[a.Name].Tracks)
                foreach (var c in t.Channels.Where(c => c.Keys.Count > 0))
                {
                    Assert.AreNotEqual("DIFFERS", c.Compiled, $"{a.Name} {t.Widget} {c.Path}: the two copies disagree");
                    if (AnimationFlatten.PartOf(t) != TrackParts.None)
                        Assert.AreEqual(1, c.Keys.Select(k => AnimKeyIndex.Parse(k.Value)).Distinct().Count(), $"{a.Name} {t.Widget} {c.Path} isn't flat");
                }
        }

        // recorded in the manifest and the bytes panel with each write's window
        var manifest = JsonDocument.Parse(File.ReadAllText(s.ManifestPath)).RootElement.GetProperty("AnimWrites");
        Assert.AreEqual(99, manifest.GetArrayLength());
        Assert.IsTrue(manifest.EnumerateArray().All(w => w.GetProperty("Window").GetInt32() is >= 32 and <= 512));
        var panel = BytesReport.Build("t", [s], (_, _) => null).Packages.Single();
        Assert.AreEqual(99, panel.AnimRows.Count);
        StringAssert.Contains(panel.AnimText, "63 animation keys in 99 writes");
        StringAssert.Contains(panel.ToText(), "proof    unique stock window ±");

        // undo: back to stock, no writes
        Assert.IsTrue(s.Undo());
        Assert.AreEqual(0, s.Plan().AnimWrites.Count);
    }

    [TestMethod]
    public void Save_Variants_C11a_b_c_EachPassTheHardChecks()
    {
        foreach (var (parts, writes, bytes) in new[] { (TrackParts.Width, 6, 6), (TrackParts.Visibility, 66, 66), (TrackParts.Positions, 27, 54) })
        {
            var s = PatchSession.Open(Source, MatchTime, Path.Combine(_folder, parts.ToString()));
            Assert.IsNull(s.Apply(EditResult.Of(Flatten(s, parts).Changes)));
            var saved = s.Save();
            Assert.IsTrue(saved.Saved, $"{parts}: {saved.Summary}");
            Assert.AreEqual(writes, saved.Plan.AnimWrites.Count, parts.ToString());
            Assert.AreEqual(bytes, saved.ChangedOffsets.Count, parts.ToString());
        }
    }

    // ---- negative tests ----

    [TestMethod]
    public void WrongWidgetsIdenticalSectionBlock_IsRefusedByStructure()
    {
        var s = Open();
        var mine = VisibilityKey(s, "Info_On", "Img_HomeColor_1");
        var other = s.AnimKeys.Find(VisibilityKey(s, "Info_On", "Img_HomeColor_2"))!;
        var stock = File.ReadAllBytes(s.StockCopyPath);
        Assert.AreEqual(stock[s.AnimKeys.Find(mine)!.ValueAt], stock[other.ValueAt], "the two widgets' key bytes are identical");
        // a lookup that points this key at the other widget's identical section copy
        s.AnimKeyTransform = r => r != null && r.SectionExport == mine.SlotExport ? r with { ValueAt = other.ValueAt } : r;
        Assert.IsNull(s.Apply(EditResult.Of([new FieldChange(mine, 1, 3)])));
        var plan = s.Plan();
        Assert.IsFalse(plan.Ok);
        Assert.IsTrue(plan.Refusals.Any(r => r.Rule == RefusalRule.NotLocated && r.Message.Contains("section's own tags")), string.Join("\n", plan.Refusals.Select(r => r.Message)));
        Assert.IsFalse(s.Save().Saved);
        Assert.IsFalse(File.Exists(s.EditedPath));
    }

    [TestMethod]
    public void WrongWidgetsIdenticalCompiledCopy_IsRefusedByTheReparse()
    {
        var s = Open();
        var mine = VisibilityKey(s, "Info_On", "Img_HomeColor_1");
        var other = s.AnimKeys.Find(VisibilityKey(s, "Info_On", "Img_HomeColor_2"))!;
        Assert.IsTrue(other.HasCompiledCopy);
        // same compiled export, identical bytes, unique window: only the re-parse can tell it's the other widget's key
        s.AnimKeyTransform = r => r != null && r.SectionExport == mine.SlotExport ? r with { CompiledValueAt = other.CompiledValueAt } : r;
        Assert.IsNull(s.Apply(EditResult.Of([new FieldChange(mine, 1, 3)])));
        Assert.IsTrue(s.Plan().Ok, string.Join("\n", s.Plan().Refusals.Select(r => r.Message)));
        var saved = s.Save();
        Assert.IsFalse(saved.Saved, "must be refused");
        Assert.IsTrue(saved.Refusals.Any(r => r.Rule == RefusalRule.ReloadMismatch && r.Message.Contains("unintended difference")), saved.Summary);
        Assert.IsTrue(saved.Refusals.Any(r => r.Rule == RefusalRule.ReloadMismatch && r.Message.Contains("compiled copy") && r.Message.Contains("doesn't re-parse")), saved.Summary);
        Assert.IsFalse(File.Exists(s.EditedPath));
    }

    /// <summary>The edit key of Normal's board width key (margin Right) in an animation; it has a compiled copy.</summary>
    private static FieldKey MarginRightKey(PatchSession s, string anim, int key = 0)
    {
        var t = s.AnimKeys.Animations.Single(a => a.Name == anim).Tracks.Single(t => t.Widget == "Normal" && t.Track == "MarginTrack");
        return AnimKeyIndex.KeyOf(MatchTime, t, t.Channels.Single(c => c.Path.Contains("Right")), key);
    }

    private static void AssertRefused(PatchSession s, RefusalRule rule, string text)
    {
        var plan = s.Plan();
        Assert.IsFalse(plan.Ok);
        Assert.AreEqual(0, plan.AnimWrites.Count, "a refused key writes nowhere");
        Assert.IsTrue(plan.Refusals.Any(r => r.Rule == rule && r.Message.Contains(text)), string.Join("\n", plan.Refusals.Select(r => $"[{r.Rule}] {r.Message}")));
        Assert.IsFalse(s.Save().Saved);
        Assert.IsFalse(File.Exists(s.EditedPath));
    }

    [TestMethod]
    public void CompiledCopyOutsideItsExport_IsRefused()
    {
        var s = Open();
        var mine = MarginRightKey(s, "Info_On");
        var r = s.AnimKeys.Find(mine)!;
        Assert.IsTrue(r.HasCompiledCopy);
        // a lookup that puts the compiled copy at the section copy's offset: same bytes, but not in the compiled export
        s.AnimKeyTransform = x => x != null && x.SectionExport == mine.SlotExport ? x with { CompiledValueAt = x.ValueAt } : x;
        Assert.IsNull(s.Apply(EditResult.Of([new FieldChange(mine, 180, 492)])));
        AssertRefused(s, RefusalRule.NotLocated, $"is outside its compiled export {r.CompiledExport}");
    }

    [TestMethod]
    public void CompiledCopyWithOtherBytesThanTheSection_IsRefused()
    {
        var s = Open();
        var mine = MarginRightKey(s, "Info_On");
        var r = s.AnimKeys.Find(mine)!;
        var stock = File.ReadAllBytes(s.StockCopyPath);
        // 4 bytes earlier: still inside the compiled export, but not the key's value
        var shifted = r.CompiledValueAt - 4;
        CollectionAssert.AreNotEqual(stock[r.ValueAt..(r.ValueAt + 4)], stock[shifted..(shifted + 4)], "the shifted bytes must differ for this test");
        s.AnimKeyTransform = x => x != null && x.SectionExport == mine.SlotExport ? x with { CompiledValueAt = shifted } : x;
        Assert.IsNull(s.Apply(EditResult.Of([new FieldChange(mine, 180, 492)])));
        AssertRefused(s, RefusalRule.StockBytesMismatch, $"the compiled copy at 0x{shifted:X} holds");
    }

    [TestMethod]
    public void NonRepresentableValue_IsRefused()
    {
        // a visibility byte takes whole numbers 0…255; a float key takes only values a float holds exactly
        foreach (var (value, isFloat) in new[] { (2.5, false), (256.0, false), (-1.0, false), (0.1, true) })
        {
            var s = PatchSession.Open(Source, MatchTime, Path.Combine(_folder, $"v{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
            var key = isFloat ? MarginRightKey(s, "Info_On") : VisibilityKey(s, "Info_On", "Img_HomeColor_1");
            Assert.IsNull(s.Apply(EditResult.Of([new FieldChange(key, isFloat ? 180 : 1, value)])));
            AssertRefused(s, RefusalRule.NotRepresentable, "can't be stored exactly");
        }
    }

    [TestMethod]
    public void InjectedExtraByte_IsRefusedByTheExactDiff()
    {
        var s = Open();
        Assert.IsNull(s.Apply(EditResult.Of(Flatten(s, TrackParts.Width).Changes)));
        s.BeforeVerify = b => { var c = (byte[]) b.Clone(); c[0x100] ^= 0x01; return c; };
        var saved = s.Save();
        Assert.IsFalse(saved.Saved);
        Assert.IsTrue(saved.Refusals.Any(r => r.Rule == RefusalRule.UnexpectedByteDiff && r.Message.Contains("0x100")), saved.Summary);
        Assert.IsFalse(File.Exists(s.EditedPath));
    }

    [TestMethod]
    public void UniqueWindow_CountsOverlaps_AndRefusesBeyond512()
    {
        // a 2,000-byte block repeated twice: nothing inside it is unique within ±512
        var block = Enumerable.Range(0, 2000).Select(i => (byte) (i * 7 % 251)).ToArray();
        byte[] chunk = [.. new byte[100], .. block, .. block, .. new byte[100]];
        Assert.IsNull(BytePlanner.UniqueWindow(chunk, 100 + 1000, 4));
        // a unique marker is found at ±32
        chunk[100 + 1000] = 0xEE; chunk[100 + 1001] = 0xEE;
        Assert.AreEqual(32, BytePlanner.UniqueWindow(chunk, 100 + 1000, 4));
        // overlapping occurrences count: AA AA occurs twice in AA AA AA
        Assert.AreEqual(2, BytePlanner.CountOccurrences(new byte[] { 0xAA, 0xAA, 0xAA }, new byte[] { 0xAA, 0xAA }));
    }

    [TestMethod]
    public void NotAnAnimationKey_IsRefusedOnApply()
    {
        var s = Open();
        var bogus = new FieldKey(MatchTime, 5, AnimKeyPath.Make("ByteCurve", 0));
        StringAssert.Contains(s.Apply(EditResult.Of([new FieldChange(bogus, 1, 3)])), "is not an animation key");
    }

    [TestMethod]
    public void Export_ListsEachWrite_WithItsWindow()
    {
        var c = new EditorController(Source, new UsageGraph(UiIndex), Engine, p => WidgetTreeReader.Build(Source, p), project: _project);
        var s = c.Session(MatchTime);
        Assert.IsNull(s.Apply(EditResult.Of(Flatten(s, TrackParts.Width).Changes)));
        Assert.IsTrue(c.SaveAll().Single().Result.Saved);
        var plan = c.PlanExport(new ExportRequest("test-" + _project, "pc9999_mobile_and_P", [MatchTime], Source.Config.RetocPath));
        var rows = plan.Edits.Where(e => e.Field.StartsWith("animation key")).ToList();
        Assert.AreEqual(6, rows.Count);
        Assert.IsTrue(rows.All(r => r.OffsetHex.Contains("unique stock window ±")), string.Join("\n", rows.Select(r => r.OffsetHex)));
        Assert.AreEqual(3, rows.Count(r => r.Field.EndsWith("compiled copy")));
    }
}
