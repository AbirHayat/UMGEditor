using System.Text.Json.Nodes;
using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Patching;
using UmgEditor.Core.Recipes;
using UmgEditor.Core.Textures;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Stage 2a: the recipe format (strict, data only) and recipe-check (read only, in memory). Acceptance: recipes\C10,
/// C12 and C18 (device-tested: an inserted tag; 23 values + 2 tags + key/range edits; the appended + pointed texture
/// route) give exactly their pinned chunks.
/// </summary>
[TestClass]
public class RecipeTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "recipes"))) dir = dir.Parent;
        return dir?.FullName ?? throw new AssertFailedException("recipes\\ not found above the test folder");
    }

    private static string RecipeText(string name) => File.ReadAllText(Path.Combine(RepoRoot(), "recipes", name + ".json"));

    private static RecipeContext? _context;
    private static RecipeContext Context() => _context ??= new RecipeContext(Source, RecipeContext.RootOf(Source.Config),
        () => TextureUsage.LoadOrBuild(Source, UiIndex), new UsageGraph(UiIndex), new EditImpact(Engine, new UsageGraph(UiIndex), 1786, 1000),
        p => WidgetTreeReader.Build(Source, p));

    private static Recipe Parse(string json)
    {
        var (recipe, refusals) = RecipeReader.Read(json);
        Assert.IsNotNull(recipe, string.Join("\n", refusals));
        return recipe;
    }

    private static RecipeCheckResult Check(string json) => RecipeCheck.Run(Parse(json), Context());

    /// <summary>Every file under work\edits and work\out with its size and time: a check must leave it as it was.</summary>
    private static string Listing() => string.Join("\n", new[] { Source.Config.EditsDir, Path.Combine(Source.Config.WorkDir, "out") }
        .Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
        .Order(StringComparer.Ordinal).Select(f => $"{f} {new FileInfo(f).Length} {File.GetLastWriteTimeUtc(f).Ticks}"));

    // ---- acceptance ----

    [TestMethod]
    [DataRow("C10", 1, 0)]
    [DataRow("C12", 1, 5)]
    [DataRow("C18", 4, 0)]
    public void Acceptance_RecipeGivesThePinnedChunks_AndWritesNothing(string name, int chunks, int danger)
    {
        var json = RecipeText(name);
        if (name == "C18" && !File.Exists(Path.Combine(RecipeContext.RootOf(Source.Config), "work", "out", "art", "C18", "C18_PlateBlue_64x16.png")))
            Assert.Inconclusive("C18's art (work\\out\\art\\C18) is missing");
        var before = Listing();
        var r = Check(json);
        foreach (var l in r.Log.Where(l => !l.StartsWith("    "))) Console.WriteLine(l);
        Assert.IsTrue(r.Ok, string.Join("\n", r.Refusals));
        Assert.IsTrue(r.ChunksPinned, "Expect.Chunks");
        Assert.AreEqual(chunks, r.Chunks.Count);
        Assert.AreEqual(danger, r.Danger.Count, "Danger warnings (a build needs --ack-danger; the recipe can't acknowledge them)");
        Assert.AreEqual(before, Listing(), "recipe-check wrote under work\\edits or work\\out");
    }

    [TestMethod]
    public void RecipesInGit_HoldTextAndHashesOnly()
    {
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "recipes"), "*.json"))
        {
            var text = File.ReadAllText(file);
            Assert.IsTrue(text.Length < 200_000, $"{file}: {text.Length} characters");
            Assert.IsFalse(text.Contains(Source.Config.AesKey.TrimStart('0', 'x', 'X'), StringComparison.OrdinalIgnoreCase), $"{file} contains the AES key");
            var (recipe, refusals) = RecipeReader.Read(text);
            Assert.IsNotNull(recipe, $"{Path.GetFileName(file)}: {string.Join("; ", refusals)}");
        }
    }

    // ---- the reader: strict, data only ----

    private static JsonObject C10() => JsonNode.Parse(RecipeText("C10"))!.AsObject();

    private static void Refused(string json, string expected)
    {
        var (recipe, refusals) = RecipeReader.Read(json);
        Assert.IsNull(recipe, "should be refused: " + expected);
        Assert.IsTrue(refusals.Any(r => r.Contains(expected, StringComparison.OrdinalIgnoreCase)), $"expected \"{expected}\", got: {string.Join(" | ", refusals)}");
    }

    private static string With(Action<JsonObject> change) { var o = C10(); change(o); return o.ToJsonString(); }
    private static JsonObject Value(JsonObject o, int i = 0) => o["Packages"]![0]!["Values"]![i]!.AsObject();

    [TestMethod]
    public void Reader_IsStrict()
    {
        // a field nobody reads would be a switch that does nothing, or one that seems to loosen a check: refused
        Refused(With(o => o["AckDanger"] = true), "AckDanger");
        Refused(With(o => o["SkipVerify"] = true), "SkipVerify");
        Refused(With(o => Value(o)["Window"] = 512), "Window");
        Refused(With(o => o.Remove("Name")), "Name");
        Refused(With(o => o["Recipe"] = 2), "version 1");
        Refused(With(o => Value(o)["Value"] = "20"), "not a valid recipe");
        var text = RecipeText("C10");
        Refused(text.Replace("\"Recipe\": 1,", "\"Recipe\": 1, \"Recipe\": 1,"), "not a valid recipe");
        Refused(text.Replace("\"Recipe\": 1,", "// comment\n\"Recipe\": 1,"), "not a valid recipe");
        Refused(text.TrimEnd().TrimEnd('}') + ",}", "not a valid recipe");
        Refused(With(o => o["Packages"]![0]!.AsObject()["Values"] = null), "not a valid recipe");
    }

    [TestMethod]
    public void Reader_DataChecks()
    {
        Refused(With(o => o["Name"] = "../x"), "Name");
        Refused(With(o => o["Container"] = "pc9999_mobile_and"), "ending in _P");
        Refused(With(o => o["Packages"]![0]!["StockSha256"] = "ABC"), "StockSha256");
        Refused(With(o => o["Packages"]![0]!["Package"] = "MatchTime"), "/Game/");
        Refused(With(o => Value(o)["Value"] = 0.1), "write the float32 value");
        Refused(With(o => Value(o)["Value"] = 0), "Value equals Stock");
        Refused(With(o => o["Packages"]!.AsArray().Add(o["Packages"]![0]!.DeepClone())), "listed 2 times");
        Refused(With(o => o["Packages"]![0]!["Values"]!.AsArray().Add(Value(o).DeepClone())), "set 2 times");
        Refused(With(o => o["Packages"] = new JsonArray()), "no Packages and no Textures");
        Refused(With(o => o["Expect"]!["Files"]!.AsObject()["other_mod.utoc"] = new string('a', 64)), "device file name of this mod");
        foreach (var bad in new[] { "..\\outside.png", "C:\\x.png", "/abs.png", "work/./x.png", "work//x.png" })
            Assert.IsNotNull(RecipeReader.PathRefusal(bad), bad);
        Assert.IsNull(RecipeReader.PathRefusal("work/out/art/C18/C18_PlateBlue_64x16.png"));

        var c18 = JsonNode.Parse(RecipeText("C18"))!.AsObject();
        var first = c18["Inputs"]!.AsObject().First().Key;
        var noInput = c18.DeepClone().AsObject(); noInput["Textures"]![0]!.AsObject().Remove("Input"); noInput["Textures"]![1]!.AsObject().Remove("Input");
        Refused(noInput.ToJsonString(), "needs an Input");
        var unused = c18.DeepClone().AsObject(); unused["Inputs"]!.AsObject()["spare"] = new JsonObject { ["Path"] = "work/x.png", ["Sha256"] = new string('0', 64) };
        Refused(unused.ToJsonString(), "listed but not used");
        var white = c18.DeepClone().AsObject(); white["Textures"]![0]!["Mode"] = "White";
        Refused(white.ToJsonString(), "takes no Input");
        Assert.IsNotNull(first);
    }

    // ---- the check against the game ----

    private static void CheckRefused(string json, string expected)
    {
        var r = Check(json);
        Assert.IsFalse(r.Ok, "should be refused: " + expected);
        Assert.IsTrue(r.Refusals.Any(x => x.Contains(expected, StringComparison.OrdinalIgnoreCase)), $"expected \"{expected}\", got: {string.Join(" | ", r.Refusals)}");
    }

    [TestMethod]
    public void Check_Refusals_StockValuesAndPins()
    {
        CheckRefused(With(o => o["Packages"]![0]!["StockSha256"] = new string('0', 64)), "game updated?");
        CheckRefused(With(o => Value(o)["Slot"] = "CanvasPanelSlot_9"), "is CanvasPanelSlot_0, not CanvasPanelSlot_9");
        CheckRefused(With(o => Value(o)["Stock"] = 1), "stock is 0, the recipe says 1");
        CheckRefused(With(o => Value(o)["Inserted"] = false), "doesn't store it");
        // Right is stored in stock: an entry for it that says Inserted is refused, even with the right stock value
        var right = PatchSession.InMemory(Source, Tv + "MatchTime/MatchTime").StockValue(new FieldKey(Tv + "MatchTime/MatchTime", 11, "LayoutData.Offsets.Right"));
        Assert.IsNotNull(right);
        CheckRefused(With(o => { Value(o)["Path"] = "LayoutData.Offsets.Right"; Value(o)["Stock"] = right.Value; Value(o)["Value"] = right.Value + 1; }), "stores it");
        CheckRefused(With(o => Value(o)["Path"] = "LayoutData.NoSuchField"), "CanvasPanelSlot_0.LayoutData.NoSuchField");
        CheckRefused(With(o => o["Packages"]![0]!["Package"] = "/Game/Assets/ui/NoSuchPackage"), "not in the game");
        // pins: a wrong chunk hash, and an output that isn't pinned
        CheckRefused(With(o => o["Expect"]!["Chunks"]!.AsObject()[o["Packages"]![0]!["Package"]!.GetValue<string>()] = new string('a', 64)), "pinned aaaaaaaaaaaa");
        CheckRefused(With(o => o["Expect"]!["Chunks"] = new JsonObject { ["/Game/Assets/ui/Other"] = new string('a', 64) }), "made but not pinned");
    }

    [TestMethod]
    public void Check_Refusals_Inputs()
    {
        var root = RecipeContext.RootOf(Source.Config);
        if (!Directory.Exists(Path.Combine(root, "work", "out", "art", "C18"))) Assert.Inconclusive("C18's art is missing");
        var c18 = JsonNode.Parse(RecipeText("C18"))!.AsObject();
        var id = c18["Inputs"]!.AsObject().First().Key;
        var wrongSha = c18.DeepClone().AsObject(); wrongSha["Inputs"]![id]!["Sha256"] = new string('b', 64);
        CheckRefused(wrongSha.ToJsonString(), "the recipe pins bbbbbbbbbbbb");
        var missing = c18.DeepClone().AsObject(); missing["Inputs"]![id]!["Path"] = "work/out/art/C18/missing.png";
        CheckRefused(missing.ToJsonString(), "is missing");
    }

    [TestMethod]
    public void Check_DangerNeedsTheCommandLine_TheRecipeCantAcknowledge()
    {
        var r = Check(RecipeText("C12"));
        Assert.IsTrue(r.Ok, string.Join("\n", r.Refusals));
        Assert.AreEqual(5, r.Danger.Count);
        Assert.IsTrue(r.Danger.All(w => w.Warning.Message.Contains("vanish")), string.Join("\n", r.Danger.Select(w => w.Text)));
    }

    // ---- the in-memory session ----

    [TestMethod]
    public void InMemorySession_RebuildChecksLikeASave_AndCantBeSaved()
    {
        var s = PatchSession.InMemory(Source, Tv + "MatchTime/MatchTime");
        Assert.IsTrue(s.IsInMemory);
        var home = WidgetTreeReader.Build(Source, Tv + "MatchTime/MatchTime").Find("Text_Score_Home")!;
        var edit = SlotEditor.SetField(home, s.Current, "LayoutData.Offsets.Left", 20);
        Assert.IsNull(s.Apply(edit), $"{edit.Reason} | {string.Join(", ", edit.Changes.Select(c => c.Key))}");
        var good = s.Rebuild();
        Assert.IsTrue(good.Ok, string.Join("\n", good.Refusals.Select(r => r.Message)));
        Assert.AreEqual("69fde3309e907969b6dea25f29a20d42f6c1cf1e2d3c1c86306bd8cdf27473e2", RecipeCheck.Sha256(good.Chunk!), "C10's chunk");
        Assert.ThrowsException<InvalidOperationException>(() => s.Save());

        // a faulty builder (one stray byte) is refused by the exact diff, as in a save
        s.BeforeVerify = b => { var c = (byte[]) b.Clone(); c[100] ^= 0xFF; return c; };
        var bad = s.Rebuild();
        Assert.IsFalse(bad.Ok);
        Assert.IsNull(bad.Chunk);
    }
}
