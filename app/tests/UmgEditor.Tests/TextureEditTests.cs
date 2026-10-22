using System.Security.Cryptography;
using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using UmgEditor.Core.Textures;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Step 3 (textures you replace yourself): texture edits = clones + re-points planned from a project's textures.json.
/// Anchor: C16's texture packages, made again through texture edits from PNGs, are byte-identical to the chunks that
/// were tested on the device (work\out\mods\C16, 2026-10-21). Each test uses its own folder under work\edits\_tests.
/// </summary>
[TestClass]
public class TextureEditTests
{
    private const string MT = Tv + "MatchTime/", DT = Tv + "DemoTitle/";
    private const string Logo = "/Game/Assets/ui/Data/Widget/General/Modals/ExtraLoginBonus/Tex720/";

    /// <summary>sha256 of C16's device-tested chunks (work\out\mods\C16\raw\chunks) that hold only texture changes.</summary>
    private static readonly Dictionary<string, string> C16Chunks = new()
    {
        [MT + "Tex720/MatchTimeBarUC_0"] = "058a7f45b1571b057910ac241b5d6ae9b3f8446de31ef40d95514f93c8bd37fc",
        [MT + "0164F/Tex720/MatchTimeBarUC_0"] = "b7ea4fc33b961aa65535e9abe8d3bbe625d56706c37d24ed3f086709a6ef70d3",
        [MT + "Tex720/MatchTimeNavyG_0"] = "e870154849749c6d307bee7d6f96d991cea5b9391f9738bb857d7123fed3c82b",
        [MT + "0164F/Tex720/MatchTimeNavyG_0"] = "b8678a04cec6e46c121221d8c051120fccf564a3482784b3af33c8e6a441a41c",
        [MT + "Tex720/MatchTimeWhite_1"] = "a459497bbdf94f512735614c11c7a12da32bb9e10b8bc5f41f2daf9ec25899cc",
        [MT + "0164F/Tex720/MatchTimeWhite_1"] = "04fff800dc11794bfa9174de41acd526dd349cdc52d2c536fa0652ee850d69dc",
        [MT + "Tex720/MatchTimeWhite_2"] = "10b44dbaf9fda177c61d6fe7dc7c477d6685c8f17e1bac4c18bf923e3136fc8a",
        [MT + "0164F/Tex720/MatchTimeWhite_2"] = "58fa951b155d4ad970ab0ef6ce3dc6855d966f3463ea2bf9039b687db569dd91",
        [DT + "Tex720/DemoTitleUCL01"] = "6c92fd749e6be90906b746aff1fd80d841057491ac13b8c5f163f7950f01e227",
        [DT + "0164F/Tex720/DemoTitleUCL01"] = "86478e83825c13ba7d94aaac8b17cb3ed93c1891c35b1e553f4aa94d244da304",
        [Logo + "ExtraLoginBonusUCLb"] = "066d0ba30f97eaf2a62f15f91e54ea496d26bf8310d4f5e12d46363fb7248da8",
        [MT + "MatchTimeBase"] = "e58e2f07879e8991046ecb84e1855c51c3bbd2df0b2e63390150d3e7808b87d7",
        [MT + "MatchTimeBase_1"] = "5b379ed4cdb7fad6d0ea9540869450748357a6cbe730735d3132578c1a7d3a04",
        [MT + "MatchTimeBase_2"] = "82d4bc1ade38e8c48effcb99b8cf89c2cf7acf9db7ee3006c8def8cba66c272a",
        [DT + "DemoTitleBase"] = "c26d96fd4fad7fca5144f39a31e87d699d8be317b8dfd03428fab122107ca56a",
    };

    private static WriteGuard Guard => new(Source.Config);
    private static TextureUsage Usage => TextureUsageTests.Usage;
    private string _folder = null!, _scratch = null!;

    [TestInitialize]
    public void NewFolder()
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        _folder = Path.Combine(Guard.Root, "_tests", id);
        _scratch = Path.Combine(Path.GetTempPath(), "texedit_" + id);
        Directory.CreateDirectory(_scratch);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_folder)) Guard.DeleteDirectory(_folder);
        var parent = Path.GetDirectoryName(_folder)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Guard.DeleteDirectory(parent);
        Directory.Delete(_scratch, true);
    }

    /// <summary>C16's art (raw .bgra from work\out\art\C16) written as a PNG, imported into the project.</summary>
    private (string Relative, string Sha) ImportC16Art(string file, int w, int h)
    {
        var bgra = Path.Combine(Source.Config.WorkDir, "out", "art", "C16", file);
        if (!File.Exists(bgra)) Assert.Inconclusive($"C16 art not found: {bgra}");
        var png = Path.Combine(_scratch, Path.ChangeExtension(file, ".png"));
        PngWriter.Write(png, new DecodedImage(w, h, File.ReadAllBytes(bgra), "test"));
        var (relative, sha, image, why) = TextureEditStore.Import(Guard, _folder, png);
        Assert.IsNotNull(relative, why);
        CollectionAssert.AreEqual(File.ReadAllBytes(bgra), image!.Bgra, $"{file}: the PNG reads back as the raw art");
        return (relative, sha!);
    }

    private TextureEdit Image(string package, string widget, string texture, string newName, (string Relative, string Sha) art, bool margins = false, string? source = null) =>
        new(package, widget, texture, TextureEditMode.Image, newName, Image: art.Relative, ImageSha256: art.Sha, MarginsZero: margins, Source: source);

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [TestMethod]
    public void C16Textures_ThroughTextureEdits_AreByteIdenticalToTheDeviceTestedChunks()
    {
        var bar = ImportC16Art("MatchTimeBarUC_0_699x96.bgra", 699, 96);
        var navy = ImportC16Art("MatchTimeNavyG_0_512x64.bgra", 512, 64);
        var goal = ImportC16Art("DemoTitleUCL01_696x192.bgra", 696, 192);
        var logo = ImportC16Art("ExtraLoginBonusUCLb_80x80.bgra", 80, 80);
        var edits = new List<TextureEdit>
        {
            Image(MT + "MatchTime", "Img_Line", MT + "Tex720/MatchTimePlate_0", "MatchTimeBarUC_0", bar),
            Image(MT + "MatchTime", "Img_Logo", MT + "Tex720/MatchTimeLogo", "ExtraLoginBonusUCLb", logo, source: Logo + "ExtraLoginBonusInfo"),
            new(MT + "MatchTimeBase_1", "Img_Plate", MT + "Tex720/MatchTimePlate_1", TextureEditMode.White, "MatchTimeWhite_1"),
            new(MT + "MatchTimeBase_2", "Img_Plate", MT + "Tex720/MatchTimePlate_2", TextureEditMode.White, "MatchTimeWhite_2"),
            Image(MT + "MatchTimeBase", "Img_Plate", MT + "Tex720/MatchTimePlate_0", "MatchTimeNavyG_0", navy, margins: true),
            Image(DT + "DemoTitleBase", "Img_Plate", DT + "Tex720/DemoTitlePlate", "DemoTitleUCL01", goal, margins: true),
        };
        TextureEditStore.Save(Guard, _folder, edits);
        var loaded = TextureEditStore.Load(_folder);
        CollectionAssert.AreEqual(edits, loaded.ToList(), "textures.json round trip");

        var plan = TextureEdits.Plan(Source, Usage, _folder, loaded);
        foreach (var l in plan.Log) Console.WriteLine(l);
        Assert.IsTrue(plan.Ok, string.Join("\n", plan.Refusals));
        var raw = Path.Combine(Source.Config.WorkDir, "out", "mods", "C16", "raw", "chunks");
        foreach (var p in plan.All)
        {
            Console.WriteLine($"{p.PackagePath}  {p.ChunkIdHex}  {Sha(p.Chunk)}{(p.CloneOf != null ? "  clone of " + GamePaths.ShortName(p.CloneOf) : "")}");
            if (p.PackagePath == MT + "MatchTime") continue; // C16's MatchTime also has colour, clipping and edit-set layers
            Assert.IsTrue(C16Chunks.TryGetValue(p.PackagePath, out var pinned), $"{p.PackagePath} isn't one of C16's texture packages");
            Assert.AreEqual(pinned, Sha(p.Chunk), $"{p.PackagePath} differs from the device-tested C16 chunk");
            var file = Path.Combine(raw, p.ChunkIdHex);
            if (File.Exists(file)) CollectionAssert.AreEqual(File.ReadAllBytes(file), p.Chunk, $"{p.PackagePath}: raw chunk");
        }
        CollectionAssert.AreEquivalent(C16Chunks.Keys.ToList(), plan.All.Select(p => p.PackagePath).Where(p => p != MT + "MatchTime").ToList(), "exactly C16's texture packages");
        Assert.AreEqual(11, plan.Expected.Count);

        var barInfo = plan.Items[0];
        Assert.AreEqual(MT + "Tex720/MatchTimePlate_0.MatchTimePlate", barInfo.From);
        Assert.AreEqual(MT + "Tex720/MatchTimeBarUC_0.MatchTimeBarUC", barInfo.To);
        Assert.AreEqual((699, 96), (barInfo.Width, barInfo.Height));
        Assert.IsTrue(barInfo.Warnings.Any(w => w.Contains("MatchTimeConnection") && w.Contains("keeps the stock image")), "other users of the bar texture are listed");
    }

    private static byte[] Solid(int w, int h, byte r, byte g, byte b, byte a = 255) =>
        Enumerable.Range(0, w * h).SelectMany(_ => new[] { b, g, r, a }).ToArray();

    private (string Relative, string Sha) ImportSolid(int w, int h, byte r, byte g, byte b)
    {
        var png = Path.Combine(_scratch, $"solid_{w}x{h}_{r}{g}{b}.png");
        PngWriter.Write(png, new DecodedImage(w, h, Solid(w, h, r, g, b), "test"));
        var (relative, sha, _, why) = TextureEditStore.Import(Guard, _folder, png);
        Assert.IsNotNull(relative, why);
        return (relative, sha!);
    }

    [TestMethod]
    public void Repoint_OnTopOfASavedLayoutEdit_KeepsBoth()
    {
        // a layout edit saved in the same project folder, then the bar texture on the same package
        var session = PatchSession.Open(Source, MT + "MatchTime", PatchSession.DefaultFolder(Guard, MT + "MatchTime", Path.GetRelativePath(Guard.Root, _folder)));
        var home = WidgetTreeReader.Build(Source, MT + "MatchTime").Find("Text_Score_Home")!;
        Assert.IsNull(session.Apply(SlotEditor.SetField(home, session.Current, "LayoutData.Offsets.Top", 3)));
        Assert.IsTrue(session.Save().Saved);
        var (saved, why) = session.ReadSavedChunk();
        Assert.IsNotNull(saved, why);

        var red = ImportSolid(64, 8, 255, 0, 0);
        var plan = TextureEdits.Plan(Source, Usage, _folder, [Image(MT + "MatchTime", "Img_Line", MT + "Tex720/MatchTimePlate_0", "MatchTimePlaU1_0", red)],
            p => p == MT + "MatchTime" ? saved : null);
        foreach (var l in plan.Log.Where(l => !l.StartsWith("  "))) Console.WriteLine(l);
        Assert.IsTrue(plan.Ok, string.Join("\n", plan.Refusals));
        var board = plan.Packages.Single();
        var exports = Source.ParsePackage(board.FileKey, board.Chunk).ExportsLazy.Select(e => e.Value).ToList();
        var values = exports.Select(WidgetTreeReader.StoredValues).ToList();
        Assert.IsTrue(values.Any(v => v.GetValueOrDefault("texture") == MT + "Tex720/MatchTimePlaU1_0.MatchTimePlaU1_0"), "Img_Line names the clone");
        Assert.AreEqual(2, plan.Clones.Count, "both theme copies");
        StringAssert.Contains(string.Join("\n", plan.Log), "union of 2 layer(s)");
        // the layout edit survived: the re-parsed slot holds Top = 3
        var slot = WidgetTreeReader.Build(Source, MT + "MatchTime").Find("Text_Score_Home")!.Slot!;
        Assert.AreEqual("3", values[slot.ExportIndex].GetValueOrDefault("LayoutData.Offsets.Top"));
    }

    [TestMethod]
    public void AllUsers_RepointsEveryPackageNamingTheTexture_WidgetScopeOnlyOne()
    {
        var blue = ImportSolid(8, 8, 0, 0, 255);
        var one = TextureEdits.Plan(Source, Usage, _folder, [Image(MT + "MatchTime", "Img_Logo", MT + "Tex720/MatchTimeLogo", "ExtraLoginBonusU1fo", blue, source: Logo + "ExtraLoginBonusInfo")]);
        Assert.IsTrue(one.Ok, string.Join("\n", one.Refusals));
        CollectionAssert.AreEqual(new[] { MT + "MatchTime" }, one.Packages.Select(p => p.PackagePath).ToList());
        Assert.IsTrue(one.Items[0].Warnings.Any(w => w.StartsWith("MatchTimeConnection also names MatchTimeLogo")), string.Join("\n", one.Items[0].Warnings));

        var all = TextureEdits.Plan(Source, Usage, _folder, [Image(MT + "MatchTime", "Img_Logo", MT + "Tex720/MatchTimeLogo", "ExtraLoginBonusU1fo", blue, source: Logo + "ExtraLoginBonusInfo") with { Scope = TextureEditScope.AllUsers }]);
        Assert.IsTrue(all.Ok, string.Join("\n", all.Refusals));
        CollectionAssert.AreEquivalent(new[] { MT + "MatchTime", Tv + "MatchTimeConnection/MatchTimeConnection" }, all.Packages.Select(p => p.PackagePath).ToList());
        Assert.AreEqual(1, all.Clones.Count, "one clone for both users");
    }

    [TestMethod]
    public void SuggestName_IsSameLengthUnusedAndKeepsTheSuffix()
    {
        var name = TextureEdits.SuggestName(Source, MT + "Tex720/MatchTimePlate_0", []);
        Assert.AreEqual("MatchTimePlate_0".Length, name.Length);
        Assert.IsTrue(name.EndsWith("_0"), name);
        Assert.AreNotEqual(name, TextureEdits.SuggestName(Source, MT + "Tex720/MatchTimePlate_0", [name]));
        Console.WriteLine(name);
    }

    [TestMethod]
    public void Sources_ForTheEtc2Logo_IncludeC16sAndFitThePathLength()
    {
        var logo = Usage.Of(MT + "MatchTime", "Img_Logo")!;
        StringAssert.Contains(TextureEdits.SourceRefusal(Source, logo.Texture), "only PF_B8G8R8A8");
        var sources = TextureEdits.Sources(Source, UiIndex, logo);
        Console.WriteLine($"{sources.Count} sources: {string.Join(", ", sources.Take(10).Select(GamePaths.ShortName))}");
        CollectionAssert.Contains(sources.ToList(), Logo + "ExtraLoginBonusInfo");
        Assert.IsTrue(sources.All(s => TextureEdits.NewNameMapPath(s, GamePaths.ShortName(s)).Length == logo.NameMapPath.Length));
    }

    [TestMethod]
    public void Refusals()
    {
        var red = ImportSolid(8, 8, 255, 0, 0);
        string Refused(params TextureEdit[] edits)
        {
            var plan = TextureEdits.Plan(Source, Usage, _folder, edits);
            Assert.IsFalse(plan.Ok, "expected a refusal");
            Console.WriteLine(string.Join("\n", plan.Refusals));
            return string.Join("\n", plan.Refusals);
        }
        var bar = Image(MT + "MatchTime", "Img_Line", MT + "Tex720/MatchTimePlate_0", "MatchTimePlaU1_0", red);
        StringAssert.Contains(Refused(bar with { Widget = "Text_Score_Home" }), "isn't a uiImage with a stored texture");
        StringAssert.Contains(Refused(bar with { Texture = MT + "Tex720/MatchTimePlate_1" }), "the edit was made for");
        StringAssert.Contains(Refused(bar with { NewName = "MatchTimePlaU12_0" }), "must be as long as");
        StringAssert.Contains(Refused(bar with { NewName = "MatchTimePlate_1" }), "already exists");
        StringAssert.Contains(Refused(Image(MT + "MatchTime", "Img_Logo", MT + "Tex720/MatchTimeLogo", "MatchTimeLoU1", red)), "only PF_B8G8R8A8");
        StringAssert.Contains(Refused(Image(MT + "MatchTime", "Img_Logo", MT + "Tex720/MatchTimeLogo", "MatchTimePlaU1_0", red, source: MT + "Tex720/MatchTimePlate_0")), "must keep its length");
        StringAssert.Contains(Refused(bar, bar with { Widget = "Img_Line" }), "already re-pointed");
        var green = ImportSolid(8, 8, 0, 255, 0);
        StringAssert.Contains(Refused(bar, Image(MT + "MatchTimeBase", "Img_Plate", MT + "Tex720/MatchTimePlate_0", "MatchTimePlaU1_0", green)), "with other pixels");
        StringAssert.Contains(Refused(bar with { ImageSha256 = new string('0', 64) }), "changed since it was imported");
        StringAssert.Contains(Refused(bar with { Image = "_art/missing.png" }), "missing from the project");
        StringAssert.Contains(Refused(bar with { Package = "/Game/Assets/ui/library/core/uiImage" }), "library widgets");
    }

    // ---- through the controller (what umgmod and the app drive) ----

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private EditorController Controller(string project) =>
        new(Source, new UsageGraph(UiIndex), Engine, p => WidgetTreeReader.Build(Source, p), project: project, textureUsage: () => Usage);

    [TestMethod]
    public void Controller_AskFirst_UndoRedo_Save_Reopen_AndExportMountsTheClone()
    {
        var project = "zztest" + Guid.NewGuid().ToString("N")[..10];
        var mod = "test-" + project;
        try
        {
            var c = Controller(project);
            var png = Path.Combine(_scratch, "logo.png");
            PngWriter.Write(png, new DecodedImage(80, 80, Solid(80, 80, 20, 200, 90), "test"));
            var (edit, image, why) = c.PrepareTextureEdit(MT + "MatchTime", "Img_Logo", TextureEditMode.Image, png, source: Logo + "ExtraLoginBonusInfo");
            Assert.IsNotNull(edit, why);
            Assert.AreEqual(19, edit.NewName.Length, "a suggested same-length name");
            Assert.AreEqual((80, 80), (image!.Width, image.Height));

            var first = c.ApplyTextureEdit(edit);
            Assert.IsTrue(first.NeedsConfirmation, "the first edit of a package asks");
            StringAssert.Contains(first.ConfirmationText, "MatchTimeConnection also names MatchTimeLogo");
            Assert.IsTrue(c.ApplyTextureEdit(edit, confirmed: true).Applied);
            Assert.IsTrue(c.TexturesDirty && c.AnyDirty);
            StringAssert.Contains(c.UnsavedPackagesText, "texture edits");
            Assert.AreEqual(EditorController.TexturesStep, c.NextUndo);
            Assert.IsTrue(c.Undo());
            Assert.AreEqual(0, c.TextureEdits.Count);
            Assert.IsTrue(c.Redo());
            Assert.AreEqual(edit, c.TextureEdits.Single());

            // a layout edit in the same package, then save everything
            var tree = WidgetTreeReader.Build(Source, MT + "MatchTime");
            var home = tree.Find("Text_Score_Home")!;
            Assert.IsTrue(c.Apply(tree, home, e => SlotEditor.SetField(home, e, "LayoutData.Offsets.Top", 3)).Applied, "no second prompt for the package");
            Assert.IsTrue(c.SaveAll().All(r => r.Result.Saved));
            Assert.IsFalse(c.AnyDirty);
            var reopened = Controller(project);
            Assert.AreEqual(edit, reopened.TextureEdits.Single(), "textures.json reloads");

            var candidates = reopened.ExportCandidates();
            Assert.AreEqual(1, candidates.Single().TextureEdits);
            var plan = reopened.PlanExport(new ExportRequest(mod, "pc9999_mobile_and_P", [MT + "MatchTime"], Source.Config.RetocPath));
            Assert.IsTrue(plan.Ok, string.Join("\n", plan.Refusals));
            CollectionAssert.AreEquivalent(new[] { MT + "MatchTime", Logo + edit.NewName }, plan.Packages.Select(p => p.PackagePath).ToList());
            var log = new List<string>();
            var result = reopened.RunExport(plan, new RetocProcess(plan.Retoc.Path), new SyncProgress(log.Add));
            foreach (var l in log) Console.WriteLine(l);
            Assert.IsTrue(result.Built, string.Join("\n", result.Refusals));
            var instructions = File.ReadAllText(result.InstructionsPath!);
            StringAssert.Contains(instructions, "## Textures");
            StringAssert.Contains(instructions, $"MatchTimeLogo → **{edit.NewName}**");
            StringAssert.Contains(instructions, "1 new texture package(s) load by path and decode to the images");
            StringAssert.Contains(instructions, "the new image on MatchTime/Img_Logo");
        }
        finally
        {
            Guard.DeleteDirectory(Path.Combine(Guard.Root, project));
            var mods = WriteGuard.ForMods(Source.Config);
            mods.DeleteDirectory(Path.Combine(mods.Root, mod));
        }
    }

    [TestMethod]
    public void InspectTexture_TheLogo_UsersSourcesAndOverrides()
    {
        var project = "zztest" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            var c = Controller(project);
            var board = WidgetTreeReader.Build(Source, MT + "MatchTime");
            Assert.IsNull(c.InspectTexture(board.Find("Text_Score_Home")!), "only image widgets have a texture section");
            var logo = c.InspectTexture(board.Find("Img_Logo")!)!;
            Assert.AreEqual(MT + "Tex720/MatchTimeLogo", logo.Texture);
            Assert.AreEqual((80, 80, "PF_ETC2_RGB"), (logo.Width, logo.Height, logo.Format));
            StringAssert.Contains(logo.CloneRefusal, "only PF_B8G8R8A8");
            CollectionAssert.Contains(logo.Sources.ToList(), Logo + "ExtraLoginBonusInfo");
            Assert.AreEqual(2, logo.Users.Count);
            Assert.IsTrue(logo.Users[0].IsThis, "this widget first");
            Assert.IsTrue(logo.Users[1].IsOnline && logo.Users[1].PackageName == "MatchTimeConnection");
            Assert.IsNull(logo.Edit);

            // the plate in MatchTimeBase: shown in 8 packages; stored 9-slice margins
            var plate = c.InspectTexture(WidgetTreeReader.Build(Source, MT + "MatchTimeBase").Find("Img_Plate")!)!;
            Assert.IsNull(plate.CloneRefusal);
            Assert.IsTrue(plate.Margins.Any);
            StringAssert.Contains(plate.Users.Single(u => u.IsThis).ShownIn, "shown in 8:");
            Assert.AreEqual(5, plate.Users.Count, "MatchTimePlate_0's five references");

            // overrides: a trial on the logo draws only on MatchTime's boxes; an applied white plate shows its white pixels
            var png = Path.Combine(_scratch, "logo.png");
            PngWriter.Write(png, new DecodedImage(8, 8, Solid(8, 8, 1, 2, 3), "test"));
            var (trial, image, why) = c.PrepareTextureEdit(MT + "MatchTime", "Img_Logo", TextureEditMode.Image, png, source: Logo + "ExtraLoginBonusInfo");
            Assert.IsNotNull(trial, why);
            var (white, _, why2) = c.PrepareTextureEdit(MT + "MatchTimeBase_1", "Img_Plate", TextureEditMode.White);
            Assert.IsNotNull(white, why2);
            Assert.IsTrue(c.ApplyTextureEdit(white, confirmed: true).Applied);
            var overrides = c.OverridesOf(null, (trial, image!));
            Assert.AreEqual(2, overrides.Count);
            var layout = Engine.Arrange(board, 1786, 1000);
            var logoBox = layout.Find("Img_Logo")!;
            Assert.IsTrue(overrides.For(logoBox) is { IsTrial: true } o && ReferenceEquals(o.Image, image));
            var whiteImage = overrides.All.Single(x => !x.IsTrial).Image;
            Assert.IsTrue(whiteImage.Bgra.Where((_, i) => i % 4 != 3).All(b => b == 255), "white: RGB 255");
            Assert.AreEqual(TextureEditMode.White, c.InspectTexture(WidgetTreeReader.Build(Source, MT + "MatchTimeBase_1").Find("Img_Plate")!)!.Edit?.Mode);

            // the plan is cached: the same edits on the same saved edit sets give the same object
            Assert.AreSame(c.PlanTextures(), c.PlanTextures());
        }
        finally { Guard.DeleteDirectory(Path.Combine(Guard.Root, project)); }
    }

    [TestMethod]
    public void Export_AnAllUsersEdit_NeedsEveryPackageItRepoints()
    {
        var project = "zztest" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            var c = Controller(project);
            var png = Path.Combine(_scratch, "logo.png");
            PngWriter.Write(png, new DecodedImage(8, 8, Solid(8, 8, 0, 0, 255), "test"));
            var (edit, _, why) = c.PrepareTextureEdit(MT + "MatchTime", "Img_Logo", TextureEditMode.Image, png, source: Logo + "ExtraLoginBonusInfo", scope: TextureEditScope.AllUsers);
            Assert.IsNotNull(edit, why);
            Assert.IsTrue(c.ApplyTextureEdit(edit, confirmed: true).Applied);
            Assert.IsTrue(c.SaveTextureEdits());
            CollectionAssert.AreEquivalent(new[] { MT + "MatchTime", Tv + "MatchTimeConnection/MatchTimeConnection" }, c.ExportCandidates().Select(x => x.PackagePath).ToList());
            var plan = c.PlanExport(new ExportRequest("test-" + project, "pc9999_mobile_and_P", [MT + "MatchTime"], Source.Config.RetocPath));
            StringAssert.Contains(string.Join("\n", plan.Refusals), "also re-points MatchTimeConnection: pick it too");
            Assert.IsTrue(c.RemoveTextureEdit(MT + "MatchTime", "Img_Logo"));
            Assert.IsTrue(c.SaveTextureEdits());
            Assert.IsFalse(File.Exists(Path.Combine(c.ProjectFolder, TextureEditStore.FileName)), "an empty list removes textures.json");
        }
        finally { Guard.DeleteDirectory(Path.Combine(Guard.Root, project)); }
    }
}
