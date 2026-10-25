using System.Security.Cryptography;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Recipes;

/// <summary>
/// <c>umgmod recipe-build</c>: <see cref="RecipeCheck"/>, then the export's own request checks (mod and container names,
/// retoc, the .pak stub, every Danger warning acknowledged on the command line), then the one build path
/// (<see cref="ExportRunner"/>: pack, retoc verify, read-back, textures mounted, predictions, instructions), then
/// Expect.Files: the device files must be exactly the pinned ones, or mod\ is removed. Writes only under
/// work\out\mods\&lt;name&gt; (plus recipe.json there, a copy of the recipe built).
/// </summary>
public static class RecipeBuild
{
    public const int Done = 0, Refused = 2, NeedsAck = 3, PinMismatch = 4;
    public const string RecipeCopy = "recipe.json";

    public sealed record Outcome(int Code, RecipeCheckResult Check, ExportPlan? Plan, ExportResult? Result, IReadOnlyList<string> Refusals);

    /// <param name="modName">Folder under work\out\mods; the recipe's Name when null</param>
    /// <param name="ackDanger">--ack-danger: acknowledges every Danger warning (they are listed first)</param>
    public static ExportPlan Plan(Recipe recipe, RecipeCheckResult check, GameConfig config, string retocPath, string? modName, bool ackDanger)
    {
        var refusals = new List<string>(check.Refusals);
        var warnings = new List<string>();
        var mods = WriteGuard.ForMods(config);
        var name = modName ?? recipe.Name;
        var outDir = Path.Combine(mods.Root, name);
        var retoc = RetocInfo.Of(retocPath);
        if (ModBuilder.ModNameRefusal(name) is { } badName) refusals.Add(badName);
        else if (mods.Refusal(outDir) is { } outside) refusals.Add(outside);
        if (ModBuilder.ContainerNameRefusal(recipe.Container) is { } badContainer) refusals.Add(badContainer);
        if (!retoc.Exists) refusals.Add($"retoc not found at \"{retocPath}\" (setting RetocPath).");
        else if (!retoc.IsTested) warnings.Add($"This retoc (sha256 {retoc.Sha256[..16]}…) is not the tested build {ModBuilder.TestedRetocSha256[..16]}… (app/README.md); the mod is still verified after packing.");
        if (!File.Exists(config.ModStubPath)) refusals.Add($"The .pak stub {config.ModStubPath} doesn't exist (setting ModStubPak).");
        if (check.Packages.Count == 0 && check.LoosePaks.Count == 0 && check.Ok) refusals.Add("the recipe makes no package and no loose-file pak");

        var request = new ExportRequest(name, recipe.Container, check.Packages.Keys.ToList(), retocPath,
            ackDanger ? check.Danger.Select(w => w.Key).ToList() : null);
        var unticked = check.Danger.Where(w => !request.IsAcknowledged(w)).ToList();
        if (unticked.Count > 0)
            refusals.Add($"{(unticked.Count == 1 ? "A danger warning is" : $"{unticked.Count} danger warnings are")} not acknowledged (umgmod: --ack-danger): " +
                         string.Join("; ", unticked.Select(w => w.Package + ": " + w.Warning.Message)));
        if (refusals.Count == 0 && Directory.Exists(outDir)) warnings.Add($"{outDir} exists and will be replaced.");
        return new ExportPlan(request, $"recipe {recipe.Name}", outDir, retoc, refusals, warnings,
            refusals.Count == 0 ? check.Packages.Values.ToList() : [], check.Edits, check.ExportEdits, check.Warnings)
        {
            Textures = check.Textures?.Items ?? [],
            ExpectedTextures = [.. check.Textures?.Expected ?? [], .. check.CloneMips],
            ExpectedNames = check.RepointNames,
            LoosePaks = check.LoosePaks,
        };
    }

    public static Outcome Run(Recipe recipe, string recipeText, RecipeContext context, ExportEnvironment env, IRetoc retoc, string? modName, bool ackDanger,
        IProgress<string>? log = null)
    {
        var check = RecipeCheck.Run(recipe, context);
        if (!check.Ok) return new Outcome(Refused, check, null, null, check.Refusals);
        var plan = Plan(recipe, check, context.Source.Config, env.Source.Config.RetocPath, modName, ackDanger);
        if (!plan.Ok)
            return new Outcome(plan.Unacknowledged.Count > 0 && plan.Refusals.Count == 1 ? NeedsAck : Refused, check, plan, null, plan.Refusals);

        var result = ExportRunner.Run(plan, env with { Project = plan.Project }, retoc, log);
        if (!result.Built) return new Outcome(Refused, check, plan, result, result.Refusals);
        var mods = WriteGuard.ForMods(context.Source.Config);
        mods.WriteAllText(Path.Combine(plan.OutDir, RecipeCopy), recipeText);

        if (recipe.Expect?.Files is { } pinned)
        {
            var problems = new List<string>();
            var built = result.Files.ToDictionary(f => f.Name, f => f.Sha256, StringComparer.Ordinal);
            foreach (var (file, sha) in pinned)
                if (!built.TryGetValue(file, out var got)) problems.Add($"Expect.Files: {file} isn't built");
                else if (got != sha) problems.Add($"Expect.Files: {file} is sha256 {got[..12]}…, pinned {sha[..12]}…");
            foreach (var file in built.Keys.Where(f => !pinned.ContainsKey(f))) problems.Add($"Expect.Files: {file} is built but not pinned");
            if (problems.Count > 0)
            {
                mods.DeleteDirectory(result.ModDir);
                mods.WriteAllText(Path.Combine(plan.OutDir, "FAILED.txt"), string.Join(Environment.NewLine, problems) + Environment.NewLine + result.Log);
                log?.Report($"FAILED: the device files aren't the pinned ones; {result.ModDir} removed");
                return new Outcome(PinMismatch, check, plan, result, problems);
            }
            log?.Report($"Expect.Files: all {pinned.Count} device files as pinned");
        }
        // the disable script for these files (it never touches the phone: a checklist, or moves in a folder on this PC)
        var (script, _, why) = DisableScript.Write(context.Source.Config, plan.OutDir, DateTime.Now);
        log?.Report(script != null ? $"Disable script: {script} (no arguments: the files to delete on the phone)" : $"warning: no disable script: {why}");
        return new Outcome(Done, check, plan, result, []);
    }

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
