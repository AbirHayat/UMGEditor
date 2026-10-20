using UmgEditor.Core;
using UmgEditor.Core.Animation;
using UmgEditor.Core.Patching;

namespace UmgEditor.Tests;

/// <summary>Branch experiment: inserting tags that aren't stored (C10) and same-size animation edits (C9).</summary>
[TestClass]
public class SizeChangingEditTests
{
    private const string MatchTime = GameFixture.Tv + "MatchTime/MatchTime";
    private static readonly string[] Margin = ["Left", "Top", "Right", "Bottom"];

    private static int SlotExport(string widget) => WidgetTreeReader.Build(GameFixture.Source, MatchTime).Find(widget)!.Slot!.ExportIndex;

    [TestMethod]
    public void Insert_MissingLeft_GrowsBy29_AndOnlyThatFieldDiffers()
    {
        var ins = new TagInserter(GameFixture.Source, MatchTime);
        var (_, stock) = GameFixture.Source.ReadPackageBytes(MatchTime);
        Assert.IsTrue(ins.InsertFloat(SlotExport("Text_Score_Home"), "LayoutData.Offsets", "Left", 20f, Margin), string.Join("; ", ins.Refusals));
        var problems = ins.Verify();
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        Assert.AreEqual(stock.Length + TagInserter.FloatTagSize, ins.Bytes.Length);
    }

    [TestMethod]
    public void Insert_SameEditTwice_GivesIdenticalBytes()
    {
        byte[] Build()
        {
            var ins = new TagInserter(GameFixture.Source, MatchTime);
            ins.InsertFloat(SlotExport("Text_Score_Home"), "LayoutData.Offsets", "Left", 20f, Margin);
            return ins.Bytes;
        }
        CollectionAssert.AreEqual(Build(), Build());
    }

    [TestMethod]
    public void Insert_StoredField_IsRefused()
    {
        var ins = new TagInserter(GameFixture.Source, MatchTime);
        Assert.IsFalse(ins.InsertFloat(SlotExport("Text_Score_Home"), "LayoutData.Offsets", "Right", 99f, Margin));
        StringAssert.Contains(ins.Refusals.Single(), "already stored");
    }

    [TestMethod]
    public void Insert_MissingStruct_IsRefused()
    {
        var ins = new TagInserter(GameFixture.Source, MatchTime);
        Assert.IsFalse(ins.InsertFloat(SlotExport("Text_Score_Home"), "LayoutData.NoSuchStruct", "Left", 1f, Margin));
        StringAssert.Contains(ins.Refusals.Single(), "not stored");
    }

    [TestMethod]
    public void AnimationPatch_C9_ChangesExactly14Values_SameSize()
    {
        const string Goal = GameFixture.Tv + "MatchTime/MatchTimeGoal";
        var anims = AnimationReader.Read(GameFixture.Source, Goal).ToDictionary(a => a.Name);
        AnimTrack GoalY(string a) => anims[a].Tracks.Single(t => t.Widget == "Goal" && t.Track == "2DTransformTrack" && t.Channels.Any(c => c.Path == "Translation[1]" && c.Keys.Count > 0));
        var patch = new AnimationPatch(GameFixture.Source, Goal);
        Assert.IsTrue(patch.SetKeys(GoalY("Show").SectionExport, "Translation[1]", [new(1, Value: -96f, InterpMode: 1), new(2, Frame: 108000, Value: 0f)]));
        Assert.IsTrue(patch.SetKeys(GoalY("Hide").SectionExport, "Translation[1]", [new(1, Value: 90f)]));
        foreach (var (i, _, type, _) in patch.ExportsOf("Show").ToList())
        {
            if (type == "MovieScene") Assert.IsTrue(patch.MoveRangeBounds(i, 48001, 132001));
            else if (type.EndsWith("Section") || type == "MovieSceneCompiledData") Assert.IsTrue(patch.MoveRangeBounds(i, 48000, 132000));
        }
        var problems = patch.Verify();
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        Assert.IsTrue(patch.Log.Any(l => l.Contains("14 intended values changed")), string.Join("\n", patch.Log));
        // the chunk the device test used (CANARY_C9.md)
        Assert.AreEqual("e31086c0db9ffbb7124ef1baa95d7fe6bbd41b207e95c91f948c79ccb4ab03b0",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(patch.Bytes)).ToLowerInvariant());
    }

    [TestMethod]
    public void AnimationPatch_WrongStockKeys_AreRefused()
    {
        const string Goal = GameFixture.Tv + "MatchTime/MatchTimeGoal";
        var patch = new AnimationPatch(GameFixture.Source, Goal);
        Assert.IsFalse(patch.SetKeys(0, "Translation[1]", [new(0, Value: 1f)])); // export 0 isn't a section
        Assert.AreEqual(1, patch.Refusals.Count);
    }
}
