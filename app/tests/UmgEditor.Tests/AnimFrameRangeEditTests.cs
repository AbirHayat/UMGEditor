using System.Security.Cryptography;
using UmgEditor.Core;
using UmgEditor.Core.Animation;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Branch experiment, step A for canary C12: key frames, interp modes and range bounds through the ordinary edit set
/// (planned with the animation-key location rule, exact diff and re-parse as hard refusals). Anchor: canary C9, which
/// passed in game, rebuilt through the edit set must give its chunk byte for byte. Each test uses its own folder under
/// work\edits\_tests and deletes it.
/// </summary>
[TestClass]
public class AnimFrameRangeEditTests
{
    private const string Goal = Tv + "MatchTime/MatchTimeGoal";
    /// <summary>sha256 of C9's patched MatchTimeGoal chunk (work\out\mods\C9\build.json, device-tested 2026-10-20).</summary>
    private const string C9ChunkSha256 = "e31086c0db9ffbb7124ef1baa95d7fe6bbd41b207e95c91f948c79ccb4ab03b0";

    private static WriteGuard Guard => new(Source.Config);
    private string _folder = null!;

    [TestInitialize]
    public void NewFolder() => _folder = Path.Combine(Guard.Root, "_tests", Guid.NewGuid().ToString("N")[..10]);

    [TestCleanup]
    public void Cleanup()
    {
        Guard.DeleteDirectory(_folder);
        var parent = Path.GetDirectoryName(_folder)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Guard.DeleteDirectory(parent);
    }

    private PatchSession Open(string sub = "") => PatchSession.Open(Source, Goal, Path.Combine(_folder, sub));

    /// <summary>The Goal canvas's Translation.Y channel in an animation (the banner's vertical motion).</summary>
    private static (AnimTrack Track, AnimChannel Channel) GoalY(PatchSession s, string anim)
    {
        var t = s.AnimKeys.Animations.Single(a => a.Name == anim).Tracks.Single(t => t.Widget == "Goal" && t.Track == "2DTransformTrack");
        return (t, t.Channels.Single(c => c.Path == "Translation[1]"));
    }

    private static FieldKey Key(PatchSession s, string anim, int key, AnimKeyField field = AnimKeyField.Value)
    {
        var (t, c) = GoalY(s, anim);
        return AnimKeyIndex.KeyOf(Goal, t, c, key, field);
    }

    /// <summary>C9 exactly: Show holds −96 (Constant) until 1.8 s, then 0; Hide goes on to +90; Show lasts 2.2 s.</summary>
    private static List<FieldChange> C9(PatchSession s)
    {
        var changes = new List<FieldChange>
        {
            new(Key(s, "Show", 1), 0, -96),
            new(Key(s, "Show", 1, AnimKeyField.Interp), 2, 1),
            new(Key(s, "Show", 2, AnimKeyField.Frame), 48000, 108000),
            new(Key(s, "Hide", 1), -96, 90),
        };
        changes.AddRange(s.AnimationEndChanges("Show", 48000, 132000));
        return changes;
    }

    [TestMethod]
    public void C9_ThroughTheEditSet_IsByteIdenticalToTheDeviceTestedChunk()
    {
        var s = Open();
        var (_, show) = GoalY(s, "Show");
        Assert.AreEqual("0=-96.0 24000=0.0 48000=0.0", string.Join(" ", show.Keys.Select(k => $"{k.Frame}={k.Value}")), "stock Show keys");
        Assert.AreEqual("Cubic", show.Keys[1].Interp);

        var changes = C9(s);
        Assert.IsTrue(changes.Count(c => AnimRangePath.Is(c.Key)) >= 3, "the MovieScene, the sections and the compiled data");
        Assert.IsNull(s.Apply(EditResult.Of(changes)));
        var plan = s.Plan();
        Assert.IsTrue(plan.Ok, string.Join("\n", plan.Refusals.Select(r => $"[{r.Rule}] {r.Message}")));
        Assert.IsTrue(plan.AnimWrites.All(w => w.Window is >= BytePlanner.Context and <= BytePlanner.MaxAnimWindow));
        Assert.AreEqual(4, plan.AnimWrites.Count(w => w.Copy == "section"), "four key writes, no compiled copy (2D transform)");
        Assert.IsTrue(plan.AnimWrites.Where(w => w.Copy == "range").All(w => w.ReparsePath != null));

        var saved = s.Save();
        Assert.IsTrue(saved.Saved, saved.Summary);
        StringAssert.Contains(saved.Summary, "size 109803 unchanged");
        Assert.AreEqual(C9ChunkSha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(s.EditedPath))).ToLowerInvariant(),
            "C9 rebuilt through the edit set must equal the device-tested chunk");

        // read back: the keys and the playback range are C9's
        var after = AnimationReader.Read(Source, Goal, chunk: File.ReadAllBytes(s.EditedPath)).ToDictionary(a => a.Name);
        var showAfter = after["Show"].Tracks.Single(t => t.Widget == "Goal" && t.Track == "2DTransformTrack").Channels.Single(c => c.Path == "Translation[1]");
        Assert.AreEqual("0=-96.0 24000=-96.0 108000=0.0", string.Join(" ", showAfter.Keys.Select(k => $"{k.Frame}={k.Value}")));
        Assert.AreEqual("Constant", showAfter.Keys[1].Interp);
        Assert.AreEqual(132001, after["Show"].End);
    }

    /// <summary>
    /// C12 (goal banner on the board's row, UCL two-row step, 60-unit rows): 23 layout values, 2 inserted tags, 5 key edits
    /// and 8 range edits plan and save. Exactly three proofs need more than ±32 (rule since C12): both plates' Bottom (±48)
    /// and the yellow plate's Top insertion point (±64).
    /// </summary>
    [TestMethod]
    public void C12_GoalBannerOnTheBoardRow_PlansAndSaves()
    {
        var s = Open();
        var tree = WidgetTreeReader.Build(Source, Goal);
        (string Widget, string Field, double Value)[] layout =
        [
            ("MatchTime", "Top", 27), ("MatchTime", "Right", 372), ("MatchTime", "Bottom", 60),
            ("Goal", "Right", 372), ("Goal", "Bottom", 60),
            ("MatchTimeBase_1", "Top", -60), ("MatchTimeBase_1", "Right", 372), ("MatchTimeBase_1", "Bottom", 60),
            ("Plate_MatchTimeBg", "Left", 60), ("Plate_MatchTimeBg", "Top", 0), ("Plate_MatchTimeBg", "Right", 312), ("Plate_MatchTimeBg", "Bottom", 60),
            ("MatchTimeBase_2", "Left", 0), ("MatchTimeBase_2", "Right", 60), ("MatchTimeBase_2", "Bottom", 60),
            ("Emblem_Team", "Left", 6), ("Emblem_Team", "Top", 6), ("Emblem_Team", "Right", 48), ("Emblem_Team", "Bottom", 48),
            ("Text_Item", "Left", 3000), ("Text_PlayerNumber", "Left", 3000),
            ("Text_PlayerName", "Left", 12), ("Text_PlayerName", "Top", -58), ("Text_PlayerName", "Right", 354),
            ("Plate_MatchTimeBg_3", "Bottom", 0),
        ];
        foreach (var (widget, field, value) in layout)
            Assert.IsNull(s.Apply(SlotEditor.SetField(tree.All().Single(n => n.Name == widget), s.Current, "LayoutData.Offsets." + field, value)), $"{widget}.{field}");
        var keys = new List<FieldChange>
        {
            new(Key(s, "Show", 1, AnimKeyField.Interp), 2, 1),
            new(Key(s, "Show", 2, AnimKeyField.Frame), 48000, 108000),
            new(Key(s, "Show", 2), 0, 60),
            new(Key(s, "Hide", 0), 0, 60),
            new(Key(s, "Hide", 1), -96, 180),
        };
        keys.AddRange(s.AnimationEndChanges("Show", 48000, 132000));
        Assert.IsNull(s.Apply(EditResult.Of(keys)));

        var plan = s.Plan();
        Assert.IsTrue(plan.Ok, string.Join("\n", plan.Refusals.Select(r => r.Message)));
        Assert.AreEqual(23, plan.Entries.Count);
        Assert.AreEqual(2, plan.Insertions.Count);
        Assert.AreEqual(109803 + 58, plan.EditedSize);
        var wide = plan.Entries.Where(e => e.Window > BytePlanner.Context).Select(e => $"{e.SlotName}.{e.Key.Path}=±{e.Window}")
            .Concat(plan.Insertions.Where(i => i.Window > BytePlanner.Context).Select(i => $"{i.SlotName}.{i.Path} insert=±{i.Window}")).Order().ToList();
        CollectionAssert.AreEqual(new[]
        {
            "CanvasPanelSlot_11.LayoutData.Offsets.Bottom=±48",
            "CanvasPanelSlot_20.LayoutData.Offsets.Bottom=±48",
            "CanvasPanelSlot_20.LayoutData.Offsets.Top insert=±64",
        }, wide, string.Join(", ", wide));
        Assert.IsTrue(plan.Entries.Where(e => e.Window > BytePlanner.Context).All(e => e.ContextMatches == 2));

        var saved = s.Save();
        Assert.IsTrue(saved.Saved, saved.Summary);
        StringAssert.Contains(saved.Summary, "109803 → 109861");
    }

    // ---- negative tests ----

    [TestMethod]
    public void InterpOnAByteKey_IsRefusedOnApply()
    {
        var s = Open();
        var t = s.AnimKeys.Animations.Single(a => a.Name == "Off").Tracks.Single(t => t.Widget == "Goal" && t.Track == "ByteTrack");
        var key = AnimKeyIndex.KeyOf(Goal, t, t.Channels.Single(), 0, AnimKeyField.Interp);
        StringAssert.Contains(s.Apply(EditResult.Of([new FieldChange(key, 0, 1)])), "has no interp mode");
    }

    [TestMethod]
    public void NonRepresentableFrameOrInterp_IsRefused()
    {
        foreach (var (field, value) in new[] { (AnimKeyField.Frame, 1.5), (AnimKeyField.Interp, 5.0), (AnimKeyField.Interp, -1.0) })
        {
            var s = Open($"{field}{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            Assert.IsNull(s.Apply(EditResult.Of([new FieldChange(Key(s, "Show", 1, field), 0, value)])));
            var plan = s.Plan();
            Assert.IsFalse(plan.Ok);
            Assert.IsTrue(plan.Refusals.Any(r => r.Rule == RefusalRule.NotRepresentable && r.Message.Contains("can't be stored exactly")), string.Join("\n", plan.Refusals.Select(r => r.Message)));
            Assert.IsFalse(s.Save().Saved);
            Assert.IsFalse(File.Exists(s.EditedPath));
        }
    }

    [TestMethod]
    public void WrongFrameLookup_IsRefusedByStructure()
    {
        var s = Open();
        var key = Key(s, "Show", 2, AnimKeyField.Frame);
        // a lookup that points the frame at the previous key's frame (also a valid int32, 4 bytes earlier)
        s.AnimKeyTransform = r => r != null && r.SectionExport == key.SlotExport && r.KeyIndex == 2 ? r with { FrameAt = r.FrameAt - 4 } : r;
        Assert.IsNull(s.Apply(EditResult.Of([new FieldChange(key, 48000, 108000)])));
        var plan = s.Plan();
        Assert.IsFalse(plan.Ok);
        Assert.IsTrue(plan.Refusals.Any(r => r.Rule == RefusalRule.NotLocated && r.Message.Contains("section's own tags")), string.Join("\n", plan.Refusals.Select(r => r.Message)));
        Assert.IsFalse(s.Save().Saved);
    }

    [TestMethod]
    public void RangeWithoutABoundAtThatFrame_IsRefusedOnApply()
    {
        var s = Open();
        var section = GoalY(s, "Show").Track.SectionExport;
        StringAssert.Contains(s.Apply(EditResult.Of([new FieldChange(new FieldKey(Goal, section, AnimRangePath.Make(12345)), 12345, 1)])), "no range bound at frame 12345");
    }

    [TestMethod]
    public void RangeBoundsOfAnotherExport_AreRefusedByTheReparse()
    {
        var s = Open();
        var ends = s.AnimationEndChanges("Show", 48000, 132000);
        var sections = ends.Where(c => s.AnimRange(c.Key).Ref!.Bounds.All(b => b.Path.Contains("SectionRange"))).ToList();
        Assert.IsTrue(sections.Count >= 2, "two sections of Show with identical ranges");
        var (mine, other) = (sections[0].Key, s.AnimRange(sections[1].Key).Ref!);
        // identical bytes (both 48000), unique windows: only the re-parse can tell the bound belongs to another export
        s.AnimRangeTransform = r => r != null && r.Export == mine.SlotExport ? r with { Bounds = other.Bounds } : r;
        Assert.IsNull(s.Apply(EditResult.Of([new FieldChange(mine, 48000, 132000)])));
        Assert.IsTrue(s.Plan().Ok, string.Join("\n", s.Plan().Refusals.Select(r => r.Message)));
        var saved = s.Save();
        Assert.IsFalse(saved.Saved, "must be refused");
        Assert.IsTrue(saved.Refusals.Any(r => r.Rule == RefusalRule.ReloadMismatch && r.Message.Contains("unintended difference")), saved.Summary);
        Assert.IsTrue(saved.Refusals.Any(r => r.Rule == RefusalRule.ReloadMismatch && r.Message.Contains("doesn't re-parse")), saved.Summary);
        Assert.IsFalse(File.Exists(s.EditedPath));
    }

    [TestMethod]
    public void RangeBoundWithOtherStockBytes_IsRefused()
    {
        var s = Open();
        var end = s.AnimationEndChanges("Show", 48000, 132000).First();
        s.AnimRangeTransform = r => r != null && r.Export == end.Key.SlotExport ? r with { Bounds = [r.Bounds[0] with { Offset = r.Bounds[0].Offset - 5 }] } : r;
        Assert.IsNull(s.Apply(EditResult.Of([end])));
        var plan = s.Plan();
        Assert.IsTrue(plan.Refusals.Any(r => r.Rule == RefusalRule.StockBytesMismatch && r.Message.Contains($"doesn't hold frame {AnimRangePath.Parse(end.Key.Path)}")), string.Join("\n", plan.Refusals.Select(r => r.Message)));
        Assert.IsFalse(s.Save().Saved);
    }
}
