using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Branch experiment, canary C16: the parts of UclC16 that don't need the C16 edit sets (those are the user's working
/// data): the replay group end to end with test art, the logo's clone source and same-length re-point, the white
/// plates, and the layer union check.
/// </summary>
[TestClass]
public class UclC16Tests
{
    private static string ArtDir()
    {
        var dir = Path.Combine(Source.Config.WorkDir, "edits", "_tests", "c16art_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "MatchTimeNavyG_0_512x64.bgra"), TextureClone.Solid(0, 0, 84)(512, 64));
        return dir;
    }

    [TestMethod]
    public void Replay_ClassSwapAndNavyGradient()
    {
        var dir = ArtDir();
        try
        {
            var o = UclC16.Build(Source, Source.Config, "_none", ["replay"], dir);
            foreach (var s in o.Steps) Console.WriteLine($"{(s.Ok ? "PASS" : "FAIL")} {s.What} {s.Detail}");
            Assert.IsTrue(o.Ok, string.Join("\n", o.Steps.Where(s => !s.Ok).Select(s => s.What + ": " + s.Detail)));
            CollectionAssert.AreEquivalent(new[] { UclC16.DT + "DemoTitle", UclC16.MT + "MatchTimeBase", UclC16.MT + "Tex720/MatchTimeNavyG_0", UclC16.MT + "0164F/Tex720/MatchTimeNavyG_0" },
                o.Packages.Select(p => p.PackagePath).ToList());
            var demoTitle = o.Packages.Single(p => p.PackagePath.EndsWith("/DemoTitle"));
            Assert.AreEqual(1, demoTitle.ImportedPackageSwaps.Count);
            Assert.AreEqual(2, o.Textures.Count);
            Assert.IsTrue(o.Expects.Any(e => e.Export == "Plate_DemoTitleBg" && e.Contains == "MatchTimeBase_C"));
            Assert.IsTrue(o.Expects.Any(e => e.Export == "Img_Plate" && e.Contains.EndsWith("MatchTimeNavyG_0.MatchTimeNavyG_0")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void MissingArt_IsRefused()
    {
        var dir = Path.Combine(Source.Config.WorkDir, "edits", "_tests", "c16empty_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var o = UclC16.Build(Source, Source.Config, "_none", ["replay"], dir);
            Assert.IsFalse(o.Ok);
            StringAssert.Contains(o.Steps.Single(s => !s.Ok).What, "MatchTimeNavyG_0_512x64.bgra");
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>The logo: an ETC2 stock texture can't be cloned, so the B8G8R8A8 ExtraLoginBonusInfo (112x112) is, at 80x80; its path fits the logo's exactly.</summary>
    [TestMethod]
    public void Logo_CloneSourceAndRepoint()
    {
        Assert.AreEqual(UclC16.LogoFrom.Length, UclC16.LogoTo.Length);
        var logo = new TextureClone.Mip(80, 80, TextureClone.Solid(255, 255, 255)(80, 80));
        var clone = TextureClone.BuildResized(Source, UclC16.LogoSource, UclC16.LogoName, [logo]);
        Assert.IsNotNull(clone.Package, string.Join("\n", clone.Refusals));
        Assert.AreEqual(UclC16.LogoTo[..UclC16.LogoTo.LastIndexOf('.')], clone.Package.PackagePath);
        var (fileKey, bytes) = Source.ReadPackageBytes(UclC16.MT + "MatchTime");
        var r = NameRenamePatch.Apply(Source, fileKey, bytes, [(UclC16.LogoFrom, UclC16.LogoTo)]);
        Assert.IsTrue(r.Ok, string.Join("\n", r.Refusals));
        var img = Source.ParsePackage(fileKey, r.Chunk!).ExportsLazy.Select(e => e.Value).Single(e => e.Name == "Img_Logo");
        Assert.AreEqual(UclC16.LogoTo, TagInserter.Flatten(img)["Properties.texture.AssetPathName"].Trim('"'));
    }

    /// <summary>One layer's change is the union; a later layer that changes the same value again (here: undoes it) is refused.</summary>
    [TestMethod]
    public void LayerUnion_RefusesALayerThatChangesAnEarlierLayersValue()
    {
        var (fileKey, stock) = Source.ReadPackageBytes(UclC16.MT + "MatchTimeBase_1");
        const string from = UclC16.MT + "Tex720/MatchTimePlate_1.MatchTimePlate", to = UclC16.MT + "Tex720/MatchTimeWhite_1.MatchTimeWhite";
        var a = NameRenamePatch.Apply(Source, fileKey, stock, [(from, to)]).Chunk!;
        var back = NameRenamePatch.Apply(Source, fileKey, a, [(to, from)]).Chunk!;
        CollectionAssert.AreEqual(stock, back);
        var (p1, n1) = LayerUnion.Check(Source, fileKey, stock, [a]);
        Assert.AreEqual(0, p1.Count, string.Join("\n", p1));
        Assert.AreEqual(1, n1);
        var (p2, _) = LayerUnion.Check(Source, fileKey, stock, [a, back]);
        StringAssert.Contains(string.Join("\n", p2), "an earlier layer changed it");
    }
}
