using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Branch experiment, canary C16b: centring the goal and card banners' names = appending the names a Justification tag
/// needs to the package's name map, then inserting the tag.
/// </summary>
[TestClass]
public class CentreTextTests
{
    private static readonly string[] JustifyNames = ["Justification", "ETextJustify", "ETextJustify::Center"];

    /// <summary>The tag builder reproduces stock's own Justification tag (MatchTime's team names) byte for byte.</summary>
    [TestMethod]
    public void Build_ReproducesStockTag()
    {
        foreach (var export in new[] { "Text_TeamName_Home", "Text_Score_Away" })
        {
            var why = EnumTagInsert.CheckAgainst(Source, Tv + "MatchTime/MatchTime", export, "Justification", "ByteProperty", "ETextJustify", "ETextJustify::Center");
            Assert.IsNull(why, why);
        }
    }

    /// <summary>Every stored hash of the packages involved is reproduced (the append's precondition).</summary>
    [TestMethod]
    public void NameHashes_AllReproduced()
    {
        foreach (var p in new[] { "MatchTimeGoal", "MatchTimeCard", "MatchTime", "MatchTimeMemberChange" })
        {
            var names = ZenNameMap.Read(Source.ReadPackageBytes(Tv + "MatchTime/" + p).Bytes);
            Assert.IsTrue(names.All(n => ZenNameMap.Hash(n.Text, n.Wide) == n.Hash), p);
        }
    }

    [DataTestMethod]
    [DataRow("MatchTimeGoal", "Text_PlayerName")]
    [DataRow("MatchTimeCard", "Text_Value")]
    public void AppendNamesThenInsert_CentresTheName(string package, string text)
    {
        var (fileKey, stock) = Source.ReadPackageBytes(Tv + "MatchTime/" + package);
        var appended = EnumTagInsert.AppendNames(Source, fileKey, stock, JustifyNames);
        Console.WriteLine(string.Join("\n", appended.Log));
        Assert.IsNotNull(appended.Chunk, string.Join("\n", appended.Refusals));
        Assert.AreEqual(0, (appended.Chunk.Length - stock.Length) % 8);
        var inserted = EnumTagInsert.Insert(Source, fileKey, appended.Chunk, text, "VerticalAlignment", "Justification", "ByteProperty", "ETextJustify", "ETextJustify::Center");
        Console.WriteLine(string.Join("\n", inserted.Log));
        Assert.IsNotNull(inserted.Chunk, string.Join("\n", inserted.Refusals));
        Assert.AreEqual(appended.Chunk.Length + EnumTagInsert.TagLength, inserted.Chunk.Length);
        var export = Source.ParsePackage(fileKey, inserted.Chunk).ExportsLazy.Select(e => e.Value).Single(e => e.Name == text);
        Assert.AreEqual("ETextJustify::Center", TagInserter.Flatten(export)["Properties.Justification"].Trim('"'));
        var (problems, changes) = LayerUnion.Check(Source, fileKey, stock, [appended.Chunk, inserted.Chunk]);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public void Refusals()
    {
        var (fileKey, stock) = Source.ReadPackageBytes(Tv + "MatchTime/MatchTimeGoal");
        StringAssert.Contains(ZenNameMap.Append(stock, ["VerticalAlignment"]).Why, "already in the name map");
        StringAssert.Contains(ZenNameMap.Append(stock, ["Justifié"]).Why, "plain ASCII");
        StringAssert.Contains(ZenNameMap.Append(stock, ["Justification", "Justification"]).Why, "distinct");
        // without the names, the tag can't be built
        StringAssert.Contains(EnumTagInsert.Insert(Source, fileKey, stock, "Text_PlayerName", "VerticalAlignment", "Justification", "ByteProperty", "ETextJustify", "ETextJustify::Center").Refusals.Single(), "occurs 0 times");
        var appended = EnumTagInsert.AppendNames(Source, fileKey, stock, JustifyNames).Chunk!;
        StringAssert.Contains(EnumTagInsert.Insert(Source, fileKey, appended, "Text_PlayerName", "NoSuchTag", "Justification", "ByteProperty", "ETextJustify", "ETextJustify::Center").Refusals.Single(), "occurs 0 times among");
        var once = EnumTagInsert.Insert(Source, fileKey, appended, "Text_PlayerName", "VerticalAlignment", "Justification", "ByteProperty", "ETextJustify", "ETextJustify::Center").Chunk!;
        StringAssert.Contains(EnumTagInsert.Insert(Source, fileKey, once, "Text_PlayerName", "VerticalAlignment", "Justification", "ByteProperty", "ETextJustify", "ETextJustify::Center").Refusals.Single(), "already stores");
    }
}
