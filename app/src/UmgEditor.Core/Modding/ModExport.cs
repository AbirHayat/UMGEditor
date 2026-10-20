using System.Globalization;
using System.Text;
using UmgEditor.Core.Editing;

namespace UmgEditor.Core.Modding;

/// <summary>What the export dialog asks for.</summary>
public sealed record ExportRequest(string ModName, string ContainerName, IReadOnlyList<string> Packages, string RetocPath);

/// <summary>The retoc executable the export will run, identified by sha256 (this retoc has no --version).</summary>
public sealed record RetocInfo(string Path, bool Exists, string Sha256)
{
    public bool IsTested => Sha256 == ModBuilder.TestedRetocSha256;

    public string Text => !Exists ? $"not found: {Path}"
        : $"sha256 {Sha256} " + (IsTested ? "(the tested build, see app/README.md)" : $"(NOT the tested build {ModBuilder.TestedRetocSha256[..16]}…)");

    public static RetocInfo Of(string path)
    {
        var full = string.IsNullOrWhiteSpace(path) ? "" : System.IO.Path.GetFullPath(path.Trim());
        var exists = full.Length > 0 && File.Exists(full);
        return new RetocInfo(full, exists, exists ? RetocProcess.Sha256Of(full) : "");
    }
}

/// <summary>A package of the project the dialog can export (it has saved edits).</summary>
public sealed record ExportCandidate(string PackagePath, int SavedEdits, bool Dirty)
{
    public string Name => GamePaths.ShortName(PackagePath);
}

/// <summary>One saved value the mod ships, for the instructions.</summary>
public sealed record ExportEdit(string Package, string Widget, string Field, double Stock, double Value, string OffsetHex, string OldHex, string NewHex);

/// <summary>
/// The checked export: everything the background build needs (the saved chunks are read here, on the caller's
/// thread), or why it is refused. Warnings don't block (a retoc that isn't the tested build, a folder that is replaced).
/// </summary>
public sealed record ExportPlan(
    ExportRequest Request, string Project, string OutDir, RetocInfo Retoc,
    IReadOnlyList<string> Refusals, IReadOnlyList<string> Warnings,
    IReadOnlyList<ModPackage> Packages, EditSet SavedEdits, IReadOnlyList<ExportEdit> Edits)
{
    public bool Ok => Refusals.Count == 0;
    public string ModDir => Path.Combine(OutDir, "mod");
}

/// <summary>Outcome of a background export. When not built, mod\ was removed (FAILED.txt says why) and nothing else is written.</summary>
public sealed record ExportResult(bool Built, IReadOnlyList<string> Refusals, string OutDir, IReadOnlyList<ModFile> Files, string Log,
    string? InstructionsPath, IReadOnlyList<string> PredictionFiles, ImpactReport? Impact)
{
    public string ModDir => Path.Combine(OutDir, "mod");
}

/// <summary>The test-instructions file written next to a built mod (the format of work/out/mods/CANARY_C6.md).</summary>
public static class ModInstructions
{
    public const string FileName = "TEST_INSTRUCTIONS.md";
    public const string OnlineWarning = "Remove the mod before playing online";

    /// <summary>What a screen is called in game, how to get it on screen, for the known scoreboard packages.</summary>
    private static readonly Dictionary<string, (string Screen, string How)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MatchTime"] = ("scoreboard", "≥ 10 s into play"),
        ["MatchTimeGoal"] = ("goal-scorer banner", "after you score"),
        ["MatchTimeCard"] = ("card pop-up", "commit fouls until a yellow card"),
        ["MatchTimeMemberChange"] = ("substitution pop-up", "make a substitution at a stoppage"),
        ["MatchTimeConnection"] = ("online scoreboard", "an online match"),
        ["MatchTimeConnectionGoal"] = ("online goal-scorer banner", "a goal in an online match"),
        ["MatchTimeConnectionCard"] = ("online card pop-up", "a card in an online match"),
        ["MatchTimeConnectionMemberChange"] = ("online substitution pop-up", "a substitution in an online match"),
    };

    public static string ScreenName(string package) => Known.TryGetValue(package, out var k) ? k.Screen : package;
    public static string HowToSee(string package) => Known.TryGetValue(package, out var k) ? k.How : "find this screen in game";

    /// <param name="isScreen">Whether a package is shown on its own (nothing embeds it); embedded parts show inside the screens</param>
    public static string Build(ExportPlan plan, ModResult build, ImpactReport impact, Func<string, bool> isScreen, DateTime when)
    {
        var name = plan.Request.ModName;
        var container = plan.Request.ContainerName;
        var screens = impact.Packages.Where(p => isScreen(p.PackagePath)).ToList();
        var offline = screens.Where(p => !p.IsOnline).ToList();
        var online = screens.Where(p => p.IsOnline).ToList();
        var parts = impact.Packages.Where(p => !isScreen(p.PackagePath)).ToList();
        var sb = new StringBuilder();
        void L(string line = "") => sb.Append(line).Append('\n');

        L($"# Mod {name}: test instructions (built by the UMG editor, {when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)})");
        L();
        L($"> **{OnlineWarning}.** Delete the {build.Files.Count} files below from the device before any online match" +
          (online.Count > 0 ? $": these edits also change {string.Join(", ", online.Select(p => $"the {ScreenName(p.Name)} ({p.Name})"))}." : "."));
        L();
        L("| | |");
        L("|---|---|");
        L($"| Project | {plan.Project} |");
        L($"| Folder | `{build.ModDir}` (copy only these files; the predictions and this file stay here) |");
        L($"| Container | `{container}` ({plan.Packages.Count} package{(plan.Packages.Count == 1 ? "" : "s")}: {string.Join(", ", plan.Packages.Select(p => GamePaths.ShortName(p.PackagePath)))}) |");
        L($"| retoc | `{plan.Retoc.Path}` sha256 `{plan.Retoc.Sha256}` {(plan.Retoc.IsTested ? "(the tested build)" : "(**not** the tested build)")} |");
        L("| Checks passed | retoc verify, CUE4Parse read-back of every package byte for byte |");
        L();
        L("## Files to copy");
        L("| File | Size | sha256 |");
        L("|---|---|---|");
        foreach (var f in build.Files) L($"| `{f.Name}` | {f.Size:N0} B | `{f.Sha256}` |");
        L();
        L("## Edits");
        L("| Package | Widget | Field | Stock → value | Bytes |");
        L("|---|---|---|---|---|");
        foreach (var e in plan.Edits)
            L($"| {e.Package} | {e.Widget} | {e.Field} | {SlotEditor.Format(e.Stock)} → **{SlotEditor.Format(e.Value)}** | `{e.OldHex}` → `{e.NewHex}` @ {e.OffsetHex} |");
        if (parts.Count > 0)
            L($"\n{string.Join(", ", parts.Select(p => p.Name))} {(parts.Count == 1 ? "is" : "are")} embedded in the screens below, so {(parts.Count == 1 ? "it changes" : "they change")} inside them.");
        L();
        L("## Steps");
        L($"1. Force-stop eFootball. In the folder with `pc0010_mobile_and.*`, delete any old `{container}.*`, then copy the {build.Files.Count} files from `{build.ModDir}`.");
        L($"2. Offline match vs AI. Screenshots (1920×1080, same LDPlayer setup) into `ss\\`:");
        foreach (var p in offline)
            L($"   - `{Shot(name, p.Name)}`: the {ScreenName(p.Name)} ({p.Name}), {HowToSee(p.Name)}" + (p.Boxes.Count == 0 ? " (no box changes predicted)" : ""));
        if (offline.Count == 0) L("   - (no offline screen changes)");
        L($"3. **{OnlineWarning}**: delete the {build.Files.Count} files again before playing online.");
        L();
        L("## Every affected screen");
        L("| Screen | Package | Online | Boxes that change |");
        L("|---|---|---|---|");
        foreach (var p in screens)
            L($"| {ScreenName(p.Name)} | {p.Name} | {(p.IsOnline ? "**online**: predicted only, don't take the mod online" : "offline")} | {p.ChangeText} |");
        L();
        L("## Predicted rects (game px; LDPlayer screenshot px = game px + 50 / + 32)");
        L($"Stock and edited layouts at {impact.ViewportWidth}×{impact.ViewportHeight} for `validate_layout.py`: `{Path.Combine(plan.OutDir, "predictions")}`.");
        foreach (var p in screens)
        {
            L();
            L($"### {ScreenName(p.Name)} ({p.Name}){(p.IsOnline ? " · online" : "")}");
            if (p.Boxes.Count == 0) { L("No box changes."); continue; }
            L("| Element | stock L / R / T / B | edited L / R / T / B | change |");
            L("|---|---|---|---|");
            foreach (var b in p.Boxes)
            {
                string Lrtb(Func<EdgeChange, double> v) => string.Join(" / ", b.Edges.Select(e => EdgeChange.F(v(e))));
                var change = string.Join(", ", b.Edges.Where(e => e.Changed).Select(e => $"{e.Edge} {e.DeltaText}"));
                L($"| `{b.ShortPath}`{(b.Visible ? "" : " (hidden)")} | {Lrtb(e => e.BeforePx)} | {Lrtb(e => e.AfterPx)} | {change} |");
            }
        }
        L();
        L("## Checking");
        L("```");
        foreach (var p in offline.Where(p => p.Boxes.Any(b => b.Visible)))
        {
            var targets = p.Boxes.Where(b => b.Visible).Select(b =>
                $"{string.Join("/", b.Path.Split('/').TakeLast(4))}:{string.Concat(b.Edges.Where(e => e.Changed).Select(e => e.Edge[0]))}");
            L($"python work/scripts/validate_layout.py {Path.Combine(plan.OutDir, "predictions", p.Name + ".json")} ss/{Shot(name, p.Name)} 50 32 {string.Join(" ", targets)}");
        }
        L("```");
        L("Or send the screenshots.");
        return sb.ToString();
    }

    public static string Shot(string mod, string package) => $"{mod}_{package}.png";
}

/// <summary>Reports every retoc command to the export log (the build runs in the background).</summary>
internal sealed class LoggingRetoc(IRetoc inner, IProgress<string>? log) : IRetoc
{
    public string ExePath => inner.ExePath;

    public RetocResult Run(params string[] args)
    {
        log?.Report($"retoc {string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");
        var r = inner.Run(args);
        log?.Report($"  exit {r.ExitCode}{(r.Output.Length > 0 ? ": " + r.Output.Replace("\r", "").Replace("\n", " | ") : "")}");
        return r;
    }
}
