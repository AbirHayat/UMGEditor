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
/// <param name="Clones">New texture packages made directly from one stock texture each (canaries C13/C14): a solid colour at
/// the stock size, or an image input with the mip chain halved down to the stock mip count</param>
/// <param name="Repoints">Same-length soft path renames in stock packages (canaries C13/C14), with stored floats changed alongside</param>
/// <param name="LoosePaks">Pak-only legacy paks whose files replace stock loose files (canary C17a/C17b: a font)</param>
/// <param name="Expect">Pins: what the recipe must produce, by sha256</param>
public sealed record Recipe(
    [property: JsonPropertyName("Recipe")] int Version,
    string Name,
    string Container,
    IReadOnlyDictionary<string, RecipeInput>? Inputs = null,
    IReadOnlyList<RecipePackage>? Packages = null,
    IReadOnlyList<RecipeTexture>? Textures = null,
    IReadOnlyList<RecipeClone>? Clones = null,
    IReadOnlyList<RecipeRepoint>? Repoints = null,
    IReadOnlyList<RecipeLoosePak>? LoosePaks = null,
    RecipeExpect? Expect = null)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// Computed members of the recipe records, as extension properties so the serializer never sees them: a recipe can't
/// carry a field under one of these names (the reader refuses it as unknown) and the writer never emits one. As record
/// properties they were written by recipe-from-set and silently ignored on reading.
/// </summary>
public static class RecipeSections
{
    extension(Recipe r)
    {
        public IReadOnlyDictionary<string, RecipeInput> InputsOrEmpty => r.Inputs ?? new Dictionary<string, RecipeInput>();
        public IReadOnlyList<RecipePackage> PackagesOrEmpty => r.Packages ?? [];
        public IReadOnlyList<RecipeTexture> TexturesOrEmpty => r.Textures ?? [];
        public IReadOnlyList<RecipeClone> ClonesOrEmpty => r.Clones ?? [];
        public IReadOnlyList<RecipeRepoint> RepointsOrEmpty => r.Repoints ?? [];
        public IReadOnlyList<RecipeLoosePak> LoosePaksOrEmpty => r.LoosePaks ?? [];
    }

    extension(RecipeClone c)
    {
        public string NewPath => c.From[..(c.From.LastIndexOf('/') + 1)] + c.NewName;
    }

    extension(RecipeRepoint p)
    {
        public IReadOnlyList<RecipeRepointValue> ValuesOrEmpty => p.Values ?? [];
    }

    extension(RecipePackage p)
    {
        public IReadOnlyList<RecipeLayer> LayersOrEmpty => p.Layers ?? [];
    }

    extension(RenameLayer l)
    {
        public IReadOnlyList<RecipeRename> RenamesOrEmpty => l.Renames ?? [];
        public IReadOnlyList<RecipeEnumSwap> EnumSwapsOrEmpty => l.EnumSwaps ?? [];
    }

    extension(RecipeLayer l)
    {
        /// <summary>The layer's "Op" as written in the recipe</summary>
        public string Op => l switch
        {
            RenameLayer => "Rename", RemoveTagLayer => "RemoveTag", ClassSwapLayer => "ClassSwap", ColorRowsLayer => "ColorRows",
            AppendNamesLayer => "AppendNames", EnumInsertLayer => "EnumInsert", NameTagLayer => "NameTag", FontFaceLayer => "FontFace", _ => l.GetType().Name,
        };
    }
}

/// <param name="Path">Relative to the workspace root (the folder holding work\), inside it</param>
public sealed record RecipeInput(string Path, string Sha256);

/// <param name="StockSha256">The stock chunk the values were made on; a game update is a refusal, never a silent rebuild</param>
/// <param name="Values">Layer 1 (the edit set's values, on stock); may be empty when Layers follow</param>
/// <param name="Layers">Further layers, in order, each on the one before and each verified by its own tool; then the
/// union check against stock (<see cref="Patching.LayerUnion"/>): no layer may change what an earlier one changed</param>
public sealed record RecipePackage(string Package, string StockSha256, IReadOnlyList<RecipeValue> Values, IReadOnlyList<RecipeLayer>? Layers = null);

/// <summary>One layer of a package: an existing tool with its own checks. JSON: an object whose first field is "Op".</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "Op")]
[JsonDerivedType(typeof(RenameLayer), "Rename")]
[JsonDerivedType(typeof(RemoveTagLayer), "RemoveTag")]
[JsonDerivedType(typeof(ClassSwapLayer), "ClassSwap")]
[JsonDerivedType(typeof(ColorRowsLayer), "ColorRows")]
[JsonDerivedType(typeof(AppendNamesLayer), "AppendNames")]
[JsonDerivedType(typeof(EnumInsertLayer), "EnumInsert")]
[JsonDerivedType(typeof(NameTagLayer), "NameTag")]
[JsonDerivedType(typeof(FontFaceLayer), "FontFace")]
public abstract record RecipeLayer;

/// <summary>NameRenamePatch: same-length name-map renames (soft paths) and enum values swapped to names already in the map (EnumValueSwap).</summary>
public sealed record RenameLayer(IReadOnlyList<RecipeRename>? Renames = null, IReadOnlyList<RecipeEnumSwap>? EnumSwaps = null) : RecipeLayer;

/// <param name="From">A soft path as the name map stores it; To must be as long, and its package made by the recipe or in the game</param>
public sealed record RecipeRename(string From, string To);

/// <param name="Property">The export's top-level enum property, e.g. color</param>
public sealed record RecipeEnumSwap(string Export, string Property, string From, string To);

/// <summary>TagRemover: a top-level tag back to its class default; Stock is the value it holds.</summary>
public sealed record RemoveTagLayer(string Export, string Tag, string Stock) : RecipeLayer;

/// <summary>ClassSwap: the export's class becomes the one of another stock package (To, e.g. /Game/…/DemoTitleBase).</summary>
public sealed record ClassSwapLayer(string Export, string To) : RecipeLayer;

/// <summary>DataTableColorEdit: rows' Color values, applied through NameRenamePatch.</summary>
public sealed record ColorRowsLayer(IReadOnlyList<RecipeColorRow> Rows) : RecipeLayer;

/// <param name="Stock">"#RRGGBBAA" as stock stores it</param>
/// <param name="Color">"#RRGGBBAA"</param>
public sealed record RecipeColorRow(string Row, string Stock, string Color);

/// <summary>EnumTagInsert.AppendNames: names appended to the name map (old names and hashes kept, every export re-parses identically).</summary>
public sealed record AppendNamesLayer(IReadOnlyList<string> Names) : RecipeLayer;

/// <summary>
/// EnumTagInsert.Insert: an enum tag inserted after a top-level tag. The tag builder is first checked byte for byte
/// against a stock tag of the same name, type, enum and value (<see cref="Anchor"/>).
/// </summary>
/// <param name="Type">ByteProperty (TEnumAsByte) or EnumProperty</param>
public sealed record EnumInsertLayer(string Export, string After, string Tag, string Type, string Enum, string Value, RecipeAnchor Anchor) : RecipeLayer;

/// <param name="Export">An export of that stock package that stores the same tag</param>
public sealed record RecipeAnchor(string Package, string Export);

/// <summary>
/// NameTagEdit: a NameProperty member of a stored struct (format 1: <c>_font</c>, an FSlateFontInfo) set to
/// <see cref="Value"/>. <see cref="Stock"/> absent = stock doesn't store it, so the member is inserted in declaration order
/// (its tag builder first reproduces <see cref="Anchor"/>'s stock tag, required then); otherwise the stored name is
/// swapped in place. Either way the stock value is checked.
/// </summary>
public sealed record NameTagLayer(string Export, string Struct, string Member, string Value, string? Stock = null, RecipeNameAnchor? Anchor = null) : RecipeLayer;

/// <param name="Value">The name that stock export stores in the same member</param>
public sealed record RecipeNameAnchor(string Package, string Export, string Value);

/// <summary>FontFaceRepoint: a CompositeFont entry (e.g. pes's Latin HeadlineBoldSub) from one font face to another the font already imports.</summary>
/// <param name="Entry">The entry's flattened path, e.g. CompositeFont.SubTypefaces[9].Typeface.Fonts[4]</param>
/// <param name="Name">The entry's typeface name, e.g. HeadlineBoldSub</param>
public sealed record FontFaceLayer(string Entry, string Name, string From, string To) : RecipeLayer;

/// <summary>A pak-only legacy pak (v11, index encrypted as stock's) shipped next to the mod's container.</summary>
/// <param name="Pak">Its file name, e.g. pc9998_mobile_and_P.pak</param>
/// <param name="MountPoint">e.g. ../../../PesMobile/Content/Platforms/_Android/Assets/ui/Font/</param>
/// <param name="Files">Each replaces the stock loose file of the same name under the mount point</param>
public sealed record RecipeLoosePak(string Pak, string MountPoint, IReadOnlyList<RecipePakFile> Files);

/// <param name="Path">A file name in the mount point's folder</param>
/// <param name="Input">The input whose bytes it holds</param>
public sealed record RecipePakFile(string Path, string Input);

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

/// <summary>A new texture package next to <paramref name="From"/> (same folder), named <paramref name="NewName"/> (as long as From's short name).</summary>
/// <param name="From">The stock B8G8R8A8 texture package to clone</param>
/// <param name="StockSha256">From's stock chunk</param>
/// <param name="Solid">"#RRGGBB": every pixel of every mip, at the stock size (exactly one of Solid, Input and White)</param>
/// <param name="Input">An image input: mip 0 at the image's size, each further mip half the one before, as many mips as stock.
/// A .png, or raw B, G, R, A bytes in a .bgra file with <paramref name="Width"/> and <paramref name="Height"/></param>
/// <param name="White">The stock pixels with RGB white and the stock alpha kept, at the stock size</param>
public sealed record RecipeClone(string From, string StockSha256, string NewName, string? Solid = null, string? Input = null,
    int? Width = null, int? Height = null, bool White = false);

/// <summary>One stock package whose soft path <paramref name="From"/> becomes <paramref name="To"/> (a same-length name-map rename).</summary>
/// <param name="From">The name-map string, e.g. "/Game/…/Tex720/DemoTitlePlate.DemoTitlePlate"</param>
/// <param name="To">The new one; its package must be made by the recipe or be in the game</param>
/// <param name="Values">Stored floats changed in the same patch (e.g. a 9-slice margin to 0), each with its stock value</param>
public sealed record RecipeRepoint(string Package, string StockSha256, string From, string To, IReadOnlyList<RecipeRepointValue>? Values = null);

/// <param name="Export">The export's name (exactly one in the package), e.g. Img_Plate</param>
/// <param name="Path">The stored value's path, e.g. Margin.Left</param>
public sealed record RecipeRepointValue(string Export, string Path, double Stock, double Value);

/// <param name="Chunks">Package path → sha256 of its chunk; when given, exactly the recipe's outputs</param>
/// <param name="Files">Device file name → sha256 (checked when the recipe is built)</param>
public sealed record RecipeExpect(IReadOnlyDictionary<string, string>? Chunks = null, IReadOnlyDictionary<string, string>? Files = null);

/// <summary>Reads a recipe strictly (unknown or duplicate fields, missing ones, comments and numbers in strings are refusals) and checks its data.</summary>
public static partial class RecipeReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        // a get-only property would be "mapped" yet ignored on reading: the records have none (see RecipeSections)
        IgnoreReadOnlyProperties = true,
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
        IgnoreReadOnlyProperties = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static (Recipe? Recipe, IReadOnlyList<string> Refusals) Read(string json)
    {
        Recipe? recipe;
        try { recipe = JsonSerializer.Deserialize<Recipe>(json, Options); }
        catch (Exception e) when (e is JsonException or NotSupportedException) { return (null, [$"not a valid recipe: {e.Message}"]); }
        if (recipe == null) return (null, ["not a valid recipe: empty"]);
        var refusals = Validate(recipe);
        return (refusals.Count == 0 ? recipe : null, refusals);
    }

    public static string Write(Recipe recipe) => JsonSerializer.Serialize(recipe, WriteOptions);

    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Sha256Regex();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,47}$")] private static partial Regex NameRegex();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_]{0,62}_P$")] private static partial Regex ContainerRegex();
    [GeneratedRegex("^#[0-9A-F]{6}$")] private static partial Regex SolidRegex();
    [GeneratedRegex("^[A-Za-z0-9_]+$")] private static partial Regex ShortNameRegex();

    /// <summary>"#RRGGBB" as bytes.</summary>
    public static (byte R, byte G, byte B) ParseSolid(string solid) =>
        (Convert.ToByte(solid[1..3], 16), Convert.ToByte(solid[3..5], 16), Convert.ToByte(solid[5..7], 16));

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

        if (r.PackagesOrEmpty.Count == 0 && r.TexturesOrEmpty.Count == 0 && r.ClonesOrEmpty.Count == 0 && r.RepointsOrEmpty.Count == 0 && r.LoosePaksOrEmpty.Count == 0)
            Refuse("the recipe has no Packages, Textures, Clones, Repoints or LoosePaks");
        foreach (var dup in r.PackagesOrEmpty.GroupBy(p => p.Package, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Refuse($"{dup.Key} is listed {dup.Count()} times; put all its values in one entry");
        foreach (var p in r.PackagesOrEmpty)
        {
            var head = GamePaths.ShortName(p.Package);
            if (!p.Package.StartsWith("/Game/", StringComparison.Ordinal)) Refuse($"package \"{p.Package}\" must start with /Game/");
            if (!IsSha256(p.StockSha256)) Refuse($"{head}: StockSha256 must be 64 lower-case hex digits");
            if (p.Values.Count == 0 && p.LayersOrEmpty.Count == 0) Refuse($"{head}: no Values and no Layers");
            foreach (var (layer, n) in p.LayersOrEmpty.Select((l, i) => (l, i + (p.Values.Count > 0 ? 2 : 1))))
                foreach (var why in LayerRefusals(layer))
                    Refuse($"{head}: layer {n} ({layer.Op}): {why}");
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
        foreach (var dup in r.ClonesOrEmpty.GroupBy(c => c.NewPath, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Refuse($"clone {dup.Key} is made {dup.Count()} times");
        foreach (var (c, n) in r.ClonesOrEmpty.Select((c, i) => (c, i + 1)))
        {
            var what = $"clone {n} ({c.NewName})";
            if (!c.From.StartsWith("/Game/", StringComparison.Ordinal) || c.From.Contains('.')) Refuse($"{what}: From must be a /Game/ package path (no object name)");
            if (!IsSha256(c.StockSha256)) Refuse($"{what}: StockSha256 must be 64 lower-case hex digits");
            if (!ShortNameRegex().IsMatch(c.NewName)) Refuse($"{what}: NewName: letters, digits and '_'");
            if ((c.Solid != null ? 1 : 0) + (c.Input != null ? 1 : 0) + (c.White ? 1 : 0) != 1) Refuse($"{what}: give exactly one of Solid, Input and White");
            if (c.Solid != null && !SolidRegex().IsMatch(c.Solid)) Refuse($"{what}: Solid \"{c.Solid}\" must be #RRGGBB (upper-case hex)");
            if (c.Input != null && !inputs.ContainsKey(c.Input)) Refuse($"{what}: Input {c.Input} isn't listed in Inputs");
            var raw = c.Input != null && inputs.TryGetValue(c.Input, out var image) && image.Path.EndsWith(".bgra", StringComparison.OrdinalIgnoreCase);
            if (raw && (c.Width is not > 0 || c.Height is not > 0)) Refuse($"{what}: a .bgra input needs Width and Height");
            if (!raw && (c.Width != null || c.Height != null)) Refuse($"{what}: Width and Height are only for a .bgra input (a PNG has its own)");
        }

        foreach (var dup in r.RepointsOrEmpty.GroupBy(p => p.Package, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Refuse($"{dup.Key} is re-pointed in {dup.Count()} entries; one per package");
        foreach (var p in r.RepointsOrEmpty)
        {
            var head = GamePaths.ShortName(p.Package);
            if (!p.Package.StartsWith("/Game/", StringComparison.Ordinal)) Refuse($"re-point \"{p.Package}\" must start with /Game/");
            if (r.PackagesOrEmpty.Any(v => v.Package.Equals(p.Package, StringComparison.OrdinalIgnoreCase)))
                Refuse($"{head} is in Packages and Repoints; recipe format 1 doesn't layer a re-point on value edits (use Textures)");
            if (!IsSha256(p.StockSha256)) Refuse($"{head}: StockSha256 must be 64 lower-case hex digits");
            if (!p.From.StartsWith("/Game/", StringComparison.Ordinal) || !p.To.StartsWith("/Game/", StringComparison.Ordinal) || !p.From.Contains('.') || !p.To.Contains('.'))
                Refuse($"{head}: From and To must be /Game/ object paths (package.object)");
            if (p.From == p.To) Refuse($"{head}: From equals To");
            if (p.From.Length != p.To.Length) Refuse($"{head}: To must be as long as From ({p.From.Length} characters, a same-length rename)");
            foreach (var dup in p.ValuesOrEmpty.GroupBy(v => (v.Export, v.Path)).Where(g => g.Count() > 1))
                Refuse($"{head}: {dup.Key.Export}.{dup.Key.Path} is set {dup.Count()} times");
            foreach (var v in p.ValuesOrEmpty)
            {
                var what = $"{head}: {v.Export}.{v.Path}";
                if (string.IsNullOrWhiteSpace(v.Export) || string.IsNullOrWhiteSpace(v.Path)) { Refuse($"{head}: a value needs Export and Path"); continue; }
                if (!double.IsFinite(v.Stock) || !double.IsFinite(v.Value)) { Refuse($"{what}: Stock and Value must be finite numbers"); continue; }
                if ((double) (float) v.Value != v.Value) Refuse($"{what}: {v.Value:R} isn't stored exactly; write the float32 value {(double) (float) v.Value:R}");
                if ((float) v.Value == (float) v.Stock) Refuse($"{what}: Value equals Stock ({v.Stock:R}); leave the entry out");
            }
        }

        foreach (var dup in r.LoosePaksOrEmpty.GroupBy(x => x.Pak, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            Refuse($"pak {dup.Key} is listed {dup.Count()} times");
        foreach (var pak in r.LoosePaksOrEmpty)
        {
            var what = $"pak {pak.Pak}";
            if (!PakRegex().IsMatch(pak.Pak)) Refuse($"{what}: a file name of letters, digits and '_' ending in _P.pak (e.g. pc9998_mobile_and_P.pak)");
            if (pak.Pak.Equals(r.Container + ".pak", StringComparison.OrdinalIgnoreCase)) Refuse($"{what}: that is the container's own .pak stub");
            if (!pak.MountPoint.StartsWith("../../../", StringComparison.Ordinal) || !pak.MountPoint.EndsWith('/') || pak.MountPoint[9..].Split('/').Any(x => x is "." or "..")
                || pak.MountPoint[9..].Contains("//") || pak.MountPoint.Contains('\\'))
                Refuse($"{what}: MountPoint \"{pak.MountPoint}\" must be ../../../<folder>/ with no '.', '..' or empty parts");
            if (pak.Files.Count == 0) Refuse($"{what}: no Files");
            foreach (var f in pak.Files)
            {
                if (f.Path.Length == 0 || f.Path.IndexOfAny(['/', '\\', ':']) >= 0 || f.Path is "." or "..") Refuse($"{what}: file \"{f.Path}\" must be a plain file name (the pak's files sit in the mount point's folder)");
                if (!inputs.ContainsKey(f.Input)) Refuse($"{what}: {f.Path}: Input {f.Input} isn't listed in Inputs");
            }
            foreach (var dup in pak.Files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)) Refuse($"{what}: {dup.Key} is listed {dup.Count()} times");
        }

        var used = r.TexturesOrEmpty.Select(t => t.Input).Concat(r.ClonesOrEmpty.Select(c => c.Input))
            .Concat(r.LoosePaksOrEmpty.SelectMany(p => p.Files.Select(f => (string?) f.Input))).OfType<string>().ToHashSet();
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

    [GeneratedRegex("^#[0-9A-F]{8}$")] private static partial Regex RgbaRegex();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_]{0,62}_P\\.pak$")] private static partial Regex PakRegex();

    /// <summary>"#RRGGBBAA" as bytes.</summary>
    public static (byte R, byte G, byte B, byte A) ParseRgba(string rgba) =>
        (Convert.ToByte(rgba[1..3], 16), Convert.ToByte(rgba[3..5], 16), Convert.ToByte(rgba[5..7], 16), Convert.ToByte(rgba[7..9], 16));

    /// <summary>The data checks of one layer (its tool checks the rest against the package).</summary>
    private static IEnumerable<string> LayerRefusals(RecipeLayer layer)
    {
        static bool Blank(params string?[] s) => s.Any(string.IsNullOrWhiteSpace);
        switch (layer)
        {
            case RenameLayer l:
                if (l.RenamesOrEmpty.Count == 0 && l.EnumSwapsOrEmpty.Count == 0) yield return "no Renames and no EnumSwaps";
                foreach (var x in l.RenamesOrEmpty)
                {
                    if (!x.From.StartsWith("/Game/", StringComparison.Ordinal) || !x.To.StartsWith("/Game/", StringComparison.Ordinal) || !x.From.Contains('.') || !x.To.Contains('.'))
                        yield return $"rename {x.From} → {x.To}: From and To must be /Game/ object paths (package.object)";
                    if (x.From == x.To) yield return $"rename {x.From}: From equals To";
                    if (x.From.Length != x.To.Length) yield return $"rename {x.From} → {x.To}: To must be as long as From ({x.From.Length} characters)";
                }
                foreach (var dup in l.RenamesOrEmpty.GroupBy(x => x.From).Where(g => g.Count() > 1)) yield return $"{dup.Key} is renamed {dup.Count()} times";
                foreach (var x in l.EnumSwapsOrEmpty)
                {
                    if (Blank(x.Export, x.Property, x.From, x.To)) yield return "an enum swap needs Export, Property, From and To";
                    else if (x.From == x.To) yield return $"{x.Export}.{x.Property}: From equals To";
                }
                foreach (var dup in l.EnumSwapsOrEmpty.GroupBy(x => (x.Export, x.Property)).Where(g => g.Count() > 1)) yield return $"{dup.Key.Export}.{dup.Key.Property} is swapped {dup.Count()} times";
                break;
            case RemoveTagLayer l:
                if (Blank(l.Export, l.Tag, l.Stock)) yield return "Export, Tag and Stock are required";
                break;
            case ClassSwapLayer l:
                if (Blank(l.Export)) yield return "Export is required";
                if (!l.To.StartsWith("/Game/", StringComparison.Ordinal) || l.To.Contains('.')) yield return $"To \"{l.To}\" must be a /Game/ package path";
                break;
            case ColorRowsLayer l:
                if (l.Rows.Count == 0) yield return "no Rows";
                foreach (var row in l.Rows)
                {
                    if (Blank(row.Row)) yield return "a row needs its name";
                    if (!RgbaRegex().IsMatch(row.Stock) || !RgbaRegex().IsMatch(row.Color)) yield return $"{row.Row}: Stock and Color must be #RRGGBBAA (upper-case hex)";
                    else if (row.Stock == row.Color) yield return $"{row.Row}: Color equals Stock; leave the row out";
                }
                foreach (var dup in l.Rows.GroupBy(x => x.Row).Where(g => g.Count() > 1)) yield return $"row {dup.Key} is set {dup.Count()} times";
                break;
            case AppendNamesLayer l:
                if (l.Names.Count == 0) yield return "no Names";
                if (l.Names.Any(string.IsNullOrEmpty)) yield return "an empty name";
                foreach (var dup in l.Names.GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1)) yield return $"{dup.Key} is listed {dup.Count()} times";
                break;
            case NameTagLayer l:
                if (Blank(l.Export, l.Struct, l.Member, l.Value)) yield return "Export, Struct, Member and Value are required";
                if (l.Struct != "_font") yield return $"Struct \"{l.Struct}\": recipe format 1 edits _font (FSlateFontInfo) members only";
                if (l.Stock == l.Value) yield return "Value equals Stock";
                if (l.Stock == null && l.Anchor == null) yield return "an inserted member (Stock null) needs an Anchor: a stock export that stores the member";
                if (l.Stock != null && l.Anchor != null) yield return "a swap in place takes no Anchor";
                if (l.Anchor is { } a && (!a.Package.StartsWith("/Game/", StringComparison.Ordinal) || Blank(a.Export, a.Value))) yield return "Anchor: a /Game/ package, its Export and the Value it stores";
                break;
            case FontFaceLayer l:
                if (Blank(l.Entry, l.Name, l.From, l.To)) yield return "Entry, Name, From and To are required";
                else if (l.From == l.To) yield return "From equals To";
                break;
            case EnumInsertLayer l:
                if (Blank(l.Export, l.After, l.Tag, l.Type, l.Enum, l.Value, l.Anchor.Export)) yield return "Export, After, Tag, Type, Enum, Value and Anchor.Export are required";
                if (!l.Anchor.Package.StartsWith("/Game/", StringComparison.Ordinal)) yield return $"Anchor.Package \"{l.Anchor.Package}\" must start with /Game/";
                break;
        }
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
