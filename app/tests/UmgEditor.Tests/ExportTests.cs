using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>Step 6g: the export dialog's logic (plan, background build, predictions, test instructions). Own project + mod folder per test.</summary>
[TestClass]
public class ExportTests
{
    private const string Base = Tv + "MatchTime/MatchTimeBase";
    private const string MatchTime = Tv + "MatchTime/MatchTime";
    private const string Right = "LayoutData.Offsets.Right";
    private const string Container = "pc9999_mobile_and_P";

    private string _project = null!;
    private string ModName => "test-" + _project;
    private static WriteGuard Edits => new(Source.Config);
    private static WriteGuard Mods => WriteGuard.ForMods(Source.Config);

    [TestInitialize]
    public void NewProject() => _project = "zztest" + Guid.NewGuid().ToString("N")[..10];

    [TestCleanup]
    public void Cleanup()
    {
        Edits.DeleteDirectory(Path.Combine(Edits.Root, _project));
        Mods.DeleteDirectory(Path.Combine(Mods.Root, ModName));
    }

    private EditorController Controller() =>
        new(Source, new UsageGraph(UiIndex), Engine, p => WidgetTreeReader.Build(Source, p), project: _project);

    private static void Edit(EditorController c, string package, string widget, string path, double value)
    {
        var tree = WidgetTreeReader.Build(Source, package);
        var node = tree.Find(widget)!;
        var first = c.Apply(tree, node, e => SlotEditor.SetField(node, e, path, value));
        Assert.IsTrue((first.NeedsConfirmation ? c.Apply(tree, node, e => SlotEditor.SetField(node, e, path, value), confirmed: true) : first).Applied);
    }

    /// <summary>Canary C6's two edits, saved.</summary>
    private EditorController C6()
    {
        var c = Controller();
        Edit(c, Base, "Img_Plate", Right, 100);
        Edit(c, MatchTime, "Img_MatchTimeBg_1", "LayoutData.Offsets.Left", 146);
        Assert.IsTrue(c.SaveAll().All(r => r.Result.Saved));
        return c;
    }

    private ExportRequest Request(params string[] packages) => new(ModName, Container, packages, Source.Config.RetocPath);

    private sealed class FailingVerify(IRetoc inner) : IRetoc
    {
        public string ExePath => inner.ExePath;
        public RetocResult Run(params string[] args) => args[0] == "verify" ? new RetocResult(1, "", "simulated verify failure") : inner.Run(args);
    }

    // ---- the plan: what is refused ----

    [TestMethod]
    public void Plan_RefusedWhileAnythingIsUnsaved()
    {
        var c = C6();
        Edit(c, MatchTime, "Img_MatchTimeBg_1", "LayoutData.Offsets.Left", 150);
        var plan = c.PlanExport(Request(Base)); // even when the dirty package isn't picked
        Assert.IsFalse(plan.Ok);
        StringAssert.Contains(plan.Refusals.Single(), "unsaved edits in MatchTime");
        Assert.AreEqual(0, plan.Packages.Count);
    }

    [TestMethod]
    public void Plan_RefusesBadNames_NoPackage_SamePackageTwice_NoSavedEdits_MissingRetoc()
    {
        var c = C6();
        StringAssert.Contains(c.PlanExport(Request(Base) with { ContainerName = "pc9999_mobile_and" }).Refusals.Single(), "ending in _P");
        StringAssert.Contains(c.PlanExport(Request(Base) with { ContainerName = "pc9999 mobile_P" }).Refusals.Single(), "ending in _P");
        foreach (var bad in new[] { "../escape", "a b", "", ".." })
            StringAssert.Contains(c.PlanExport(Request(Base) with { ModName = bad }).Refusals.Single(), "plain folder name", bad);
        StringAssert.Contains(c.PlanExport(Request()).Refusals.Single(), "at least one package");
        StringAssert.Contains(c.PlanExport(Request(Base, MatchTime, Base.ToUpperInvariant())).Refusals.Single(), "MatchTimeBase is picked 2 times");
        StringAssert.Contains(c.PlanExport(Request(Tv + "MatchTime/MatchTimeGoal")).Refusals.Single(), "MatchTimeGoal has no saved edits");
        var noRetoc = c.PlanExport(Request(Base) with { RetocPath = @"E:\nowhere\retoc.exe" });
        StringAssert.Contains(noRetoc.Refusals.Single(), "retoc not found");
        Assert.IsFalse(noRetoc.Retoc.Exists);
    }

    [TestMethod]
    public void Plan_Ok_ReadsTheSavedChunks_RetocHashShown_OutputUnderMods()
    {
        var c = C6();
        CollectionAssert.AreEqual(new[] { "MatchTime", "MatchTimeBase" }, c.ExportCandidates().Select(x => x.Name).ToArray());
        var plan = c.PlanExport(Request(Base, MatchTime));
        Assert.IsTrue(plan.Ok, string.Join(" | ", plan.Refusals));
        Assert.AreEqual(Path.Combine(Source.Config.ModsDir, ModName), plan.OutDir);
        Assert.AreEqual(2, plan.Packages.Count);
        Assert.AreEqual(2, plan.SavedEdits.Count);
        Assert.IsTrue(plan.Retoc.IsTested, plan.Retoc.Text);
        StringAssert.Contains(plan.Retoc.Text, ModBuilder.TestedRetocSha256);
        CollectionAssert.AreEquivalent(new[] { ("0xA64", "00 00 C8 42"), ("0x133E23", "00 00 12 43") }, plan.Edits.Select(e => (e.OffsetHex, e.NewHex)).ToArray(), "C6's bytes");
        Assert.AreEqual(0, plan.Warnings.Count, string.Join(" | ", plan.Warnings));
    }

    // ---- the build ----

    [TestMethod]
    public void Run_BuildsC6_FilesHashesPredictionsAndInstructions()
    {
        var c = C6();
        var plan = c.PlanExport(Request(Base, MatchTime));
        var log = new List<string>();
        var result = c.RunExport(plan, new RetocProcess(plan.Retoc.Path), new SyncProgress(log.Add));
        Assert.IsTrue(result.Built, string.Join(" | ", result.Refusals) + "\n" + string.Join("\n", log));

        // the three device files, as listed (names, sizes, sha256)
        CollectionAssert.AreEquivalent(new[] { Container + ".pak", Container + ".ucas", Container + ".utoc" }, result.Files.Select(f => f.Name).ToArray());
        foreach (var f in result.Files)
        {
            var path = Path.Combine(result.ModDir, f.Name);
            Assert.AreEqual(f.Size, new FileInfo(path).Length);
            Assert.AreEqual(f.Sha256, PatchSession.Sha256(File.ReadAllBytes(path)), f.Name);
        }
        Assert.AreEqual(3, Directory.GetFiles(result.ModDir).Length, "only the device files in mod\\");
        // the same container as canary C6, proven in game (work/out/mods/CANARY_C6.md)
        CollectionAssert.AreEquivalent(new[]
        {
            "0c9d79129f5ca913335ca2f864ae40d39818a16a12805c9ab55fa1329c7f9424", // .pak stub
            "cdb2945a84c73359118fe9b8f51418364c9ecf35a2ac95529bba661d4bb8546d", // .ucas
            "56ad50b6ce2183964a35f9c1b8571c3fb70e945d687fbec3f05d1a24a089409b", // .utoc
        }, result.Files.Select(f => f.Sha256).ToArray(), "byte-identical to canary C6");
        StringAssert.Contains(File.ReadAllText(Path.Combine(result.OutDir, "build.json")), ModBuilder.TestedRetocSha256, "build.json records the retoc hash");

        // the log: each retoc command, the files, the end
        Assert.IsTrue(log.Any(l => l.StartsWith("retoc pack-raw")) && log.Any(l => l.StartsWith("retoc verify")), string.Join("\n", log));
        Assert.IsTrue(log.Any(l => l.Contains(".utoc") && l.Contains("sha256")));
        StringAssert.Contains(log[^1], "Remove the mod before playing online");

        // predictions: stock + edited for every package the edits reach (9: 2 edited + the 7 others embedding MatchTimeBase)
        Assert.AreEqual(18, result.PredictionFiles.Count);
        Assert.IsTrue(result.PredictionFiles.All(f => File.Exists(f) && f.StartsWith(Path.Combine(result.OutDir, "predictions"))));

        // the instructions: every affected screen, online ones included, the warning, the files and bytes
        var md = File.ReadAllText(result.InstructionsPath!);
        Console.WriteLine(md);
        StringAssert.StartsWith(md, $"# Mod {ModName}: test instructions");
        StringAssert.Contains(md, "> **Remove the mod before playing online.**");
        foreach (var screen in new[] { "MatchTime", "MatchTimeGoal", "MatchTimeCard", "MatchTimeMemberChange",
                     "MatchTimeConnection", "MatchTimeConnectionGoal", "MatchTimeConnectionCard", "MatchTimeConnectionMemberChange" })
            StringAssert.Contains(md, $"| {screen} |", screen);
        StringAssert.Contains(md, "| online goal-scorer banner | MatchTimeConnectionGoal | **online**: predicted only");
        StringAssert.Contains(md, "MatchTimeBase is embedded in the screens below");
        foreach (var f in result.Files) StringAssert.Contains(md, $"| `{f.Name}` | {f.Size:N0} B | `{f.Sha256}` |");
        StringAssert.Contains(md, "`00 00 00 00` → `00 00 C8 42` @ 0xA64");
        StringAssert.Contains(md, "`00 00 FC 42` → `00 00 12 43` @ 0x133E23");
        StringAssert.Contains(md, $"`{ModInstructions.Shot(ModName, "MatchTimeCard")}`: the card pop-up (MatchTimeCard), commit fouls until a yellow card");
        // the predicted rects match the in-game measurements of C6 (score box 180/291, card plate 452)
        StringAssert.Contains(md, "| 161.1 / 272.2 / 25 / 80.6 | 179.6 / 290.7 / 25 / 80.6 |");
        StringAssert.Contains(md, "| 211.1 / 544.4 / 80.6 / 125 | 211.1 / 451.9 / 80.6 / 125 |");
        // every checking command points at a prediction file that exists
        foreach (var line in md.Split('\n').Where(l => l.StartsWith("python work/scripts/validate_layout.py")))
            Assert.IsTrue(File.Exists(line.Split(' ')[2]), line);
        Assert.AreEqual(4, md.Split('\n').Count(l => l.StartsWith("python ")), "one check per offline screen");
    }

    [TestMethod]
    public void Run_FailedVerify_AbortsAndRemovesTheModFolder()
    {
        var c = C6();
        var plan = c.PlanExport(Request(Base));
        var log = new List<string>();
        var result = c.RunExport(plan, new FailingVerify(new RetocProcess(plan.Retoc.Path)), new SyncProgress(log.Add));
        Assert.IsFalse(result.Built);
        StringAssert.Contains(result.Refusals.Single(), "retoc verify failed");
        Assert.IsFalse(Directory.Exists(result.ModDir), "a half-built mod must not be left to copy");
        Assert.IsTrue(File.Exists(Path.Combine(result.OutDir, "FAILED.txt")));
        Assert.IsNull(result.InstructionsPath);
        Assert.IsFalse(Directory.Exists(Path.Combine(result.OutDir, "predictions")), "nothing is predicted for a failed build");
        Assert.IsFalse(File.Exists(Path.Combine(result.OutDir, ModInstructions.FileName)));
        Assert.IsTrue(log.Any(l => l.Contains("removed; see FAILED.txt")), string.Join("\n", log));
    }

    [TestMethod]
    public void Plan_ExistingFolder_IsReplaced_WithAWarning()
    {
        var c = C6();
        Assert.IsTrue(c.RunExport(c.PlanExport(Request(Base)), new RetocProcess(Source.Config.RetocPath)).Built);
        var again = c.PlanExport(Request(Base));
        Assert.IsTrue(again.Ok);
        StringAssert.Contains(again.Warnings.Single(), "exists and will be replaced");
    }

    /// <summary>Progress that reports on the calling thread (Progress&lt;T&gt; would post to a sync context).</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
