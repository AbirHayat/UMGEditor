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
        Assert.AreEqual(0, plan.ImpactWarnings.Count, string.Join(" | ", plan.ImpactWarnings.Select(w => w.Text)));
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
        StringAssert.Contains(md, "## Warnings (Impact panel)\nNone: no rescale");
        StringAssert.Contains(md, "## Hidden on purpose\nNone: the edits collapse no widget");
        StringAssert.Contains(md, "## Confirm on the device\nNothing beyond the screenshots");
        Assert.IsTrue(log.Contains("Hidden on purpose by the edits: none"), string.Join("\n", log));
        Assert.IsFalse(md.Contains("cut off by") || md.Contains("not checked"), "C6 pushes nothing out of a clip");
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

    // ---- Impact warnings (canary C8: the timer pushed out of Normal_Display vanished) ----

    /// <summary>C8's timer edit alone, saved: Danger, the timer keeps 0.9 px inside its clipping parent.</summary>
    private EditorController TimerOut()
    {
        var c = Controller();
        Edit(c, MatchTime, "Time_Timer", "LayoutData.Offsets.Left", 491);
        Assert.IsTrue(c.SaveAll().All(r => r.Result.Saved));
        return c;
    }

    [TestMethod]
    public void Plan_DangerWarning_RefusedUntilTicked()
    {
        var c = TimerOut();
        var plan = c.PlanExport(Request(MatchTime));
        var danger = plan.ImpactWarnings.Single();
        Assert.IsTrue(danger.IsDanger);
        Assert.AreEqual("MatchTime", danger.Package);
        StringAssert.Contains(danger.Text, "[Danger] MatchTime: Time_Timer ends up almost completely outside Normal_Display");
        Assert.IsFalse(plan.Ok, "an unticked Danger warning blocks the build");
        StringAssert.StartsWith(plan.Refusals.Single(), "A danger warning is not acknowledged (tick it below; umgmod build: --ack-danger): MatchTime: Time_Timer");
        CollectionAssert.AreEqual(new[] { danger }, plan.Unacknowledged.ToArray());

        var ticked = c.PlanExport(Request(MatchTime) with { Acknowledged = [danger.Key] });
        Assert.IsTrue(ticked.Ok, string.Join(" | ", ticked.Refusals));
        Assert.AreEqual(0, ticked.Unacknowledged.Count);
        Assert.AreEqual(1, ticked.ImpactWarnings.Count, "still listed once ticked");
        Assert.IsFalse(c.PlanExport(Request(MatchTime) with { Acknowledged = ["MatchTime: some other warning"] }).Ok, "a tick is per warning");
    }

    [TestMethod]
    public void Plan_PlainWarning_ListedButNoTickNeeded()
    {
        var c = Controller();
        Edit(c, MatchTime, "Mask", "LayoutData.Offsets.Left", 429); // partly outside Normal_Display: cut off, not gone
        Assert.IsTrue(c.SaveAll().All(r => r.Result.Saved));
        var plan = c.PlanExport(Request(MatchTime));
        Assert.IsTrue(plan.Ok, string.Join(" | ", plan.Refusals));
        var w = plan.ImpactWarnings.Single();
        Assert.AreEqual(Severity.Warning, w.Warning.Severity);
        StringAssert.Contains(w.Text, "Mask ends up partly outside Normal_Display");
    }

    [TestMethod]
    public void Plan_ImpactWarnings_OnlyForThePickedPackages()
    {
        var c = TimerOut();
        Edit(c, Base, "Img_Plate", Right, 100);
        Assert.IsTrue(c.SaveAll().All(r => r.Result.Saved));
        var baseOnly = c.PlanExport(Request(Base));
        Assert.IsTrue(baseOnly.Ok, string.Join(" | ", baseOnly.Refusals));
        Assert.AreEqual(0, baseOnly.ImpactWarnings.Count, "the timer edit isn't shipped");
        Assert.IsFalse(c.PlanExport(Request(Base, MatchTime)).Ok);
    }

    [TestMethod]
    public void Run_TimerOut_InstructionsCarryTheWarning_MarkTheCut_SkipItsEdges()
    {
        var c = TimerOut();
        var first = c.PlanExport(Request(MatchTime));
        var plan = c.PlanExport(Request(MatchTime) with { Acknowledged = first.ImpactWarnings.Select(w => w.Key).ToList() });
        var log = new List<string>();
        var result = c.RunExport(plan, new RetocProcess(plan.Retoc.Path), new SyncProgress(log.Add));
        Assert.IsTrue(result.Built, string.Join(" | ", result.Refusals));
        Assert.IsTrue(log.Any(l => l.Contains("[Danger] MatchTime: Time_Timer") && l.EndsWith("(acknowledged)")), string.Join("\n", log));

        var md = File.ReadAllText(result.InstructionsPath!);
        Console.WriteLine(md);
        StringAssert.Contains(md, "## Warnings (Impact panel)\n- **Danger** · MatchTime: Time_Timer ends up almost completely outside Normal_Display");
        StringAssert.Contains(md, "*Acknowledged before the build (export dialog tick or umgmod build --ack-danger).*");
        // an acknowledged Danger is still a line to confirm on the device; the timer keeps its size on screen, so it isn't "hidden on purpose"
        StringAssert.Contains(md, "## Confirm on the device\n- [ ] Confirm on the device (scoreboard, ≥ 10 s into play): **Danger** · MatchTime: Time_Timer ends up almost completely outside Normal_Display");
        StringAssert.Contains(md, "## Hidden on purpose\nNone");
        Assert.IsTrue(log.Contains("Hidden on purpose by the edits: none"), string.Join("\n", log));
        StringAssert.Contains(md, "| `…/Normal/Normal_Display/Time_Timer` **cut off by Normal_Display: 0.9×57.4 of 118.5×59.3 px visible, practically gone** |");
        StringAssert.Contains(md, "`…/Timer/Plate_MatchTimeBg/CanvasPanel_0` **outside Normal_Display: not visible**");
        StringAssert.Contains(md, "A box marked *cut off* / *outside* loses more to a clipping ancestor than in stock");
        // nothing of the timer is checked against the screenshot: there is nothing of it to measure
        var check = md.Split('\n').Where(l => l.StartsWith("python ")).ToList();
        Assert.IsTrue(check.All(l => !l.Contains("Time_Timer") && !l.Contains("Timer/")), string.Join("\n", check));
        StringAssert.Contains(md, "# MatchTime: not checked at all: `…/Normal/Normal_Display/Time_Timer` is cut off by Normal_Display");
    }

    // ---- umgmod build (BuildCommand): the same plan, the Danger tick is --ack-danger ----

    private (int Exit, string Out, string Err) Cli(EditorController c, bool ackDanger, params string[] packages)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = BuildCommand.Run(c, Request(packages), ackDanger, new RetocProcess(Source.Config.RetocPath), output, error);
        return (exit, output.ToString(), error.ToString());
    }

    [TestMethod]
    public void Cli_DangerWarning_NeedsAckDanger_ThenBuilds()
    {
        var c = TimerOut();
        var (exit, output, error) = Cli(c, false, MatchTime);
        Console.WriteLine(output + error);
        Assert.AreEqual(BuildCommand.NeedsAck, exit);
        StringAssert.Contains(output, "[Danger] MatchTime: Time_Timer ends up almost completely outside Normal_Display");
        StringAssert.Contains(output, "(NOT acknowledged)");
        StringAssert.Contains(error, "refused: A danger warning is not acknowledged");
        StringAssert.Contains(error, "rerun with --ack-danger");
        Assert.IsFalse(Directory.Exists(Path.Combine(Mods.Root, ModName)), "refused: nothing written");

        (exit, output, error) = Cli(c, true, MatchTime);
        Console.WriteLine(output + error);
        Assert.AreEqual(BuildCommand.Done, exit, error);
        StringAssert.Contains(output, "(acknowledged by --ack-danger)");
        StringAssert.Contains(output, "retoc verify");
        StringAssert.Contains(output, $"OK: copy these 3 files from {Path.Combine(Mods.Root, ModName, "mod")}");
        var md = File.ReadAllText(Path.Combine(Mods.Root, ModName, ModInstructions.FileName));
        StringAssert.Contains(md, "*Acknowledged before the build (export dialog tick or umgmod build --ack-danger).*");
    }

    [TestMethod]
    public void Cli_NoDanger_BuildsC6WithoutTheFlag_OtherRefusalsAreExit2()
    {
        var c = C6();
        var (exit, output, error) = Cli(c, false, Base, MatchTime);
        Assert.AreEqual(BuildCommand.Done, exit, output + error);
        StringAssert.Contains(output, "layout warnings: none");
        foreach (var sha in new[] { "cdb2945a84c73359118fe9b8f51418364c9ecf35a2ac95529bba661d4bb8546d", "56ad50b6ce2183964a35f9c1b8571c3fb70e945d687fbec3f05d1a24a089409b" })
            StringAssert.Contains(output, sha, "byte-identical to canary C6, as before");
        Assert.IsTrue(File.Exists(Path.Combine(Mods.Root, ModName, ModInstructions.FileName)), "the CLI writes the test instructions too");

        // a refusal that isn't about acknowledging: 2, and --ack-danger doesn't get past it
        (exit, _, error) = Cli(c, true, Tv + "MatchTime/MatchTimeGoal");
        Assert.AreEqual(BuildCommand.Refused, exit);
        StringAssert.Contains(error, "MatchTimeGoal has no saved edits");
    }

    [TestMethod]
    public void Cli_DangerPlusAnotherRefusal_IsExit2()
    {
        var c = TimerOut();
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = BuildCommand.Run(c, Request(MatchTime) with { ContainerName = "no_suffix" }, false, new RetocProcess(Source.Config.RetocPath), output, error);
        Assert.AreEqual(BuildCommand.Refused, exit, "the flag alone wouldn't help, so not 'needs --ack-danger'");
        StringAssert.Contains(error.ToString(), "ending in _P");
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
