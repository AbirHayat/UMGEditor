using System.Globalization;
using System.Text;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Modding;

/// <summary>What the export dialog asks for.</summary>
/// <param name="Acknowledged">Keys (<see cref="ExportWarning.Key"/>) of the Danger warnings the user ticked</param>
public sealed record ExportRequest(string ModName, string ContainerName, IReadOnlyList<string> Packages, string RetocPath,
    IReadOnlyCollection<string>? Acknowledged = null)
{
    public bool IsAcknowledged(ExportWarning w) => Acknowledged?.Contains(w.Key) == true;
}

/// <summary>
/// A warning of the Impact panel for the saved edits being shipped, per package it concerns. A Danger warning (something
/// will vanish) has to be ticked before the build.
/// </summary>
public sealed record ExportWarning(string Package, bool Online, EditWarning Warning)
{
    public bool IsDanger => Warning.Severity == Severity.Danger;
    /// <summary>Identifies the warning across re-plans (the same edits give the same text).</summary>
    public string Key => $"{Package}: {Warning.Message}";
    public string Text => $"[{Warning.Severity}] {Package}{(Online ? " (online)" : "")}: {Warning.Message}";

    public static IReadOnlyList<ExportWarning> From(ImpactReport impact) =>
        impact.Packages.SelectMany(p => p.Warnings.Select(w => new ExportWarning(p.Name, p.IsOnline, w)))
            .OrderByDescending(w => w.Warning.Severity).ToList();
}

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
public sealed record ExportCandidate(string PackagePath, int SavedEdits, bool Dirty, int TextureEdits = 0)
{
    public string Name => GamePaths.ShortName(PackagePath);
}

/// <summary>One saved value the mod ships, for the instructions.</summary>
public sealed record ExportEdit(string Package, string Widget, string Field, double Stock, double Value, string OffsetHex, string OldHex, string NewHex)
{
    /// <summary>The rows of one package's plan: in-place values, animation writes, inserted tags.</summary>
    /// <param name="slot">(slot export, slot name) → how the instructions name it, e.g. "Text_Score_Home (CanvasPanelSlot_0)"</param>
    public static IEnumerable<ExportEdit> Of(BytePlan plan, Func<int, string, string> slot, Func<FieldKey, double?> baseValue)
    {
        var package = GamePaths.ShortName(plan.PackagePath);
        foreach (var e in plan.Entries)
            yield return new ExportEdit(package, slot(e.Key.SlotExport, e.SlotName), e.Key.Path.Replace("LayoutData.", ""), e.OldValue, e.NewValue, e.OffsetHex, e.OldHex, e.NewHex);
        foreach (var w in plan.AnimWrites)
            yield return new ExportEdit(package, w.Description, w.Copy == "range" ? "animation range bound" : $"animation key, {w.Copy} copy",
                w.OldValue, w.NewValue, $"{w.OffsetHex} ({w.WindowText})", w.OldHex, w.NewHex);
        foreach (var i in plan.Insertions)
            yield return new ExportEdit(package, slot(i.ExportIndex, i.SlotName), i.Path.Replace("LayoutData.", "") + " (new tag)", i.Key is { } k ? baseValue(k) ?? 0 : 0, i.Value,
                $"{i.InsertAtHex} (inserted before {i.Before}, +{TagInserter.FloatTagSize} bytes)", "(not stored)", i.TagHex);
    }
}

/// <summary>
/// The checked export: everything the background build needs (the saved chunks are read here, on the caller's
/// thread), or why it is refused. Warnings don't block (a retoc that isn't the tested build, a folder that is replaced).
/// ImpactWarnings are the Impact panel's warnings for the saved edits picked; an unticked Danger one is a refusal.
/// </summary>
public sealed record ExportPlan(
    ExportRequest Request, string Project, string OutDir, RetocInfo Retoc,
    IReadOnlyList<string> Refusals, IReadOnlyList<string> Warnings,
    IReadOnlyList<ModPackage> Packages, EditSet SavedEdits, IReadOnlyList<ExportEdit> Edits,
    IReadOnlyList<ExportWarning> ImpactWarnings)
{
    public bool Ok => Refusals.Count == 0;
    public string ModDir => Path.Combine(OutDir, "mod");
    public IReadOnlyList<ExportWarning> Unacknowledged => ImpactWarnings.Where(w => w.IsDanger && !Request.IsAcknowledged(w)).ToList();
    /// <summary>The texture edits shipped (step 3): what each one re-points and clones</summary>
    public IReadOnlyList<TextureEditInfo> Textures { get; init; } = [];
    /// <summary>Each new texture package and the mip 0 it must decode to once mounted</summary>
    public IReadOnlyList<(string PackagePath, byte[] Mip0)> ExpectedTextures { get; init; } = [];
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
    /// <param name="embeddedBy">Every package that shows a package (for where texture edits are seen)</param>
    public static string Build(ExportPlan plan, ModResult build, ImpactReport impact, Func<string, bool> isScreen, DateTime when,
        Func<string, IReadOnlyCollection<string>>? embeddedBy = null)
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
        L($"| Checks passed | retoc verify, CUE4Parse read-back of every package byte for byte{(plan.ExpectedTextures.Count > 0 ? $"; the mod mounted on the game: {plan.ExpectedTextures.Count} new texture package(s) load by path and decode to the images" : "")} |");
        L();
        var warnings = ExportWarning.From(impact);
        L("## Warnings (Impact panel)");
        if (warnings.Count == 0) L("None: no rescale, no widget pushed out of a clip, nothing outside the edited widgets moves.");
        foreach (var w in warnings)
            L($"- **{w.Warning.Severity}** · {w.Package}{(w.Online ? " (online)" : "")}: {w.Warning.Message}" +
              (w.IsDanger ? plan.Request.IsAcknowledged(w) ? " *Acknowledged before the build (export dialog tick or umgmod build --ack-danger).*" : " *Not acknowledged.*" : ""));
        L();
        L("## Files to copy");
        L("| File | Size | sha256 |");
        L("|---|---|---|");
        foreach (var f in build.Files) L($"| `{f.Name}` | {f.Size:N0} B | `{f.Sha256}` |");
        L();
        L("## Edits");
        L("| Package | Widget | Field | Stock → value | Bytes |");
        L("|---|---|---|---|---|");
        if (plan.Edits.Count == 0) L("| (no layout or animation values) | | | | |");
        foreach (var e in plan.Edits)
            L($"| {e.Package} | {e.Widget} | {e.Field} | {SlotEditor.Format(e.Stock)} → **{SlotEditor.Format(e.Value)}** | `{e.OldHex}` → `{e.NewHex}` @ {e.OffsetHex} |");
        if (plan.Edits.Any(e => e.Field.StartsWith("animation ", StringComparison.Ordinal)))
        {
            L();
            L($"Animation key rows (branch experiment): each key is written in its section and, where one exists, in its compiled copy, with the same old and new bytes. " +
              $"Location proof (animation keys only): the section's own tags give the offset, the compiled copy sits in the animation's compiled export, the stock bytes decode " +
              $"to the parsed value, and the *unique stock window* is the smallest window around the write, computed on the stock bytes, that occurs exactly once in the chunk " +
              $"(refused beyond ±{Patching.BytePlanner.MaxAnimWindow}). The exact byte diff and a full re-parse of every export were hard checks before the save.");
        }
        if (parts.Count > 0)
            L($"\n{string.Join(", ", parts.Select(p => p.Name))} {(parts.Count == 1 ? "is" : "are")} embedded in the screens below, so {(parts.Count == 1 ? "it changes" : "they change")} inside them.");
        if (plan.Textures.Count > 0)
        {
            L();
            L("## Textures");
            L("Each image is in new texture packages (both theme folders where the game has them) and the widget's texture path is re-pointed to them. " +
              "Checked before the build: every clone decodes back to the image, every re-point is an exact byte diff + re-parse, every package's layers add up against stock; " +
              "after it: the mod mounted on top of the game loads each new package by path with the image's pixels.");
            L();
            L("| Widget | Stock texture → new | Size | New packages | Re-pointed in | Margins 0 |");
            L("|---|---|---|---|---|---|");
            foreach (var t in plan.Textures)
                L($"| {GamePaths.ShortName(t.Edit.Package)}/{t.Edit.Widget} | {GamePaths.ShortName(t.Edit.Texture)} → **{t.Edit.NewName}**{(t.Edit.Mode == TextureEditMode.White ? " (white)" : "")} | " +
                  $"{t.StockWidth}x{t.StockHeight} → {t.Width}x{t.Height} | {string.Join(", ", t.ClonePaths.Select(p => p[(p.IndexOf("/Widget/", StringComparison.Ordinal) + 8)..]))} | " +
                  $"{string.Join(", ", t.Storing.Select(GamePaths.ShortName))} | {(t.Edit.MarginsZero ? "yes" : "no")} |");
            foreach (var w in plan.Textures.SelectMany(t => t.Warnings).Distinct())
                L($"- {w}");
        }
        L();
        L("## Steps");
        L($"1. Force-stop eFootball. In the folder with `pc0010_mobile_and.*`, delete any old `{container}.*`, then copy the {build.Files.Count} files from `{build.ModDir}`.");
        L($"2. Offline match vs AI. Screenshots (1920×1080, same LDPlayer setup) into `ss\\`:");
        // texture edits change no box, so the Impact panel lists no screen for them: the screens that show a re-pointed package
        var textureScreens = plan.Textures.SelectMany(t => t.Storing.SelectMany(p => (embeddedBy?.Invoke(p) ?? []).Append(p))
                .Where(p => isScreen(p) && !p.Contains("/MatchTimeConnection/", StringComparison.OrdinalIgnoreCase))
                .Select(p => (Package: GamePaths.ShortName(p), Widget: $"{GamePaths.ShortName(t.Edit.Package)}/{t.Edit.Widget}", t.Edit.NewName)))
            .GroupBy(s => s.Package).ToDictionary(g => g.Key, g => g.Select(s => $"{s.Widget} ({s.NewName})").Distinct().ToList());
        string Images(string screen) => textureScreens.TryGetValue(screen, out var list) ? $": the new image{(list.Count == 1 ? "" : "s")} on {string.Join(", ", list)}" : "";
        foreach (var p in offline)
            L($"   - `{Shot(name, p.Name)}`: the {ScreenName(p.Name)} ({p.Name}), {HowToSee(p.Name)}" + (p.Boxes.Count == 0 ? " (no box changes predicted)" : "") + Images(p.Name));
        foreach (var screen in textureScreens.Keys.Where(k => offline.All(o => o.Name != k)))
            L($"   - `{Shot(name, screen)}`: the {ScreenName(screen)} ({screen}), {HowToSee(screen)}{Images(screen)}");
        if (offline.Count == 0 && textureScreens.Count == 0) L("   - (no offline screen changes)");
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
                L($"| `{b.ShortPath}`{(b.Visible ? "" : " (hidden)")}{(b.CutOff ? $" **{b.Cut!.Text}**" : "")} | {Lrtb(e => e.BeforePx)} | {Lrtb(e => e.AfterPx)} | {change} |");
            }
            if (p.Boxes.Any(b => b.CutOff))
                L("\nA box marked *cut off* / *outside* loses more to a clipping ancestor than in stock: only the part inside the ancestor shows, and its edges outside it are not on screen.");
        }
        L();
        L("## Checking");
        L("```");
        foreach (var p in offline)
        {
            // only edges that are on screen where predicted: a clipped edge shows as the clip line, not as the box
            var checks = p.Boxes.Where(b => b.Visible)
                .Select(b => (Box: b, Edges: b.Edges.Where(e => e.Changed && (b.Cut == null || b.Cut.EdgeShows(e.Edge))).ToList())).ToList();
            var targets = checks.Where(c => c.Edges.Count > 0)
                .Select(c => $"{string.Join("/", c.Box.Path.Split('/').TakeLast(4))}:{string.Concat(c.Edges.Select(e => e.Edge[0]))}").ToList();
            if (targets.Count > 0)
                L($"python work/scripts/validate_layout.py {Path.Combine(plan.OutDir, "predictions", p.Name + ".json")} ss/{Shot(name, p.Name)} 50 32 {string.Join(" ", targets)}");
            foreach (var c in checks.Where(c => c.Box.Cut != null && c.Edges.Count < c.Box.Edges.Count(e => e.Changed)))
                L($"# {p.Name}: not checked {(c.Edges.Count == 0 ? "at all" : string.Join("/", c.Box.Edges.Where(e => e.Changed && !c.Edges.Contains(e)).Select(e => e.Edge)))}: `{c.Box.ShortPath}` is {c.Box.Cut!.Text}");
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
