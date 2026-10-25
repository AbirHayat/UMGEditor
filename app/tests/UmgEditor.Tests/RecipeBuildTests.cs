using System.Text.Json.Nodes;
using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using UmgEditor.Core.Recipes;
using UmgEditor.Core.Textures;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Stage 2b: recipe-build (the export's one build path + Expect.Files) and recipe-from-set. Acceptance: recipes\C10, C12,
/// C18 and C16b_test build into exactly the device-tested files; recipes\C18 and C16b_test are what recipe-from-set makes
/// of projects C18 and C16b. Stage 2c: recipes\C13 and C14 (Clones + Repoints) too, with their clones and re-points
/// checked on the mounted mod. Stage 2d: recipes\C15, C16 and C16b (package Layers). Stage 2e: C17a (a pak-only mod) and
/// C17b (container + font pak). Builds go to work\out\mods\zzrb-&lt;id&gt; and are deleted.
/// </summary>
[TestClass]
public class RecipeBuildTests
{
    private static readonly WriteGuard Mods = WriteGuard.ForMods(Source.Config);
    private readonly List<string> _built = [];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var dir in _built) Mods.DeleteDirectory(dir);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "recipes"))) dir = dir.Parent;
        return dir?.FullName ?? throw new AssertFailedException("recipes\\ not found above the test folder");
    }

    private static string RecipeText(string name) => File.ReadAllText(Path.Combine(RepoRoot(), "recipes", name + ".json"));

    private static RecipeContext Context() => new(Source, RecipeContext.RootOf(Source.Config), () => TextureUsage.LoadOrBuild(Source, UiIndex),
        new UsageGraph(UiIndex), new EditImpact(Engine, new UsageGraph(UiIndex), 1786, 1000), p => WidgetTreeReader.Build(Source, p));

    private static ExportEnvironment Env() => new(Source, Engine, new UsageGraph(UiIndex), p => WidgetTreeReader.Build(Source, p), 1786, 1000, "test");

    private string ScratchName()
    {
        var name = "zzrb-" + Guid.NewGuid().ToString("N")[..8];
        _built.Add(Path.Combine(Mods.Root, name));
        return name;
    }

    private RecipeBuild.Outcome Build(string text, bool ack = false, string? name = null)
    {
        var (recipe, refusals) = RecipeReader.Read(text);
        Assert.IsNotNull(recipe, string.Join("\n", refusals));
        return RecipeBuild.Run(recipe, text, Context(), Env(), new RetocProcess(Source.Config.RetocPath), name ?? ScratchName(), ack);
    }

    private static void NeedsInputs(string name)
    {
        var (recipe, _) = RecipeReader.Read(RecipeText(name));
        foreach (var input in recipe!.InputsOrEmpty.Values)
            if (!File.Exists(Path.Combine(RecipeContext.RootOf(Source.Config), input.Path))) Assert.Inconclusive($"{name}: input {input.Path} is missing");
        if (!File.Exists(Source.Config.RetocPath)) Assert.Inconclusive("retoc is missing");
    }

    // ---- acceptance ----

    [TestMethod]
    [DataRow("C10", false)]
    [DataRow("C12", false)]
    [DataRow("C18", false)]
    [DataRow("C16b_test", true)]
    [DataRow("C13", false)]
    [DataRow("C14", false)]
    [DataRow("C15", false)]
    [DataRow("C16", false)]
    [DataRow("C16b", false)]
    [DataRow("C17a", false)]
    [DataRow("C17b", false)]
    public void Acceptance_RecipeBuildsTheDeviceTestedFiles(string name, bool ack)
    {
        NeedsInputs(name);
        var text = RecipeText(name);
        var outcome = Build(text, ack);
        Assert.AreEqual(RecipeBuild.Done, outcome.Code, string.Join("\n", outcome.Refusals));
        var pinned = RecipeReader.Read(text).Recipe!.Expect!.Files!;
        CollectionAssert.AreEquivalent(pinned.Select(kv => $"{kv.Key} {kv.Value}").ToList(), outcome.Result!.Files.Select(f => $"{f.Name} {f.Sha256}").ToList());
        foreach (var f in outcome.Result.Files)
            Assert.AreEqual(f.Sha256, RecipeBuild.Sha256(File.ReadAllBytes(Path.Combine(outcome.Result.ModDir, f.Name))), $"{f.Name} on disk");
        Assert.AreEqual(text, File.ReadAllText(Path.Combine(outcome.Result.OutDir, RecipeBuild.RecipeCopy)), "the recipe built is kept next to mod\\");
        Assert.IsTrue(File.Exists(outcome.Result.InstructionsPath));
        var disable = File.ReadAllText(Path.Combine(outcome.Result.OutDir, DisableScript.FileName));
        foreach (var f in outcome.Result.Files) StringAssert.Contains(disable, $"Name = '{f.Name}'; Size = {f.Size}; Sha256 = '{f.Sha256}'");
        Assert.AreEqual(outcome.Result.Files.Count, disable.Split("; Sha256 = '").Length - 1, "the script lists exactly the device files");
        Console.WriteLine($"{name}: {string.Join(", ", outcome.Result.Files.Select(f => $"{f.Name} {f.Sha256[..12]}…"))}");
    }

    /// <summary>The mounted-mod check of C13's build passes, and fails for a path the mod doesn't name or pixels it doesn't hold.</summary>
    [TestMethod]
    public void MountCheck_SeesTheClonesAndTheRepoint()
    {
        NeedsInputs("C13");
        var outcome = Build(RecipeText("C13"));
        Assert.AreEqual(RecipeBuild.Done, outcome.Code, string.Join("\n", outcome.Refusals));
        var plan = outcome.Plan!;
        var modDir = outcome.Result!.ModDir;
        Assert.AreEqual(2, plan.ExpectedTextures.Count);
        Assert.IsNull(ExportRunner.TextureMountCheck(Source, plan, modDir));
        var (package, names) = plan.ExpectedNames.Single();
        StringAssert.Contains(ExportRunner.TextureMountCheck(Source, plan with { ExpectedNames = [(package, names.Replace("UCL01", "UCL02"))] }, modDir), "no export in DemoTitleBase names");
        var (clone, mip0) = plan.ExpectedTextures[0];
        StringAssert.Contains(ExportRunner.TextureMountCheck(Source, plan with { ExpectedTextures = [(clone, new byte[mip0.Length])] }, modDir), "doesn't decode");
    }

    /// <summary>A loose-file pak that doesn't give back its files when mounted alone fails the build: mod\ removed, FAILED.txt.</summary>
    [TestMethod]
    public void LoosePak_ThatDoesNotReadBack_FailsTheBuild()
    {
        NeedsInputs("C17a");
        var outcome = Build(RecipeText("C17a"));
        Assert.AreEqual(RecipeBuild.Done, outcome.Code, string.Join("\n", outcome.Refusals));
        var plan = outcome.Plan!;
        var pak = plan.LoosePaks.Single();
        Assert.IsNotNull(ExportRunner.WriteLoosePaks(Source, plan, outcome.Result!.ModDir));
        // the plan says the font holds other bytes than the pak does
        var wrong = plan with { LoosePaks = [pak with { Files = [pak.Files[0] with { Data = new byte[pak.Files[0].Data.Length] }] }] };
        Assert.IsNull(ExportRunner.WriteLoosePaks(Source, wrong, outcome.Result.ModDir));
        Assert.IsFalse(Directory.Exists(outcome.Result.ModDir), "mod\\ must be removed");
        StringAssert.Contains(File.ReadAllText(Path.Combine(outcome.Result.OutDir, "FAILED.txt")), "not read back");
        Assert.IsFalse(Directory.Exists(Path.Combine(outcome.Result.OutDir, "_pakprobe")), "the probe folder is deleted");
    }

    // ---- refusals ----

    [TestMethod]
    public void Danger_WithoutAck_NeedsAck_AndWritesNothing()
    {
        var name = ScratchName();
        var outcome = Build(RecipeTests.C8Recipe(), ack: false, name: name);
        Assert.AreEqual(RecipeBuild.NeedsAck, outcome.Code);
        Assert.IsTrue(outcome.Refusals.Single().Contains("--ack-danger"), outcome.Refusals.Single());
        Assert.IsFalse(Directory.Exists(Path.Combine(Mods.Root, name)));
    }

    [TestMethod]
    public void PinnedFileDiffers_ModIsRemoved_Exit4()
    {
        NeedsInputs("C10");
        var o = JsonNode.Parse(RecipeText("C10"))!.AsObject();
        o["Expect"]!["Files"]!["pc9999_mobile_and_P.ucas"] = new string('c', 64);
        var outcome = Build(o.ToJsonString());
        Assert.AreEqual(RecipeBuild.PinMismatch, outcome.Code);
        Assert.IsTrue(outcome.Refusals.Any(r => r.Contains("pinned cccccccccccc")), string.Join("\n", outcome.Refusals));
        Assert.IsFalse(Directory.Exists(outcome.Result!.ModDir), "mod\\ must be removed");
        Assert.IsTrue(File.Exists(Path.Combine(outcome.Result.OutDir, "FAILED.txt")));

        // a built file that isn't pinned is a mismatch too
        var fewer = JsonNode.Parse(RecipeText("C10"))!.AsObject();
        fewer["Expect"]!["Files"]!.AsObject().Remove("pc9999_mobile_and_P.pak");
        var second = Build(fewer.ToJsonString());
        Assert.AreEqual(RecipeBuild.PinMismatch, second.Code);
        Assert.IsTrue(second.Refusals.Any(r => r.Contains("built but not pinned")));
    }

    [TestMethod]
    public void CheckRefusal_OrBadName_BuildsNothing()
    {
        var o = JsonNode.Parse(RecipeText("C10"))!.AsObject();
        o["Packages"]![0]!["StockSha256"] = new string('0', 64);
        var name = ScratchName();
        var refused = Build(o.ToJsonString(), name: name);
        Assert.AreEqual(RecipeBuild.Refused, refused.Code);
        Assert.IsNull(refused.Plan);
        Assert.IsFalse(Directory.Exists(Path.Combine(Mods.Root, name)));

        var badName = Build(RecipeText("C10"), name: "..\\outside");
        Assert.AreEqual(RecipeBuild.Refused, badName.Code);
        Assert.IsTrue(badName.Refusals.Any(r => r.Contains("plain folder name")), string.Join("\n", badName.Refusals));
    }

    // ---- recipe-from-set ----

    private static EditorController Project(string name) =>
        new(Source, new UsageGraph(UiIndex), Engine, p => WidgetTreeReader.Build(Source, p), project: name, textureUsage: () => TextureUsage.LoadOrBuild(Source, UiIndex));

    private static string Hashes(string folder) => string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order()
        .Select(f => f + " " + RecipeBuild.Sha256(File.ReadAllBytes(f))));

    /// <summary>The committed recipes C18 and C16b_test are what recipe-from-set makes of projects C18 and C16b (plus the pins), and it writes nothing into the project.</summary>
    [TestMethod]
    [DataRow("C18", "C18")]
    [DataRow("C16b", "C16b_test")]
    public void FromSet_GivesTheCommittedRecipe(string project, string recipeName)
    {
        var folder = Path.Combine(Source.Config.EditsDir, project);
        if (!Directory.Exists(folder)) Assert.Inconclusive($"project {project} is missing");
        var before = Hashes(folder);
        var (made, refusals) = RecipeFromSet.Make(Project(project), RecipeContext.RootOf(Source.Config), recipeName, "pc9999_mobile_and_P");
        Assert.IsNotNull(made, string.Join("\n", refusals));
        Assert.AreEqual(before, Hashes(folder), "recipe-from-set wrote into the project");

        var committed = RecipeReader.Read(RecipeText(recipeName)).Recipe!;
        // the same edits; the committed C18 names its art by its copy in work\out\art (same sha256), so inputs compare by hash
        Assert.AreEqual(Json(committed.PackagesOrEmpty), Json(made.PackagesOrEmpty));
        string Textures(Recipe r) => Json(r.TexturesOrEmpty.Select(t => t with { Input = t.Input is { } id ? r.InputsOrEmpty[id].Sha256 : null }));
        Assert.AreEqual(Textures(committed), Textures(made));
        CollectionAssert.AreEquivalent(committed.InputsOrEmpty.Values.Select(i => i.Sha256).ToList(), made.InputsOrEmpty.Values.Select(i => i.Sha256).ToList());
    }

    private static string Json(object o) => System.Text.Json.JsonSerializer.Serialize(o, new System.Text.Json.JsonSerializerOptions
        { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });

    [TestMethod]
    public void FromSet_RefusesUnsavedWork()
    {
        var name = "zzrecipe" + Guid.NewGuid().ToString("N")[..6];
        var guard = new WriteGuard(Source.Config);
        try
        {
            var c = Project(name);
            var home = WidgetTreeReader.Build(Source, Tv + "MatchTime/MatchTime").Find("Text_Score_Home")!;
            var s = c.Session(Tv + "MatchTime/MatchTime");
            Assert.IsNull(s.Apply(SlotEditor.SetField(home, s.Current, "LayoutData.Offsets.Left", 20)));
            var (recipe, refusals) = RecipeFromSet.Make(c, RecipeContext.RootOf(Source.Config), name, "pc9999_mobile_and_P");
            Assert.IsNull(recipe);
            Assert.IsTrue(refusals.Single().Contains("unsaved"), refusals.Single());
        }
        finally { guard.DeleteDirectory(Path.Combine(guard.Root, name)); }
    }
}
