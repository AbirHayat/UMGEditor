using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Branch experiment, canary C17b: UCL's font on the scoreboard = <c>_font.TypefaceFontName</c> = HeadlineBoldSub on the
/// texts (<see cref="NameTagEdit"/>) and pes's Latin HeadlineBoldSub entry re-pointed to NotoSansThai-ExtraBold
/// (<see cref="FontFaceRepoint"/>), whose .ufont the mod replaces.
/// </summary>
[TestClass]
public class FontEditTests
{
    private const string Bold = "HeadlineBoldSub";

    /// <summary>The tag builder reproduces stock's own TypefaceFontName tags (MatchTime's team names) byte for byte.</summary>
    [TestMethod]
    public void Build_ReproducesStockTag()
    {
        foreach (var export in new[] { "Text_TeamName_Home", "Text_TeamName_Away" })
        {
            var why = NameTagEdit.CheckAgainst(Source, Tv + "MatchTime/MatchTime", export, "_font", "TypefaceFontName", "HeadlineBold");
            Assert.IsNull(why, why);
        }
    }

    [TestMethod]
    public void AppendNamesThenInsert_SetsTheTypeface()
    {
        var (fileKey, stock) = Source.ReadPackageBytes(Tv + "MatchTime/MatchTimeTimer");
        var appended = EnumTagInsert.AppendNames(Source, fileKey, stock, ["TypefaceFontName", Bold]);
        Assert.IsNotNull(appended.Chunk, string.Join("\n", appended.Refusals));
        var inserted = NameTagEdit.Insert(Source, fileKey, appended.Chunk, "Text_Minute_1", "_font", "TypefaceFontName", Bold, NameTagEdit.SlateFontInfoOrder);
        Console.WriteLine(string.Join("\n", inserted.Log));
        Assert.IsNotNull(inserted.Chunk, string.Join("\n", inserted.Refusals));
        Assert.AreEqual(appended.Chunk.Length + NameTagEdit.TagLength, inserted.Chunk.Length);
        var export = Source.ParsePackage(fileKey, inserted.Chunk).ExportsLazy.Select(e => e.Value).Single(e => e.Name == "Text_Minute_1");
        var flat = TagInserter.Flatten(export);
        Assert.AreEqual(Bold, flat["Properties._font.TypefaceFontName"].Trim('"'));
        Assert.AreEqual("30", flat["Properties._font.Size"]);
        var (problems, changes) = LayerUnion.Check(Source, fileKey, stock, [appended.Chunk, inserted.Chunk]);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public void Swap_TeamName()
    {
        var (fileKey, stock) = Source.ReadPackageBytes(Tv + "MatchTime/MatchTime");
        var appended = EnumTagInsert.AppendNames(Source, fileKey, stock, [Bold]).Chunk!;
        var swapped = NameTagEdit.Swap(Source, fileKey, appended, "Text_TeamName_Home", "_font", "TypefaceFontName", "HeadlineBold", Bold);
        Assert.IsNotNull(swapped.Chunk, string.Join("\n", swapped.Refusals));
        Assert.AreEqual(appended.Length, swapped.Chunk.Length);
        var diff = Enumerable.Range(0, appended.Length).Where(i => appended[i] != swapped.Chunk[i]).ToList();
        Assert.IsTrue(diff.Count is >= 1 and <= 8 && diff[^1] - diff[0] < 8, string.Join(", ", diff));
        var (problems, changes) = LayerUnion.Check(Source, fileKey, stock, [appended, swapped.Chunk]);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        Assert.AreEqual(1, changes);
    }

    [TestMethod]
    public void Refusals()
    {
        var (fileKey, stock) = Source.ReadPackageBytes(Tv + "MatchTime/MatchTimeTimer");
        // without the names, the tag can't be built
        StringAssert.Contains(NameTagEdit.Insert(Source, fileKey, stock, "Text_Colon", "_font", "TypefaceFontName", Bold, NameTagEdit.SlateFontInfoOrder).Refusals.Single(), "occurs 0 times");
        var appended = EnumTagInsert.AppendNames(Source, fileKey, stock, ["TypefaceFontName", Bold]).Chunk!;
        StringAssert.Contains(NameTagEdit.Insert(Source, fileKey, appended, "Text_Colon", "NoSuchStruct", "TypefaceFontName", Bold, NameTagEdit.SlateFontInfoOrder).Refusals.Single(), "isn't stored once as a struct");
        StringAssert.Contains(NameTagEdit.Insert(Source, fileKey, appended, "Text_Colon", "_font", "Typeface", Bold, NameTagEdit.SlateFontInfoOrder).Refusals.Single(), "declaration order");
        var once = NameTagEdit.Insert(Source, fileKey, appended, "Text_Colon", "_font", "TypefaceFontName", Bold, NameTagEdit.SlateFontInfoOrder).Chunk!;
        StringAssert.Contains(NameTagEdit.Insert(Source, fileKey, once, "Text_Colon", "_font", "TypefaceFontName", Bold, NameTagEdit.SlateFontInfoOrder).Refusals.Single(), "already stored");
        StringAssert.Contains(NameTagEdit.Swap(Source, fileKey, once, "Text_Colon", "_font", "TypefaceFontName", "HeadlineBold", Bold).Refusals.Single(), "not HeadlineBold");
        StringAssert.Contains(NameTagEdit.Swap(Source, fileKey, appended, "Text_Colon", "_font", "TypefaceFontName", "Bold", Bold).Refusals.Single(), "isn't stored once");
    }

    [TestMethod]
    public void Pes_RepointsOnlyTheLatinHeadlineBoldSub()
    {
        var (fileKey, stock) = Source.ReadPackageBytes(UclC16.Pes);
        var r = FontFaceRepoint.Plan(Source, fileKey, stock, UclC16.FontEntry, Bold, UclC16.FontFrom, UclC16.FontTo);
        Console.WriteLine(string.Join("\n", r.Log));
        Assert.IsNotNull(r.Chunk, string.Join("\n", r.Refusals));
        var diff = Enumerable.Range(0, stock.Length).Where(i => stock[i] != r.Chunk[i]).ToList();
        Assert.IsTrue(diff.Count is >= 1 and <= 4 && diff[^1] - diff[0] < 4, string.Join(", ", diff));
        var flat = TagInserter.Flatten(Source.ParsePackage(fileKey, r.Chunk).ExportsLazy[0].Value);
        Assert.AreEqual("FontFace'NotoSansThai-ExtraBold'", flat[$"Properties.{UclC16.FontEntry}.Font.LocalFontFaceAsset.ObjectName"].Trim('"'));
        // HeadlineBold (Fonts[3]) keeps the stencil
        Assert.AreEqual("FontFace'eFootballStencil-Regular'", flat["Properties.CompositeFont.SubTypefaces[9].Typeface.Fonts[3].Font.LocalFontFaceAsset.ObjectName"].Trim('"'));
        var (problems, changes) = LayerUnion.Check(Source, fileKey, stock, [r.Chunk]);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        Assert.AreEqual(2, changes); // ObjectName and ObjectPath
    }

    [TestMethod]
    public void Pes_Refusals()
    {
        var (fileKey, stock) = Source.ReadPackageBytes(UclC16.Pes);
        StringAssert.Contains(FontFaceRepoint.Plan(Source, fileKey, stock, UclC16.FontEntry, "HeadlineBold", UclC16.FontFrom, UclC16.FontTo).Refusals.Single(), "isn't the HeadlineBold entry");
        StringAssert.Contains(FontFaceRepoint.Plan(Source, fileKey, stock, UclC16.FontEntry, Bold, "eFootballSans-Bold", UclC16.FontTo).Refusals.Single(), "not eFootballSans-Bold");
        StringAssert.Contains(FontFaceRepoint.Plan(Source, fileKey, stock, UclC16.FontEntry, Bold, UclC16.FontFrom, "NoSuchFace").Refusals.Single(), "doesn't use NoSuchFace");
    }
}
