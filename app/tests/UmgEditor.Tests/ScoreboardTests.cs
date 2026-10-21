using UmgEditor.Core;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Branch experiment, canary C15 (UCL-style scoreboard): removing a top-level tag (Normal_Display's Clipping → the
/// class default) and swapping an enum value to another existing name (the scores' colour 87 → 86). Both on the stock
/// MatchTime package, with the checks the builders enforce.
/// </summary>
[TestClass]
public class ScoreboardTests
{
    private const string MatchTime = Tv + "MatchTime/MatchTime";
    private const string Dark = "ColorEnum_String::NewEnumerator87", White = "ColorEnum_String::NewEnumerator86";

    private static (string FileKey, byte[] Bytes, CUE4Parse.UE4.Assets.IoPackage Parsed, ZenPackageLayout Layout) Stock()
    {
        var (fileKey, bytes) = Source.ReadPackageBytes(MatchTime);
        var parsed = Source.ParsePackage(fileKey, bytes);
        return (fileKey, bytes, parsed, ZenPackageLayout.Read(bytes, parsed));
    }

    private static int Export(CUE4Parse.UE4.Assets.IoPackage p, string name) => p.ExportsLazy.Select(e => e.Value.Name).ToList().IndexOf(name);

    [TestMethod]
    public void RemoveClipping_OnlyThatPropertyGoes_AndTheHeaderFollows()
    {
        var (fileKey, bytes, parsed, layout) = Stock();
        var r = TagRemover.Remove(Source, fileKey, bytes, "Normal_Display", "Clipping", "EWidgetClipping::ClipToBounds");
        Console.WriteLine(string.Join("\n", r.Log));
        Assert.IsNotNull(r.Chunk, string.Join("\n", r.Refusals));
        var tag = new TaggedPropertyWalker(layout.NameMap).TopLevelTags(bytes, layout.SpanOf(Export(parsed, "Normal_Display"))).Single(t => t.Name == "Clipping");
        Assert.AreEqual(bytes.Length - tag.Length, r.Chunk.Length);
        var after = Source.ParsePackage(fileKey, r.Chunk);
        Assert.IsFalse(Newtonsoft.Json.JsonConvert.SerializeObject(after.ExportsLazy[Export(parsed, "Normal_Display")].Value).Contains("Clipping"));
        Assert.IsTrue(Newtonsoft.Json.JsonConvert.SerializeObject(after.ExportsLazy[Export(parsed, "Mask")].Value).Contains("ClipToBounds"), "the Mask keeps its clipping");
        // the layout reads the shrunk export where the parse says
        var newLayout = ZenPackageLayout.Read(r.Chunk, after);
        Assert.AreEqual(layout.SpanOf(Export(parsed, "Normal_Display")).Size - tag.Length, newLayout.SpanOf(Export(parsed, "Normal_Display")).Size);
    }

    [TestMethod]
    public void RemoveTag_Refusals()
    {
        var (fileKey, bytes, _, _) = Stock();
        StringAssert.Contains(TagRemover.Remove(Source, fileKey, bytes, "Normal_Display", "Clipping", "EWidgetClipping::Inherit").Refusals.Single(), "expected EWidgetClipping::Inherit");
        StringAssert.Contains(TagRemover.Remove(Source, fileKey, bytes, "Normal_Display", "NoSuchProperty", "x").Refusals.Single(), "occurs 0 times");
        StringAssert.Contains(TagRemover.Remove(Source, fileKey, bytes, "NoSuchWidget", "Clipping", "x").Refusals.Single(), "0 exports named NoSuchWidget");
    }

    [TestMethod]
    public void SwapScoreColour_ToWhite_OnlyThoseValuesChange()
    {
        var (fileKey, bytes, parsed, layout) = Stock();
        var writes = new List<(int, byte[], string)>();
        var expected = new Dictionary<(int, string), string>();
        foreach (var score in new[] { "Text_Score_Home", "Text_Score_Away" })
        {
            var (at, b, why) = EnumValueSwap.Plan(bytes, layout, Export(parsed, score), "color", Dark, White);
            Assert.IsNull(why, why);
            writes.Add((at, b, score));
            expected[(Export(parsed, score), "Properties.color")] = White;
        }
        var r = NameRenamePatch.Apply(Source, fileKey, bytes, [], writes, expected);
        Console.WriteLine(string.Join("\n", r.Log));
        Assert.IsTrue(r.Ok, string.Join("\n", r.Refusals));
        Assert.AreEqual(bytes.Length, r.Chunk!.Length);
        Assert.IsTrue(r.Log.Any(l => l.Contains("2 values differ")), string.Join("\n", r.Log));
    }

    [TestMethod]
    public void SwapEnum_Refusals()
    {
        var (_, bytes, parsed, layout) = Stock();
        StringAssert.Contains(EnumValueSwap.Plan(bytes, layout, Export(parsed, "Text_Score_Home"), "color", White, Dark).Why, $"holds {Dark}");
        StringAssert.Contains(EnumValueSwap.Plan(bytes, layout, Export(parsed, "Text_Score_Home"), "color", Dark, "ColorEnum_String::NewEnumerator999").Why, "isn't in the name map");
        StringAssert.Contains(EnumValueSwap.Plan(bytes, layout, Export(parsed, "Text_Score_Home"), "nosuch", Dark, White).Why, "occurs 0 times");
        // the expected value must really appear: a write that doesn't produce it is refused by the re-parse
        var (fileKey, _) = Source.ReadPackageBytes(MatchTime);
        var r = NameRenamePatch.Apply(Source, fileKey, bytes, [], [], new Dictionary<(int, string), string> { [(Export(parsed, "Text_Score_Home"), "Properties.color")] = White });
        Assert.IsFalse(r.Ok);
        Assert.IsTrue(r.Refusals.Any(x => x.Contains("doesn't read back as")), string.Join("\n", r.Refusals));
    }
}
