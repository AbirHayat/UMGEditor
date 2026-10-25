using UmgEditor.Core.Editing;
using UmgEditor.Core.Layout;
using UmgEditor.Core.Patching;
using UmgEditor.Core.Textures;

namespace UmgEditor.Core.Modding;

/// <summary>What a build needs besides the plan: the game, the layout (predictions, warnings) and the usage graph.</summary>
/// <param name="Project">The edit set or recipe the mod comes from (shown in the instructions)</param>
public sealed record ExportEnvironment(GameSource Source, LayoutEngine Engine, UsageGraph Usage, Func<string, WidgetPackageTree> LoadTree,
    int Width, int Height, string Project);

/// <summary>
/// The one build path for a checked <see cref="ExportPlan"/> (the export dialog, umgmod build, umgmod recipe-build):
/// ModBuilder packs and verifies the container (a failed retoc verify or read-back aborts and removes mod\), new
/// texture packages are checked with the mod mounted on the game, then the stock vs edited layouts of every package
/// the edits reach go to predictions\ and the test instructions next to mod\. Writes only under work\out\mods\&lt;name&gt;.
/// </summary>
public static class ExportRunner
{
    public static ExportResult Run(ExportPlan plan, ExportEnvironment env, IRetoc retoc, IProgress<string>? log = null)
    {
        if (!plan.Ok) return new ExportResult(false, plan.Refusals, plan.OutDir, [], "", null, [], null);
        log?.Report($"Building {plan.Request.ModName} ({plan.Request.ContainerName}) from {plan.Packages.Count} package{(plan.Packages.Count == 1 ? "" : "s")}: {string.Join(", ", plan.Packages.Select(p => GamePaths.ShortName(p.PackagePath)))}");
        var build = new ModBuilder(env.Source, new LoggingRetoc(retoc, log)).Build(plan.Request.ModName, plan.Request.ContainerName, plan.Packages);
        if (!build.Built)
        {
            log?.Report("FAILED: " + string.Join(" | ", build.Refusals));
            log?.Report(Directory.Exists(build.ModDir) ? $"{build.ModDir} is still there" : $"{build.ModDir} removed; see FAILED.txt");
            return new ExportResult(false, build.Refusals, build.OutDir, [], build.Log, null, [], null);
        }
        foreach (var f in build.Files) log?.Report($"  {f.Name}  {f.Size:N0} bytes  sha256 {f.Sha256}");
        if (plan.ExpectedTextures.Count > 0 || plan.ExpectedNames.Count > 0)
        {
            log?.Report($"Mounting the mod on top of the game: {plan.ExpectedTextures.Count} new texture package(s) must load by path and decode to the imported pixels" +
                        (plan.ExpectedNames.Count > 0 ? $", {plan.ExpectedNames.Count} re-pointed package(s) must name their new path" : "") + "…");
            if (TextureMountCheck(env.Source, plan, build.ModDir) is { } problem)
            {
                var guard = WriteGuard.ForMods(env.Source.Config);
                guard.DeleteDirectory(build.ModDir);
                guard.WriteAllText(Path.Combine(plan.OutDir, "FAILED.txt"), problem + Environment.NewLine + build.Log);
                log?.Report("FAILED: " + problem);
                return new ExportResult(false, [problem], build.OutDir, [], build.Log, null, [], null);
            }
        }

        log?.Report("Laying out every package the saved edits reach (stock vs edited)…");
        var mods = WriteGuard.ForMods(env.Source.Config);
        var impacts = new EditImpact(env.Engine, env.Usage, env.Width, env.Height).Predict(plan.SavedEdits, env.LoadTree);
        var predictions = Path.Combine(plan.OutDir, "predictions");
        var files = new List<string>();
        foreach (var i in impacts.Where(i => i.Error == null))
        {
            foreach (var (suffix, layout) in new[] { (".stock.json", i.Stock), (".json", i.Edited) })
            {
                var file = Path.Combine(predictions, i.Package.Name + suffix);
                mods.WriteAllText(file, LayoutExport.ToJson(layout, i.Package.PackagePath));
                files.Add(file);
            }
        }
        var report = ImpactReport.Build(env.Project, env.Width, env.Height, impacts);
        foreach (var w in ExportWarning.From(report))
            log?.Report($"  {w.Text}{(w.IsDanger ? " (acknowledged)" : "")}");
        var instructions = Path.Combine(plan.OutDir, ModInstructions.FileName);
        mods.WriteAllText(instructions, ModInstructions.Build(plan, build, report, p => env.Usage.DirectUsers(p).Count == 0, DateTime.Now, p => env.Usage.AllUsers(p)));
        log?.Report($"{files.Count} prediction files in {predictions}");
        log?.Report($"Test instructions: {instructions}");
        log?.Report($"Done. Copy the {build.Files.Count} files in {build.ModDir} to the device. {ModInstructions.OnlineWarning}.");
        return new ExportResult(true, [], plan.OutDir, build.Files, build.Log, instructions, files, report);
    }

    /// <summary>
    /// With the built mod mounted on top of the stock game (as the canaries were checked): every new texture package loads by
    /// path and its mip 0 is the imported pixels, and every re-pointed package names its new texture. Returns the problem, or null.
    /// </summary>
    public static string? TextureMountCheck(GameSource source, ExportPlan plan, string modDir)
    {
        try
        {
            using var withMod = GameSource.Open(source.Config with { ExtraPaksDirs = [modDir] });
            foreach (var (path, mip0) in plan.ExpectedTextures)
            {
                var tex = withMod.LoadPackage(path).GetExports().OfType<CUE4Parse.UE4.Assets.Exports.Texture.UTexture2D>().SingleOrDefault();
                if (tex == null || !(tex.PlatformData.Mips[0].BulkData?.Data ?? []).AsSpan().SequenceEqual(mip0))
                    return $"mounted: {path} {(tex == null ? "doesn't load" : "doesn't decode to the imported pixels")}";
            }
            foreach (var item in plan.Textures)
                foreach (var package in item.Storing)
                {
                    var named = withMod.LoadPackage(package).GetExports().Select(WidgetTreeReader.StoredValues)
                        .Count(v => v.Values.Any(x => TextureUsage.NameMapForm(x) == item.To));
                    if (named == 0) return $"mounted: no widget in {GamePaths.ShortName(package)} names {item.To}";
                }
            foreach (var (package, names) in plan.ExpectedNames)
            {
                var named = withMod.LoadPackage(package).GetExports().Select(WidgetTreeReader.StoredValues)
                    .Count(v => v.Values.Any(x => TextureUsage.NameMapForm(x) == names));
                if (named == 0) return $"mounted: no export in {GamePaths.ShortName(package)} names {names}";
            }
            return null;
        }
        catch (Exception e) { return $"mounted: the game with the mod can't be read ({e.GetType().Name}: {e.Message})"; }
    }
}
