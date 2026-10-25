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
/// Stage 2a: the recipe format (strict, data only) and recipe-check (read only, in memory). Acceptance: recipes\C10,
/// C12 and C18 (device-tested: an inserted tag; 23 values + 2 tags + key/range edits; the appended + pointed texture
/// route) give exactly their pinned chunks. Stage 2c: Clones and Repoints, acceptance recipes\C13 (solid-colour clones +
/// a re-point) and C14 (UCL's image, resized clones, a re-point with 9-slice margins 0). Stage 2d: package Layers
/// (Rename, RemoveTag, ClassSwap, ColorRows, AppendNames, EnumInsert) and White / .bgra clones, acceptance recipes\C15,
/// C16 and C16b (made by umgcompare recipe-ucl from the builders that made the device-tested mods). Stage 2e: NameTag and
/// FontFace layers and LoosePaks, acceptance recipes\C17a (a font pak only) and C17b (C16b + UCL's font on the scoreboard).
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

    private static void NeedsInputs(string json, string name)
    {
        foreach (var input in Parse(json).InputsOrEmpty.Values)
            if (!File.Exists(Path.Combine(RecipeContext.RootOf(Source.Config), input.Path))) Assert.Inconclusive($"{name}: input {input.Path} is missing");
    }

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
    [DataRow("C12", 1, 0)]
    [DataRow("C18", 4, 0)]
    [DataRow("C13", 3, 0)]
    [DataRow("C14", 3, 0)]
    [DataRow("C15", 3, 0)]
    [DataRow("C16", 22, 0)]
    [DataRow("C16b", 22, 0)]
    [DataRow("C17a", 0, 0)]
    [DataRow("C17b", 26, 0)]
    public void Acceptance_RecipeGivesThePinnedChunks_AndWritesNothing(string name, int chunks, int danger)
    {
        var json = RecipeText(name);
        NeedsInputs(json, name);
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

        // computed properties are neither written nor read: a copy of the data under their names would be ignored, so it's refused
        // (recipe-from-set wrote them into C16b_test.json before this was fixed)
        Refused(With(o => o["PackagesOrEmpty"] = new JsonArray()), "PackagesOrEmpty");
        Refused(With(o => o["InputsOrEmpty"] = new JsonObject()), "InputsOrEmpty");
        Refused(WithRecipe("C13", o => o["Clones"]![0]!["NewPath"] = "/Game/x"), "NewPath");
        foreach (var name in new[] { "C10", "C13", "C14", "C16b_test" })
        {
            var written = RecipeReader.Write(Parse(RecipeText(name)));
            foreach (var computed in new[] { "OrEmpty", "NewPath" })
                Assert.IsFalse(written.Contains(computed), $"{name}: Write emits {computed}");
            Assert.AreEqual(written, RecipeReader.Write(Parse(written)), $"{name}: Write(Read(Write(r))) changed");
        }
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
        Refused(With(o => o["Packages"] = new JsonArray()), "no Packages, Textures, Clones, Repoints or LoosePaks");
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

    // ---- stage 2c: Clones and Repoints ----

    private static string WithRecipe(string name, Action<JsonObject> change) { var o = JsonNode.Parse(RecipeText(name))!.AsObject(); change(o); return o.ToJsonString(); }
    private static JsonObject Clone(JsonObject o, int i = 0) => o["Clones"]![i]!.AsObject();
    private static JsonObject Repoint(JsonObject o) => o["Repoints"]![0]!.AsObject();
    private static JsonObject Margin(JsonObject o, int i = 0) => o["Repoints"]![0]!["Values"]![i]!.AsObject();
    private static JsonArray MarginLeft(double stock, double value) =>
        new(new JsonObject { ["Export"] = "Img_Plate", ["Path"] = "Margin.Left", ["Stock"] = stock, ["Value"] = value });

    [TestMethod]
    public void Reader_DataChecks_ClonesAndRepoints()
    {
        string C13(Action<JsonObject> change) => WithRecipe("C13", change);
        Refused(C13(o => Clone(o)["Solid"] = "#00ff00"), "upper-case hex");
        Refused(C13(o => Clone(o)["Solid"] = "green"), "#RRGGBB");
        Refused(C13(o => Clone(o).Remove("Solid")), "exactly one of Solid, Input and White");
        Refused(C13(o => Clone(o)["Input"] = "spare"), "exactly one of Solid, Input and White");
        Refused(C13(o => { Clone(o).Remove("Solid"); Clone(o)["Input"] = "spare"; }), "isn't listed in Inputs");
        Refused(C13(o => Clone(o)["StockSha256"] = "x"), "StockSha256");
        Refused(C13(o => Clone(o)["White"] = true), "exactly one of Solid, Input and White");
        Refused(C13(o => Clone(o)["Width"] = 2), "only for a .bgra input");
        Refused(C13(o => Clone(o)["From"] = Clone(o)["From"]!.GetValue<string>() + ".DemoTitlePlate"), "no object name");
        Refused(C13(o => Clone(o)["NewName"] = "Demo/Title"), "NewName");
        Refused(C13(o => Clone(o, 1)["From"] = Clone(o)["From"]!.GetValue<string>()), "made 2 times");
        Refused(C13(o => Clone(o)["Resize"] = true), "Resize");
        Refused(C13(o => Repoint(o)["To"] = Repoint(o)["To"]!.GetValue<string>() + "X"), "as long as From");
        Refused(C13(o => Repoint(o)["To"] = Repoint(o)["From"]!.GetValue<string>()), "From equals To");
        Refused(C13(o => Repoint(o)["To"] = "DemoTitleUCL01"), "object paths");
        Refused(C13(o => o["Repoints"]!.AsArray().Add(Repoint(o).DeepClone())), "one per package");
        Refused(C13(o => o["Packages"] = new JsonArray(new JsonObject { ["Package"] = Repoint(o)["Package"]!.GetValue<string>(), ["StockSha256"] = new string('0', 64), ["Values"] = new JsonArray() })),
            "doesn't layer a re-point");
        Refused(C13(o => Clone(o).Remove("NewName")), "not a valid recipe");

        string C14(Action<JsonObject> change) => WithRecipe("C14", change);
        Refused(C14(o => Margin(o)["Value"] = 0.33), "write the float32 value");
        Refused(C14(o => Margin(o)["Value"] = 0.33000001311302185), "Value equals Stock");
        Refused(C14(o => o["Repoints"]![0]!["Values"]!.AsArray().Add(Margin(o).DeepClone())), "set 2 times");
        Refused(C14(o => Margin(o).Remove("Stock")), "not a valid recipe");
        Refused(C14(o => { Clone(o).Remove("Input"); Clone(o)["Solid"] = "#00FF00"; Clone(o, 1).Remove("Input"); Clone(o, 1)["Solid"] = "#FF00FF"; }), "listed but not used");
    }

    [TestMethod]
    public void Check_Refusals_ClonesAndRepoints()
    {
        string C13(Action<JsonObject> change) => WithRecipe("C13", change);
        CheckRefused(C13(o => Clone(o)["StockSha256"] = new string('0', 64)), "game updated?");
        CheckRefused(C13(o => Clone(o)["NewName"] = "DemoTitleUCL1"), "must be as long as");
        CheckRefused(C13(o => Clone(o)["From"] = Clone(o)["From"]!.GetValue<string>() + "X"), "isn't in the game");
        CheckRefused(C13(o => { Clone(o)["NewName"] = "DemoTitlePlate"; }), "already exists in the stock containers");
        CheckRefused(C13(o => Repoint(o)["StockSha256"] = new string('0', 64)), "game updated?");
        // a re-point to a package nobody ships would leave the widget without its texture in game
        CheckRefused(C13(o => Repoint(o)["To"] = Repoint(o)["To"]!.GetValue<string>().Replace("UCL01", "UCL02")), "neither the recipe makes nor the game has");
        // From must be what the package stores (NameRenamePatch finds it in the name map)
        CheckRefused(C13(o => Repoint(o)["From"] = Repoint(o)["From"]!.GetValue<string>().Replace("Plate", "Platx")), "DemoTitleBase:");
        // values: the export, a stored value, its stock value
        CheckRefused(C13(o => Repoint(o)["Values"] = MarginLeft(0.5, 0)), "stock is 0.33, the recipe says 0.5");
        CheckRefused(C13(o => { Repoint(o)["Values"] = MarginLeft(0.33, 0); Margin(o)["Export"] = "Img_Nope"; }), "0 exports are named Img_Nope");
        CheckRefused(C13(o => { Repoint(o)["Values"] = MarginLeft(0.33, 0); Margin(o)["Path"] = "Margin.Nope"; }), "stock doesn't store it");
        // C13 plus one margin is a valid recipe, but not C13: Expect.Chunks refuses it
        CheckRefused(C13(o => Repoint(o)["Values"] = MarginLeft(0.33, 0)), "DemoTitleBase is sha256");

        // a texture edit and a recipe re-point can't both write one package
        var c18 = RecipeText("C18");
        NeedsInputs(c18, "C18");
        CheckRefused(WithRecipe("C18", o => o["Repoints"] = new JsonArray(new JsonObject
        {
            ["Package"] = o["Textures"]![0]!["Package"]!.GetValue<string>(), ["StockSha256"] = new string('0', 64), ["From"] = "/Game/A.A", ["To"] = "/Game/B.B",
        })), "doesn't layer a re-point on that");
    }

    // ---- stage 2d: package Layers ----

    private static readonly string DemoTitle = Tv + "DemoTitle/DemoTitle", MatchTime = Tv + "MatchTime/MatchTime";

    /// <summary>A recipe with one package and the given layers (no values), its stock pinned.</summary>
    private static string Layered(string package, params JsonObject[] layers) => new JsonObject
    {
        ["Recipe"] = 1, ["Name"] = "zzlayers", ["Container"] = "pc9999_mobile_and_P",
        ["Packages"] = new JsonArray(new JsonObject
        {
            ["Package"] = package, ["StockSha256"] = RecipeCheck.Sha256(Source.ReadPackageBytes(package).Bytes),
            ["Values"] = new JsonArray(), ["Layers"] = new JsonArray(layers),
        }),
    }.ToJsonString();

    private static JsonObject Layer(string op, params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject { ["Op"] = op };
        foreach (var (k, v) in fields) o[k] = v;
        return o;
    }

    private static JsonObject ScoreSwap(string from, string to) => Layer("Rename", ("EnumSwaps", new JsonArray(new JsonObject
        { ["Export"] = "Text_Score_Home", ["Property"] = "color", ["From"] = from, ["To"] = to })));

    private const string Dark = "ColorEnum_String::NewEnumerator87", White = "ColorEnum_String::NewEnumerator86";

    [TestMethod]
    public void Reader_DataChecks_Layers()
    {
        var (good, why) = RecipeReader.Read(Layered(MatchTime, ScoreSwap(Dark, White)));
        Assert.IsNotNull(good, string.Join(" | ", why));
        Refused(Layered(MatchTime), "no Values and no Layers");
        Refused(Layered(MatchTime, Layer("SkipVerify")), "not a valid recipe");
        Refused(Layered(MatchTime, new JsonObject { ["Export"] = "Normal_Display" }), "not a valid recipe");
        Refused(Layered(MatchTime, Layer("RemoveTag", ("Export", "Normal_Display"), ("Tag", "Clipping"), ("Stock", "x"), ("Window", 512))), "not a valid recipe");
        Refused(Layered(MatchTime, Layer("RemoveTag", ("Export", "Normal_Display"), ("Tag", "Clipping"))), "not a valid recipe");
        Refused(Layered(MatchTime, Layer("Rename")), "no Renames and no EnumSwaps");
        Refused(Layered(MatchTime, ScoreSwap(Dark, Dark)), "From equals To");
        Refused(Layered(MatchTime, Layer("Rename", ("Renames", new JsonArray(new JsonObject { ["From"] = "/Game/A.A", ["To"] = "/Game/BB.BB" })))), "as long as From");
        Refused(Layered(MatchTime, Layer("ClassSwap", ("Export", "Plate_DemoTitleBg"), ("To", MatchTime + ".MatchTime_C"))), "a /Game/ package path");
        Refused(Layered(MatchTime, Layer("ColorRows", ("Rows", new JsonArray(new JsonObject { ["Row"] = "R", ["Stock"] = "#ffffffff", ["Color"] = "#000000FF" })))), "upper-case hex");
        Refused(Layered(MatchTime, Layer("ColorRows", ("Rows", new JsonArray(new JsonObject { ["Row"] = "R", ["Stock"] = "#000000FF", ["Color"] = "#000000FF" })))), "Color equals Stock");
        Refused(Layered(MatchTime, Layer("AppendNames", ("Names", new JsonArray("A", "A")))), "listed 2 times");
        Refused(Layered(MatchTime, Layer("EnumInsert", ("Export", "Text_Value"), ("After", "VerticalAlignment"), ("Tag", "Justification"), ("Type", "ByteProperty"),
            ("Enum", "ETextJustify"), ("Value", "ETextJustify::Center"))), "not a valid recipe"); // no Anchor
        // a .bgra input needs its size
        Refused(WithRecipe("C15", o => Clone(o).Remove("Width")), "needs Width and Height");
    }

    [TestMethod]
    public void Check_Refusals_Layers()
    {
        // each tool refuses on its own checks; the refusal names the package, the layer and its op
        CheckRefused(Layered(MatchTime, Layer("RemoveTag", ("Export", "Normal_Display"), ("Tag", "Clipping"), ("Stock", "EWidgetClipping::Inherit"))), "MatchTime: layer 1 (RemoveTag)");
        CheckRefused(Layered(MatchTime, ScoreSwap(White, Dark)), "MatchTime: layer 1 (Rename): Text_Score_Home.color");
        CheckRefused(Layered(DemoTitle, Layer("ClassSwap", ("Export", "Plate_DemoTitleBg"), ("To", Tv + "MatchTime/NoSuchBase"))), "DemoTitle: layer 1 (ClassSwap)");
        const string table = "/Game/Assets/ui/Color/ColorTable";
        CheckRefused(Layered(table, Layer("ColorRows", ("Rows", new JsonArray(new JsonObject { ["Row"] = "STR_SWITCH_BRAND_1", ["Stock"] = "#123456FF", ["Color"] = "#FFFFFFFF" })))),
            "the recipe says #123456FF");
        CheckRefused(Layered(table, Layer("ColorRows", ("Rows", new JsonArray(new JsonObject { ["Row"] = "NO_SUCH_ROW", ["Stock"] = "#123456FF", ["Color"] = "#FFFFFFFF" })))),
            "no row NO_SUCH_ROW");
        // a rename must name a package the recipe makes or the game has
        CheckRefused(Layered(MatchTime, Layer("Rename", ("Renames", new JsonArray(new JsonObject
            { ["From"] = Tv + "MatchTime/Tex720/MatchTimePlate_0.MatchTimePlate", ["To"] = Tv + "MatchTime/Tex720/MatchTimeNopeX_0.MatchTimeNopeX" })))), "neither the recipe makes nor the game has");
        // the enum tag builder must reproduce the anchor's stock tag: an anchor that doesn't store it refuses the layer
        var names = Layer("AppendNames", ("Names", new JsonArray("Justification", "ETextJustify", "ETextJustify::Center")));
        CheckRefused(Layered(Tv + "MatchTime/MatchTimeCard", names, Layer("EnumInsert", ("Export", "Text_Value"), ("After", "VerticalAlignment"), ("Tag", "Justification"),
            ("Type", "ByteProperty"), ("Enum", "ETextJustify"), ("Value", "ETextJustify::Center"), ("Anchor", new JsonObject { ["Package"] = MatchTime, ["Export"] = "Img_Line" }))),
            "doesn't reproduce stock's");
        // two layers may not change one value: the second swap puts the colour back, which the union check refuses
        CheckRefused(Layered(MatchTime, ScoreSwap(Dark, White), ScoreSwap(White, Dark)), "union of the layers against stock");

        // a class swap carries its imported package swap into the mod's store entry
        var swapped = Check(Layered(DemoTitle, Layer("ClassSwap", ("Export", "Plate_DemoTitleBg"), ("To", Tv + "MatchTime/MatchTimeBase"))));
        Assert.IsTrue(swapped.Ok, string.Join("\n", swapped.Refusals));
        Assert.AreEqual(1, swapped.Packages[DemoTitle].ImportedPackageSwaps.Count);
    }

    // ---- stage 2e: fonts and loose-file paks ----

    private static JsonObject NameTag(string export, string? stock, JsonObject? anchor = null)
    {
        var o = Layer("NameTag", ("Export", export), ("Struct", "_font"), ("Member", "TypefaceFontName"), ("Value", "HeadlineBoldSub"));
        if (stock != null) o["Stock"] = stock;
        if (anchor != null) o["Anchor"] = anchor;
        return o;
    }

    private static JsonObject TeamNameAnchor() => new() { ["Package"] = MatchTime, ["Export"] = "Text_TeamName_Home", ["Value"] = "HeadlineBold" };

    private static string FontPak(Action<JsonObject> change)
    {
        var o = JsonNode.Parse(RecipeText("C17a"))!.AsObject();
        change(o["LoosePaks"]![0]!.AsObject());
        return o.ToJsonString();
    }

    [TestMethod]
    public void Reader_DataChecks_FontsAndPaks()
    {
        Refused(Layered(MatchTime, NameTag("Text_Score_Home", null)), "needs an Anchor");
        Refused(Layered(MatchTime, NameTag("Text_TeamName_Home", "HeadlineBold", TeamNameAnchor())), "takes no Anchor");
        Refused(Layered(MatchTime, NameTag("Text_TeamName_Home", "HeadlineBoldSub")), "Value equals Stock");
        var other = NameTag("Text_Score_Home", null, TeamNameAnchor()); other["Struct"] = "Font";
        Refused(Layered(MatchTime, other), "_font (FSlateFontInfo) members only");
        Refused(Layered(MatchTime, Layer("FontFace", ("Entry", "CompositeFont.DefaultTypeface.Fonts[0]"), ("Name", "Regular"), ("From", "A"), ("To", "A"))), "From equals To");

        Refused(FontPak(p => p["Pak"] = "pc9998_mobile_and.pak"), "ending in _P.pak");
        Refused(FontPak(p => p["Pak"] = "../pc9998_mobile_and_P.pak"), "ending in _P.pak");
        Refused(FontPak(p => p["Pak"] = "pc9999_mobile_and_P.pak"), "the container's own .pak stub");
        Refused(FontPak(p => p["MountPoint"] = "PesMobile/Content/"), "MountPoint");
        Refused(FontPak(p => p["MountPoint"] = "../../../PesMobile/Content"), "MountPoint");
        Refused(FontPak(p => p["MountPoint"] = "../../../PesMobile/../Content/"), "MountPoint");
        Refused(FontPak(p => p["Files"]![0]!["Path"] = "sub/x.ufont"), "plain file name");
        Refused(FontPak(p => p["Files"]![0]!["Input"] = "nope"), "isn't listed in Inputs");
        Refused(FontPak(p => p["Files"] = new JsonArray()), "no Files");
        Refused(FontPak(p => p["Files"]!.AsArray().Add(p["Files"]![0]!.DeepClone())), "listed 2 times");
        Refused(FontPak(p => p["Compressed"] = true), "not a valid recipe");
    }

    [TestMethod]
    public void Check_Refusals_FontsAndPaks()
    {
        // the stock value is checked both ways: stored when the recipe says so, absent when it inserts
        CheckRefused(Layered(MatchTime, NameTag("Text_Score_Home", "HeadlineBold")), "is not stored, the recipe says HeadlineBold");
        CheckRefused(Layered(MatchTime, NameTag("Text_TeamName_Home", null, TeamNameAnchor())), "is HeadlineBold, the recipe says not stored");
        // an insert's builder must reproduce the anchor's stock tag: an anchor that stores another name doesn't
        var wrongAnchor = TeamNameAnchor(); wrongAnchor["Value"] = "Regular";
        CheckRefused(Layered(MatchTime, Layer("AppendNames", ("Names", new JsonArray("HeadlineBoldSub"))), NameTag("Text_Score_Home", null, wrongAnchor)), "doesn't reproduce stock's");
        // the face must be the one the entry uses
        CheckRefused(Layered(UclC16.Pes, Layer("FontFace", ("Entry", UclC16.FontEntry), ("Name", UclC16.FontName), ("From", UclC16.FontTo), ("To", UclC16.FontFrom))), "pes: layer 1 (FontFace)");

        // a loose-file pak only replaces stock loose files, and its input is pinned
        NeedsInputs(RecipeText("C17a"), "C17a");
        CheckRefused(FontPak(p => p["Files"]![0]!["Path"] = "NoSuchFont.ufont"), "isn't a loose file of a stock pak");
        CheckRefused(WithRecipe("C17a", o => o["Inputs"]![o["Inputs"]!.AsObject().First().Key]!["Sha256"] = new string('c', 64)), "the recipe pins cccccccccccc");
        // the pak's bytes are part of the pins
        var r = Check(RecipeText("C17a"));
        Assert.IsTrue(r.Ok, string.Join("\n", r.Refusals));
        Assert.AreEqual(RecipeReader.Read(RecipeText("C17a")).Recipe!.Expect!.Files!["pc9998_mobile_and_P.pak"], RecipeCheck.Sha256(r.LoosePaks.Single().Bytes));
    }

    /// <summary>
    /// A recipe's pins are the device-tested ones (recipes\pins\device-tested.json), not typed separately, except the
    /// deliberate changes in changed-since-device-test.json: each replaces an existing device-tested pin (its Was) with a
    /// different value (C16b_test: MatchCard Img_VI_1 parked off-screen, so the MatchCard chunk, the .ucas and the .utoc).
    /// </summary>
    [TestMethod]
    public void RecipePins_AreTheDeviceTestedPins()
    {
        var pins = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "recipes", "pins", "device-tested.json")))!["Canaries"]!.AsObject();
        var changes = PinOverrides.Load();
        foreach (var (name, change) in changes)
        {
            Assert.IsTrue(pins[name] is JsonObject, $"changed-since-device-test.json: {name} isn't device-tested");
            foreach (var (key, c) in change.Chunks)
                Assert.AreEqual(pins[name]!["Chunks"]![key]?.GetValue<string>(), c.Was, $"{name}: {key} must replace the device-tested pin");
            foreach (var (key, c) in change.Files)
                Assert.AreEqual(pins[name]!["Files"]![key]?["Sha256"]?.GetValue<string>(), c.Was, $"{name}: {key} must replace the device-tested pin");
            Assert.IsTrue(change.Chunks.Values.Concat(change.Files.Values).All(c => c.Now != c.Was && c.Now.Length == 64), $"{name}: every change changes the pin");
            Assert.IsFalse(string.IsNullOrWhiteSpace(change.Why) || string.IsNullOrWhiteSpace(change.Date), $"{name}: say why and when");
        }
        var compared = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "recipes"), "*.json"))
        {
            var recipe = Parse(File.ReadAllText(file));
            if (pins[recipe.Name] is not JsonObject canary) continue;
            Assert.IsNotNull(recipe.Expect, $"{recipe.Name} is device-tested: its recipe must pin it");
            var change = changes.GetValueOrDefault(recipe.Name);
            string File_(string key, JsonNode? pin) => $"{key} {(change?.Files.TryGetValue(key, out var c) == true ? c.Now : pin!["Sha256"])}";
            string Chunk(string key, JsonNode? pin) => $"{key} {(change?.Chunks.TryGetValue(key, out var c) == true ? c.Now : pin)}";
            CollectionAssert.AreEquivalent(canary["Files"]!.AsObject().Select(kv => File_(kv.Key, kv.Value)).ToList(),
                (recipe.Expect.Files ?? new Dictionary<string, string>()).Select(kv => $"{kv.Key} {kv.Value}").ToList(), $"{recipe.Name}: Expect.Files");
            CollectionAssert.AreEquivalent(canary["Chunks"]!.AsObject().Select(kv => Chunk(kv.Key, kv.Value)).ToList(),
                (recipe.Expect.Chunks ?? new Dictionary<string, string>()).Select(kv => $"{kv.Key} {kv.Value}").ToList(), $"{recipe.Name}: Expect.Chunks");
            compared.Add(recipe.Name);
        }
        CollectionAssert.IsSubsetOf(new[] { "C13", "C14", "C16b_test", "C18" }, compared, string.Join(", ", compared));
        CollectionAssert.IsSubsetOf(changes.Keys.ToList(), compared, "every changed canary has a recipe that carries the change");
    }

    /// <summary>Canary C8 as a recipe: Time_Timer Left 368 → 491, which left 0.9 px inside Normal_Display and vanished in game.</summary>
    internal static string C8Recipe()
    {
        var timer = WidgetTreeReader.Build(Source, MatchTime).Find("Time_Timer")!.Slot!;
        return new JsonObject
        {
            ["Recipe"] = 1, ["Name"] = "zzC8", ["Container"] = "pc9999_mobile_and_P",
            ["Packages"] = new JsonArray(new JsonObject
            {
                ["Package"] = MatchTime, ["StockSha256"] = RecipeCheck.Sha256(Source.ReadPackageBytes(MatchTime).Bytes),
                ["Values"] = new JsonArray(new JsonObject
                    { ["Export"] = timer.ExportIndex, ["Slot"] = timer.Name, ["Path"] = "LayoutData.Offsets.Left", ["Stock"] = 368, ["Value"] = 491 }),
            }),
        }.ToJsonString();
    }

    [TestMethod]
    public void Check_DangerNeedsTheCommandLine_TheRecipeCantAcknowledge()
    {
        var r = Check(C8Recipe());
        Assert.IsTrue(r.Ok, string.Join("\n", r.Refusals));
        Assert.AreEqual("Time_Timer", r.Danger.Single().Warning.Message.Split(' ')[0], string.Join("\n", r.Danger.Select(w => w.Text)));
        StringAssert.Contains(r.Danger.Single().Warning.Message, "practically vanish");
    }

    /// <summary>
    /// Item 3: the layout warnings of the canaries that passed on the phone. Group A (collapsed to 0 px by the edits) and B
    /// (parked off-screen) are Warnings that say so; group C (the goal scorer's and card player's names: the edits change
    /// keys of an animation that moves them relative to their clip) is "uncertain"; group D (the timer: a RemoveTag layer
    /// takes Normal_Display's clipping away) has no clip warning at all. Nothing is Danger.
    /// </summary>
    [TestMethod]
    [DataRow("C16")]
    [DataRow("C16b")]
    [DataRow("C17b")]
    public void PassedOnThePhone_IsNotDanger_AndSaysWhy(string name)
    {
        var json = RecipeText(name);
        NeedsInputs(json, name);
        var r = Check(json);
        Assert.IsTrue(r.Ok, string.Join("\n", r.Refusals));
        Assert.AreEqual(0, r.Danger.Count, string.Join("\n", r.Danger.Select(w => w.Text)));
        string Kind(string package, string widget) => string.Join(",", r.Warnings.Where(w => w.Package == package && System.Text.RegularExpressions.Regex.IsMatch(w.Warning.Message, $@"\b{widget}\b(?!_)")).Select(w => w.Warning.Kind));
        Assert.AreEqual("AnimatedClip", Kind("MatchTimeGoal", "Text_PlayerName"), "the scorer's name: Goal's Show/Hide keys are edited");
        Assert.AreEqual("AnimatedClip", Kind("MatchTimeCard", "Text_Value"), "the card player's name: its own Card_On/Card_Off keys are edited");
        Assert.AreEqual("Collapsed", Kind("MatchTimeGoal", "MatchTimeBase_2"));
        Assert.AreEqual("OffScreen", Kind("MatchTimeGoal", "Text_Item"));
        Assert.AreEqual("", Kind("MatchTime", "Time_Timer"), "Normal_Display's clipping is removed by the recipe, so the timer leaves no clip");
        Assert.IsTrue(r.ClipRemoved.Contains(UmgEditor.Core.Layout.LayoutEngine.ClipKey(MatchTime, "Normal_Display")));
        var uncertain = r.Warnings.First(w => w.Warning.Kind == WarningKind.AnimatedClip).Warning.Message;
        StringAssert.StartsWith(uncertain, "Uncertain: an animation moves");
        StringAssert.Contains(uncertain, "the edits change");
    }

    [TestMethod]
    public void C15_TheTimerAndTheCollapsedPlates_LeaveNoClipOnceItIsRemoved()
    {
        var r = Check(RecipeText("C15"));
        Assert.IsTrue(r.Ok, string.Join("\n", r.Refusals));
        Assert.AreEqual(0, r.Warnings.Count(w => w.Package == "MatchTime" && w.Warning.Kind is WarningKind.LeavesClip or WarningKind.AnimatedClip),
            string.Join("\n", r.Warnings.Select(w => w.Text)));
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
