using System.Security.Cryptography;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Recipes;

/// <summary>
/// <c>umgmod recipe-from-set</c>: an app project (edit set + textures.json) as a recipe, so anything made in the app can be
/// checked and rebuilt without it. Only saved work: refused while anything is unsaved or a saved file no longer equals a
/// rebuild of its edits. Images become inputs pinned by sha256 (paths inside the project's _art folder). No Expect: pins
/// come from a device-tested build.
/// </summary>
public static class RecipeFromSet
{
    public static (Recipe? Recipe, IReadOnlyList<string> Refusals) Make(EditorController controller, string root, string name, string container)
    {
        var refusals = new List<string>();
        if (controller.AnyDirty) refusals.Add($"Project {controller.Project} has unsaved edits in {controller.UnsavedPackagesText}; save them first.");

        var packages = new List<RecipePackage>();
        foreach (var s in controller.Sessions.Where(s => s.Saved.Count > 0).OrderBy(s => s.PackagePath, StringComparer.Ordinal))
        {
            var (package, why) = SavedValues(s);
            if (package == null) { refusals.Add(why!); continue; }
            packages.Add(package);
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var inputs = new Dictionary<string, RecipeInput>(StringComparer.Ordinal);
        var textures = new List<RecipeTexture>();
        foreach (var e in controller.TextureEdits)
        {
            string? id = null;
            if (e.Image != null)
            {
                var full = Path.GetFullPath(Path.Combine(controller.ProjectFolder, e.Image));
                if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) { refusals.Add($"{e.Widget}: {full} is outside the workspace"); continue; }
                if (!File.Exists(full)) { refusals.Add($"{e.Widget}: the imported image {e.Image} is missing from the project"); continue; }
                var sha = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
                if (e.ImageSha256 != null && e.ImageSha256 != sha) { refusals.Add($"{e.Widget}: {e.Image} changed since it was imported"); continue; }
                id = "art-" + sha[..12];
                inputs[id] = new RecipeInput(Path.GetRelativePath(fullRoot, full).Replace('\\', '/'), sha);
            }
            textures.Add(new RecipeTexture(e.Package, e.Widget, e.Texture, e.Mode, e.NewName, e.Scope, id, e.Source, e.MarginsZero));
        }
        if (packages.Count == 0 && textures.Count == 0 && refusals.Count == 0) refusals.Add($"Project {controller.Project} has no saved edits and no texture edits.");
        if (refusals.Count > 0) return (null, refusals);

        var recipe = new Recipe(Recipe.CurrentVersion, name, container, inputs.Count > 0 ? inputs : null, packages.Count > 0 ? packages : null,
            textures.Count > 0 ? textures : null);
        // what is written must read back as the same recipe and pass the data checks
        var (back, bad) = RecipeReader.Read(RecipeReader.Write(recipe));
        if (back == null) return (null, bad);
        return (recipe, []);
    }

    /// <summary>
    /// A session's saved edits as a recipe package (Values only, ordered by export and path), refused unless its saved
    /// file equals a rebuild of them. Also used by umgcompare recipe-ucl for the canaries' edit sets.
    /// </summary>
    public static (RecipePackage? Package, string? Why) SavedValues(PatchSession s, IReadOnlyList<RecipeLayer>? layers = null)
    {
        var (chunk, why) = s.ReadSavedChunk();
        if (chunk == null) return (null, why);
        return (new RecipePackage(s.PackagePath, s.StockSha256, s.Saved.Values
            .OrderBy(kv => kv.Key.SlotExport).ThenBy(kv => kv.Key.Path, StringComparer.Ordinal)
            .Select(kv => new RecipeValue(kv.Key.SlotExport, s.SlotName(kv.Key.SlotExport), kv.Key.Path, s.BaseValue(kv.Key) ?? double.NaN, kv.Value,
                s.StockValue(kv.Key) == null)).ToList(), layers), null);
    }
}
