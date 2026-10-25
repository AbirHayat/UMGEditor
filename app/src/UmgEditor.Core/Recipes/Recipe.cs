using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UmgEditor.Core.Modding;

namespace UmgEditor.Core.Recipes;

/// <summary>
/// A recipe (version 1): a whole mod as data. Every value names its stock value, every input file its sha256, every
/// package its stock chunk's sha256, so a recipe only ever reproduces what it says or is refused. There are no switches
/// that change a check: the tools' own checks (exact diff, re-parse, hard refusals) always run, and a Danger layout
/// warning still needs <c>--ack-danger</c> on the command line. No keys, no game bytes: text and hashes only.
/// </summary>
/// <param name="Version">JSON "Recipe": the format version, 1</param>
/// <param name="Name">The mod's name (work\out\mods\&lt;Name&gt; when built)</param>
/// <param name="Container">The mod container's name, e.g. pc9999_mobile_and_P</param>
/// <param name="Inputs">Files the recipe reads (images, fonts), by id: a path relative to the workspace root + sha256</param>
/// <param name="Packages">Edited stock packages: stored values, inserted tags, animation keys, frames, interp modes, range bounds</param>
/// <param name="Textures">Texture replacements, as the app's texture edits (a clone per theme folder + the re-point)</param>
/// <param name="Expect">Pins: what the recipe must produce, by sha256</param>
public sealed record Recipe(
    [property: JsonPropertyName("Recipe")] int Version,
    string Name,
    string Container,
    IReadOnlyDictionary<string, RecipeInput>? Inputs = null,
    IReadOnlyList<RecipePackage>? Packages = null,
    IReadOnlyList<RecipeTexture>? Textures = null,
    RecipeExpect? Expect = null)
{
    public const int CurrentVersion = 1;
    public IReadOnlyDictionary<string, RecipeInput> InputsOrEmpty => Inputs ?? new Dictionary<string, RecipeInput>();
    public IReadOnlyList<RecipePackage> PackagesOrEmpty => Packages ?? [];
    public IReadOnlyList<RecipeTexture> TexturesOrEmpty => Textures ?? [];
}

/// <param name="Path">Relative to the workspace root (the folder holding work\), inside it</param>
public sealed record RecipeInput(string Path, string Sha256);

/// <param name="StockSha256">The stock chunk the values were made on; a game update is a refusal, never a silent rebuild</param>
public sealed record RecipePackage(string Package, string StockSha256, IReadOnlyList<RecipeValue> Values);

/// <summary>One edit, as an edit set's manifest stores it: the export, its name, the path, the stock value and the new one.</summary>
/// <param name="Export">Slot export (layout values) or section/animation export (animation keys, range bounds)</param>
/// <param name="Slot">That export's name, checked against the package</param>
/// <param name="Path">e.g. LayoutData.Offsets.Left, anim:&lt;channel&gt;#&lt;key&gt;[@frame|@interp], range@&lt;frame&gt;</param>
/// <param name="Stock">The value stock stores (or the engine default when <paramref name="Inserted"/>), checked against the package</param>
/// <param name="Inserted">True when stock doesn't store the value and the edit adds it as a new tag; checked, not chosen</param>
public sealed record RecipeValue(int Export, string Slot, string Path, double Stock, double Value, bool Inserted = false);

/// <param name="Input">The image's input id (Mode Image); none for White</param>
/// <param name="Source">The texture to clone when the widget's own can't be (e.g. ETC2); none = the widget's texture</param>
public sealed record RecipeTexture(string Package, string Widget, string Texture, TextureEditMode Mode, string NewName, TextureEditScope Scope,
    string? Input = null, string? Source = null, bool MarginsZero = false);

/// <param name="Chunks">Package path → sha256 of its chunk; when given, exactly the recipe's outputs</param>
/// <param name="Files">Device file name → sha256 (checked when the recipe is built)</param>
public sealed record RecipeExpect(IReadOnlyDictionary<string, string>? Chunks = null, IReadOnlyDictionary<string, string>? Files = null);

/// <summary>Reads a recipe strictly (unknown or duplicate fields, missing ones, comments and numbers in strings are refusals) and checks its data.</summary>
public static partial class RecipeReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static (Recipe? Recipe, IReadOnlyList<string> Refusals) Read(string json)
    {
        Recipe? recipe;
        try { recipe = JsonSerializer.Deserialize<Recipe>(json, Options); }
        catch (JsonException e) { return (null, [$"not a valid recipe: {e.Message}"]); }
        if (recipe == null) return (null, ["not a valid recipe: empty"]);
        var refusals = Validate(recipe);
        return (refusals.Count == 0 ? recipe : null, refusals);
    }

    public static string Write(Recipe recipe) => JsonSerializer.Serialize(recipe, WriteOptions);

    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Sha256Regex();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,47}$")] private static partial Regex NameRegex();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_]{0,62}_P$")] private static partial Regex ContainerRegex();

    public static bool IsSha256(string? s) => s != null && Sha256Regex().IsMatch(s);

    /// <summary>The data checks, without the game: names, hashes, paths, duplicates, values stored exactly.</summary>
    public static List<string> Validate(Recipe r)
    {
        var refusals = new List<string>();
        void Refuse(string why) => refusals.Add(why);

        if (r.Version != Recipe.CurrentVersion) Refuse($"\"Recipe\" is {r.Version}; this umgmod reads version {Recipe.CurrentVersion}");
        if (!NameRegex().IsMatch(r.Name)) Refuse($"Name \"{r.Name}\": letters, digits, '_', '-', '.', up to 48, starting with a letter or digit");
        if (!ContainerRegex().IsMatch(r.Container)) Refuse($"Container \"{r.Container}\": letters, digits and '_', ending in _P (e.g. pc9999_mobile_and_P)");

        foreach (var (id, input) in r.InputsOrEmpty)
        {
            if (!NameRegex().IsMatch(id)) Refuse($"input id \"{id}\": letters, digits, '_', '-', '.'");
            if (PathRefusal(input.Path) is { } bad) Refuse($"input {id}: {bad}");
            if (!IsSha256(input.Sha256)) Refuse($"input {id}: Sha256 must be 64 lower-case hex digits");
        }

        if (r.PackagesOrEmpty.Count == 0 && r.TexturesOrEmpty.Count == 0) Refuse("the recipe has no Packages and no Textures");
        foreach (var dup in r.PackagesOrEmpty.GroupBy(p => p.Package, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Refuse($"{dup.Key} is listed {dup.Count()} times; put all its values in one entry");
        foreach (var p in r.PackagesOrEmpty)
        {
            var head = GamePaths.ShortName(p.Package);
            if (!p.Package.StartsWith("/Game/", StringComparison.Ordinal)) Refuse($"package \"{p.Package}\" must start with /Game/");
            if (!IsSha256(p.StockSha256)) Refuse($"{head}: StockSha256 must be 64 lower-case hex digits");
            if (p.Values.Count == 0) Refuse($"{head}: no Values");
            foreach (var dup in p.Values.GroupBy(v => (v.Export, v.Path)).Where(g => g.Count() > 1))
                Refuse($"{head}: export {dup.Key.Export} {dup.Key.Path} is set {dup.Count()} times");
            foreach (var v in p.Values)
            {
                var what = $"{head}: {v.Slot}.{v.Path}";
                if (v.Export < 0) Refuse($"{what}: Export {v.Export}");
                if (string.IsNullOrWhiteSpace(v.Slot) || string.IsNullOrWhiteSpace(v.Path)) Refuse($"{head}: export {v.Export}: Slot and Path are required");
                if (!double.IsFinite(v.Stock) || !double.IsFinite(v.Value)) { Refuse($"{what}: Stock and Value must be finite numbers"); continue; }
                // what gets stored is a float32 (or an integer that fits one): the recipe must say exactly that, not something it rounds to
                if ((double) (float) v.Value != v.Value) Refuse($"{what}: {v.Value:R} isn't stored exactly; write the float32 value {(double) (float) v.Value:R}");
                if ((float) v.Value == (float) v.Stock) Refuse($"{what}: Value equals Stock ({v.Stock:R}); leave the entry out");
            }
        }

        var inputs = r.InputsOrEmpty;
        foreach (var dup in r.TexturesOrEmpty.GroupBy(t => (t.Package.ToLowerInvariant(), t.Widget)).Where(g => g.Count() > 1))
            Refuse($"texture {GamePaths.ShortName(dup.First().Package)}/{dup.Key.Widget} is replaced {dup.Count()} times");
        foreach (var (t, n) in r.TexturesOrEmpty.Select((t, i) => (t, i + 1)))
        {
            var what = $"texture {n} ({GamePaths.ShortName(t.Package)}/{t.Widget})";
            if (!t.Package.StartsWith("/Game/", StringComparison.Ordinal) || !t.Texture.StartsWith("/Game/", StringComparison.Ordinal)) Refuse($"{what}: Package and Texture must start with /Game/");
            if (t.Source is { } s && !s.StartsWith("/Game/", StringComparison.Ordinal)) Refuse($"{what}: Source must start with /Game/");
            if (string.IsNullOrWhiteSpace(t.Widget) || string.IsNullOrWhiteSpace(t.NewName)) Refuse($"{what}: Widget and NewName are required");
            if (t.Mode == TextureEditMode.Image && (t.Input == null || !inputs.ContainsKey(t.Input))) Refuse($"{what}: Mode Image needs an Input that is listed in Inputs");
            if (t.Mode == TextureEditMode.White && t.Input != null) Refuse($"{what}: Mode White takes no Input");
        }
        var used = r.TexturesOrEmpty.Select(t => t.Input).OfType<string>().ToHashSet();
        foreach (var id in inputs.Keys.Where(id => !used.Contains(id))) Refuse($"input {id} is listed but not used");

        if (r.Expect is { } e)
        {
            foreach (var (package, sha) in e.Chunks ?? new Dictionary<string, string>())
                if (!package.StartsWith("/Game/", StringComparison.Ordinal) || !IsSha256(sha)) Refuse($"Expect.Chunks \"{package}\": a /Game/ package path and 64 lower-case hex digits");
            foreach (var (file, sha) in e.Files ?? new Dictionary<string, string>())
                if (file.IndexOfAny(['/', '\\', ':']) >= 0 || !file.StartsWith(r.Container + ".", StringComparison.Ordinal) && !file.EndsWith(".pak", StringComparison.Ordinal) || !IsSha256(sha))
                    Refuse($"Expect.Files \"{file}\": a device file name of this mod and 64 lower-case hex digits");
        }
        return refusals;
    }

    /// <summary>Why an input path isn't a plain relative path that stays inside the workspace, or null.</summary>
    public static string? PathRefusal(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Path is empty";
        if (System.IO.Path.IsPathRooted(path) || path.Contains(':')) return $"\"{path}\" must be relative to the workspace root";
        var parts = path.Split('/', '\\');
        if (parts.Any(p => p is "" or "." or "..")) return $"\"{path}\": no empty, '.' or '..' parts";
        return null;
    }
}
