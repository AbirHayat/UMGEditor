using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Branch experiment, canary C17a: the legacy pak v11 writer (loose files such as .ufont fonts). Anchor: stock paks
/// rebuilt from what Read parses are byte-identical. Each write test uses its own folder under work\edits\_tests and deletes it.
/// </summary>
[TestClass]
public class LegacyPakTests
{
    private static WriteGuard Guard => new(Source.Config);
    private static byte[] Key => LegacyPak.KeyBytes(Source.Config.AesKey);
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

    /// <summary>pc0010 (one Zlib-compressed .usm, deep mount point) and pc0100 (the empty stub) come back byte for byte.</summary>
    [DataTestMethod]
    [DataRow("pc0010_mobile_and.pak", 1)]
    [DataRow("pc0100_mobile_and.pak", 0)]
    public void Anchor_RebuildsStockPak(string name, int files)
    {
        var stock = File.ReadAllBytes(Path.Combine(Source.Config.PaksDir, name));
        var (contents, problems) = LegacyPak.Read(stock, Key);
        Assert.IsNotNull(contents, string.Join("\n", problems));
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        Assert.AreEqual(files, contents.Files.Count);
        CollectionAssert.AreEqual(stock, LegacyPak.Write(contents, Key));
    }

    /// <summary>A stored file written at a deep mount point: our reader and CUE4Parse (mounting only that pak) read the same bytes.</summary>
    [TestMethod]
    public void Write_ReadsBackThroughCue4Parse()
    {
        var data = new byte[70_001];
        new Random(17).NextBytes(data);
        const string Mount = "../../../PesMobile/Content/Platforms/_Android/Assets/ui/Font/";
        var contents = new PakContents(Mount, LegacyPak.SeedFor("pc9998_mobile_and_P.pak"), ["", "", "", "", ""], [new PakFile("Test-Regular.ufont", data), new PakFile("Other.ufont", [1, 2, 3])]);
        var pak = LegacyPak.Write(contents, Key);

        var (back, problems) = LegacyPak.Read(pak, Key);
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems));
        Assert.AreEqual(Mount, back!.MountPoint);
        CollectionAssert.AreEqual(data, back.Files[0].Data);
        Assert.IsNull(back.Files[0].ZlibBlock);
        CollectionAssert.AreEqual(pak, LegacyPak.Write(back, Key));

        Guard.CreateDirectory(_folder);
        Guard.WriteAllBytes(Path.Combine(_folder, "pc9998_mobile_and_P.pak"), pak);
        using var provider = new DefaultFileProvider(_folder, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_eFootball), StringComparer.Ordinal);
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(Source.Config.AesKey));
        Assert.IsTrue(provider.TrySaveAsset("PesMobile/Content/Platforms/_Android/Assets/ui/Font/Test-Regular.ufont", out var viaCue4Parse));
        CollectionAssert.AreEqual(data, viaCue4Parse);
        Assert.IsTrue(provider.TrySaveAsset("PesMobile/Content/Platforms/_Android/Assets/ui/Font/Other.ufont", out var other));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, other);
    }

    /// <summary>The path hash is stock's: pc0010's one entry (seed 0xaccf60e8).</summary>
    [TestMethod]
    public void HashPath_MatchesStock() => Assert.AreEqual(0x32f22e49f19ca90fUL, LegacyPak.HashPath("PlayerCard_Black_VP9.usm", 0xaccf60e8));

    [TestMethod]
    public void Refusals()
    {
        var none = new[] { "", "", "", "", "" };
        Assert.ThrowsException<ArgumentException>(() => LegacyPak.Write(new PakContents("../../../", 1, none, [new PakFile("a/b.ufont", [1])]), Key));
        Assert.ThrowsException<ArgumentException>(() => LegacyPak.Write(new PakContents("../../../", 1, none, [new PakFile("b.ufont", [1]), new PakFile("B.ufont", [2])]), Key));
        Assert.ThrowsException<ArgumentException>(() => LegacyPak.Write(new PakContents("../../..", 1, none, [new PakFile("b.ufont", [1])]), Key));
        Assert.ThrowsException<ArgumentException>(() => LegacyPak.Write(new PakContents("../../../", 1, none, [new PakFile("b.ufont", [1], ZlibBlock: [2])]), Key));

        var pak = LegacyPak.Write(new PakContents("../../../", 1, none, [new PakFile("b.ufont", new byte[100])]), Key);
        var flipped = (byte[])pak.Clone();
        flipped[60] ^= 1;
        StringAssert.Contains(string.Join("\n", LegacyPak.Read(flipped, Key).Problems), "SHA-1 of the stored bytes");
        var badIndex = (byte[])pak.Clone();
        badIndex[pak.Length - LegacyPak.InfoSize - 20] ^= 1;
        StringAssert.Contains(string.Join("\n", LegacyPak.Read(badIndex, Key).Problems), "SHA-1 doesn't match");
        Assert.IsNull(LegacyPak.Read(pak[..^4], Key).Contents);
    }

    /// <summary>The UCL mod's font pak (PC, index not encrypted, one Zlib block per file) parses; its font is OpenType ("OTTO").</summary>
    [TestMethod]
    public void Read_UclFontPak()
    {
        var path = @"E:\n3tron\patch\pc_mods\3RD_PARTY\PesConsole\Content\Paks\~mods\UCL_theme_by_eFootball.cz\EfTheme_Fonts_P.pak";
        if (!File.Exists(path)) Assert.Inconclusive($"{path} not found");
        var (contents, problems) = LegacyPak.Read(File.ReadAllBytes(path), Key);
        Assert.IsNotNull(contents, string.Join("\n", problems));
        Console.WriteLine(string.Join("\n", problems));
        CollectionAssert.AreEqual(new[] { "index isn't encrypted (every stock mobile pak's is)" }, problems.ToList());
        var font = contents.Files.Single(f => f.Path.EndsWith("/NotoSansThai-ExtraBold.ufont"));
        Assert.AreEqual(66_560, font.Data.Length);
        Assert.AreEqual("OTTO", System.Text.Encoding.ASCII.GetString(font.Data, 0, 4));
    }
}
