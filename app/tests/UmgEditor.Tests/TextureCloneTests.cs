using System.Buffers.Binary;
using CUE4Parse.UE4.IO.Objects;
using UmgEditor.Core;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Branch experiment, canary C13 (option 2, step 1): Zen name-map renames, texture packages cloned under a new name,
/// and soft-path re-points. The anchors are stock data: the name hashes and package ids we compute must reproduce
/// the stored ones.
/// </summary>
[TestClass]
public class TextureCloneTests
{
    private const string DemoTitle = Tv + "DemoTitle/";
    private static readonly string[] Packages =
    [
        DemoTitle + "DemoTitleBase", DemoTitle + "DemoTitle", DemoTitle + "Tex720/DemoTitlePlate", DemoTitle + "0164F/Tex720/DemoTitlePlate",
        Tv + "MatchTime/MatchTime", Tv + "MatchTime/MatchTimeGoal",
    ];

    [TestMethod]
    public void NameMap_OurHashReproducesEveryStoredHash()
    {
        foreach (var p in Packages)
        {
            var (_, bytes) = Source.ReadPackageBytes(p);
            var names = ZenNameMap.Read(bytes);
            var parsed = Source.ParsePackage(Source.ReadPackageBytes(p).FileKey, bytes).NameMap.Select(n => n.Name).ToList();
            CollectionAssert.AreEqual(parsed, names.Select(n => n.Text).ToList(), $"{p}: our name map read differs from CUE4Parse's");
            var bad = names.Where(n => ZenNameMap.Hash(n.Text, n.Wide) != n.Hash).Select(n => n.Text).ToList();
            Assert.AreEqual(0, bad.Count, $"{p}: {bad.Count} of {names.Count} hashes not reproduced: {string.Join(", ", bad.Take(5))}");
            Console.WriteLine($"{GamePathsShort(p)}: {names.Count} names, all hashes reproduced");
        }
    }

    [TestMethod]
    public void PackageId_OfTheStockPathReproducesTheChunkId()
    {
        foreach (var p in Packages)
        {
            var (fileKey, _) = Source.ReadPackageBytes(p);
            var b = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, FPackageId.FromName(p).id);
            Assert.AreEqual(Source.ChunkIdHex(fileKey), Convert.ToHexString(b).ToLowerInvariant() + "00000002", p);
        }
    }

    private static string GamePathsShort(string p) => p[(p.LastIndexOf('/') + 1)..];

    private const string Plate720 = DemoTitle + "Tex720/DemoTitlePlate", Plate0164F = DemoTitle + "0164F/Tex720/DemoTitlePlate";
    private const string SoftFrom = Plate720 + ".DemoTitlePlate", SoftTo = DemoTitle + "Tex720/DemoTitleUCL01.DemoTitleUCL01";

    // ---- texture clones ----

    [TestMethod]
    public void Clone_BothThemeCopies_NewName_NewPixels_DecodeBack()
    {
        foreach (var (path, r, g, b) in new[] { (Plate720, (byte) 0, (byte) 255, (byte) 0), (Plate0164F, (byte) 255, (byte) 0, (byte) 255) })
        {
            var result = TextureClone.Build(Source, path, "DemoTitleUCL01", TextureClone.Solid(r, g, b));
            Console.WriteLine(string.Join("\n", result.Log));
            Assert.IsNotNull(result.Package, string.Join("\n", result.Refusals));
            var p = result.Package;
            Assert.AreEqual(path.Replace("DemoTitlePlate", "DemoTitleUCL01"), p.PackagePath);
            Assert.IsTrue(p.FileKey.EndsWith("/DemoTitleUCL01.uasset"));
            Assert.AreEqual("", Source.ChunkIdHex(p.FileKey), "a new file");
            Assert.AreEqual(Source.ReadPackageBytes(path).FileKey, p.CloneOf);
            var names = ZenNameMap.Read(p.Chunk);
            Assert.IsTrue(names.Any(n => n.Text == p.PackagePath) && names.Any(n => n.Text == "DemoTitleUCL01"));
            Assert.IsFalse(names.Any(n => n.Text.Contains("DemoTitlePlate")));
            Assert.IsTrue(names.All(n => ZenNameMap.Hash(n.Text, n.Wide) == n.Hash), "every hash consistent after the rename");
            Assert.AreEqual(Source.ReadPackageBytes(path).Bytes.Length, p.Chunk.Length, "same size");
        }
    }

    [TestMethod]
    public void Clone_Refusals()
    {
        StringAssert.Contains(TextureClone.Build(Source, Plate720, "DemoTitleUCL01", (w, h) => new byte[w * h * 4 - 1]).Refusals.Single(), "the new pixels of mip 0 are 15 bytes, the stock mip 16");
        StringAssert.Contains(TextureClone.Build(Source, Plate720, "DemoTitleUCL", TextureClone.Solid(0, 255, 0)).Refusals.Single(), "must be as long as");
        StringAssert.Contains(TextureClone.Build(Source, Plate720, "DemoTitlePlate", TextureClone.Solid(0, 255, 0)).Refusals.Single(), "already exists in the stock containers");
        StringAssert.Contains(TextureClone.Build(Source, DemoTitle + "DemoTitleBase", "DemoTitleBasX", TextureClone.Solid(0, 255, 0)).Refusals.Single(), "isn't a package with one Texture2D export");
    }

    // ---- soft-path re-point ----

    [TestMethod]
    public void Repoint_DemoTitleBase_OnlyTheTexturePathChanges()
    {
        var (fileKey, stock) = Source.ReadPackageBytes(DemoTitle + "DemoTitleBase");
        Assert.AreEqual(1, ZenNameMap.Read(stock).Count(n => n.Text == SoftFrom), "the soft path is one name");
        var r = SoftPathRepoint.Build(Source, DemoTitle + "DemoTitleBase", SoftFrom, SoftTo);
        Console.WriteLine(string.Join("\n", r.Log));
        Assert.IsNotNull(r.Package, string.Join("\n", r.Refusals));
        Assert.AreEqual(Source.ChunkIdHex(fileKey), r.Package.ChunkIdHex);
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(Source.ParsePackage(fileKey, r.Package.Chunk).ExportsLazy.Select(e => e.Value).Single(e => e.Name == "Img_Plate"));
        StringAssert.Contains(json, SoftTo);
        Assert.IsTrue(r.Log.Any(l => l.Contains("1 values differ")), string.Join("\n", r.Log));
        var changed = Enumerable.Range(0, stock.Length).Count(i => stock[i] != r.Package.Chunk[i]);
        Assert.IsTrue(changed is > 0 and <= 2 * 14 + 8, $"{changed} bytes: at most the 28 renamed characters and the 8 hash bytes");
    }

    [TestMethod]
    public void Rename_Refusals()
    {
        var (_, stock) = Source.ReadPackageBytes(DemoTitle + "DemoTitleBase");
        StringAssert.Contains(ZenNameMap.Rename(stock, [(SoftFrom, SoftTo + "x")]).Why, "must keep the length");
        StringAssert.Contains(ZenNameMap.Rename(stock, [("NoSuchName", "NoSuchNamX")]).Why, "occurs 0 times");
        var pair = ZenNameMap.Read(stock).GroupBy(n => n.Text.Length).First(g => g.Count() >= 2).Take(2).ToList();
        StringAssert.Contains(ZenNameMap.Rename(stock, [(pair[0].Text, pair[1].Text)]).Why, "already in the name map");
        StringAssert.Contains(ZenNameMap.Rename(stock, [(SoftFrom, SoftTo.Replace('U', 'é'))]).Why, "isn't plain ASCII");
    }

    [TestMethod]
    public void Rename_StrayByteOrUnintendedParse_IsRefused()
    {
        var (fileKey, stock) = Source.ReadPackageBytes(DemoTitle + "DemoTitleBase");
        // a planned write over a renamed name is refused; a write elsewhere changes the parse and is refused by the re-parse
        var names = ZenNameMap.Read(stock);
        var soft = names.Single(n => n.Text == SoftFrom);
        StringAssert.Contains(NameRenamePatch.Apply(Source, fileKey, stock, [(SoftFrom, SoftTo)], [(soft.StringAt, [0x41], "overlap")]).Refusals.Single(), "overlaps a renamed name");
        var other = names.Single(n => n.Text == "Img_Plate");
        var r = NameRenamePatch.Apply(Source, fileKey, stock, [(SoftFrom, SoftTo)], [(other.StringAt, [(byte) 'J'], "a stray write into another name")]);
        Assert.IsFalse(r.Ok);
        Assert.IsTrue(r.Refusals.Any(x => x.Contains("unintended difference")), string.Join("\n", r.Refusals));
    }

    // ---- the mod: three packages, two of them new ----

    [TestMethod]
    public void Mod_TwoNewTexturePackagesAndTheRepoint_BuildVerifyAndMountBack()
    {
        var green = TextureClone.Build(Source, Plate720, "DemoTitleUCL01", TextureClone.Solid(0, 255, 0)).Package!;
        var magenta = TextureClone.Build(Source, Plate0164F, "DemoTitleUCL01", TextureClone.Solid(255, 0, 255)).Package!;
        var repoint = SoftPathRepoint.Build(Source, DemoTitle + "DemoTitleBase", SoftFrom, SoftTo).Package!;
        var name = "test-" + Guid.NewGuid().ToString("N")[..12];
        var mods = WriteGuard.ForMods(Source.Config);
        try
        {
            var built = new ModBuilder(Source, new RetocProcess(Source.Config.RetocPath)).Build(name, "pc9999_mobile_and_P", [green, magenta, repoint]);
            Assert.IsTrue(built.Built, string.Join(" | ", built.Refusals) + "\n" + built.Log);
            var header = ContainerHeader.Parse(File.ReadAllBytes(Directory.GetFiles(Path.Combine(built.OutDir, "raw", "chunks"), "*0000000a").Single()));
            Assert.AreEqual(3, header.PackageIds.Count);
            foreach (var p in new[] { green, magenta })
            {
                var id = BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString(p.ChunkIdHex[..16]));
                var stockHeader = ContainerHeader.Parse(Source.ReadContainerHeaderChunk(p.CloneOf!));
                var stockEntry = stockHeader.Entries[stockHeader.PackageIds.ToList().IndexOf(BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString(Source.ChunkIdHex(p.CloneOf!)[..16])))];
                Assert.IsTrue(header.Entries[header.PackageIds.ToList().IndexOf(id)].SameAs(stockEntry), "store entry copied from the clone's source");
            }
            // refusals: a clone whose chunk id isn't its path's, a clone on a stock path
            var r1 = new ModBuilder(Source, new RetocProcess(Source.Config.RetocPath)).Build(name + "b", "pc9999_mobile_and_P", [green with { ChunkIdHex = magenta.ChunkIdHex }]);
            Assert.IsTrue(r1.Refusals.Any(x => x.Contains("isn't the package id of")), string.Join(" | ", r1.Refusals));
            var r2 = new ModBuilder(Source, new RetocProcess(Source.Config.RetocPath)).Build(name + "c", "pc9999_mobile_and_P", [repoint with { CloneOf = green.CloneOf }]);
            Assert.IsTrue(r2.Refusals.Any(x => x.Contains("can't use the path of a stock file")), string.Join(" | ", r2.Refusals));
        }
        finally
        {
            foreach (var dir in Directory.Exists(mods.Root) ? Directory.GetDirectories(mods.Root, name + "*") : []) mods.DeleteDirectory(dir);
        }
    }
}
