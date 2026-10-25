using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CUE4Parse.UE4.Assets.Exports.Texture;
using UmgEditor.Core.Patching;
using UmgEditor.Core.Textures;

namespace UmgEditor.Core.Modding;

public enum TextureEditMode { Image, White }

/// <summary>
/// What a texture edit re-points: only the picked widget (other widgets naming the texture keep it, even in the same
/// package), every widget of the picked widget's package that names it, or every package naming the texture.
/// </summary>
public enum TextureEditScope { Widget, AllUsers, Package }

/// <summary>
/// One texture replacement in a project (step 3). The picked widget (<see cref="Package"/>/<see cref="Widget"/>) names
/// <see cref="Texture"/>; its pixels go into new packages <see cref="NewName"/> cloned from <see cref="Source"/> (default:
/// the texture itself; another B8G8R8A8 texture when that one can't be cloned), in every theme folder the source has,
/// and the storing package(s) are re-pointed to the clone.
/// </summary>
/// <param name="Image">The imported PNG, relative to the project folder (_art\&lt;sha256&gt;.png); null for White</param>
/// <param name="MarginsZero">Also set the stored Margin of every uiImage that follows the re-point to 0 (a real image isn't 9-sliced)</param>
public sealed record TextureEdit(
    string Package, string Widget, string Texture, TextureEditMode Mode, string NewName,
    TextureEditScope Scope = TextureEditScope.Widget, string? Image = null, string? ImageSha256 = null,
    string? Source = null, bool MarginsZero = false)
{
    public string SourceTexture => Source ?? Texture;
}

/// <summary>A project's texture edits on disk: textures.json plus the imported images under _art (both under work\edits).</summary>
public static class TextureEditStore
{
    public const string FileName = "textures.json", ArtFolder = "_art";
    // 2: scope Widget means only the picked widget; version 1's "Widget" re-pointed the whole package (now Package)
    private const int Version = 2;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private sealed record FileContent(int Version, IReadOnlyList<TextureEdit> Edits);

    public static IReadOnlyList<TextureEdit> Load(string projectFolder)
    {
        var path = Path.Combine(projectFolder, FileName);
        if (!File.Exists(path)) return [];
        var content = JsonSerializer.Deserialize<FileContent>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"{path} is empty");
        return content.Version switch
        {
            1 => content.Edits.Select(e => e.Scope == TextureEditScope.Widget ? e with { Scope = TextureEditScope.Package } : e).ToList(),
            Version => content.Edits,
            _ => throw new InvalidDataException($"{path}: version {content.Version}, this build reads 1 and {Version}"),
        };
    }

    /// <summary>Writes the list (an empty list removes the file).</summary>
    public static void Save(WriteGuard guard, string projectFolder, IReadOnlyList<TextureEdit> edits)
    {
        var path = Path.Combine(projectFolder, FileName);
        if (edits.Count == 0) { if (File.Exists(path)) guard.Delete(path); return; }
        guard.CreateDirectory(projectFolder);
        guard.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new FileContent(Version, edits), Json));
        guard.Move(path + ".tmp", path);
    }

    /// <summary>Copies a PNG into the project (named by its sha256) after checking that it decodes.</summary>
    public static (string? Relative, string? Sha256, DecodedImage? Image, string? Why) Import(WriteGuard guard, string projectFolder, string pngPath)
    {
        if (!File.Exists(pngPath)) return (null, null, null, $"{pngPath} doesn't exist");
        var bytes = File.ReadAllBytes(pngPath);
        var (image, why) = PngReader.Read(bytes);
        if (image == null) return (null, null, null, why);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var relative = Path.Combine(ArtFolder, sha + ".png");
        var target = Path.Combine(projectFolder, relative);
        if (!File.Exists(target))
        {
            guard.CreateDirectory(Path.GetDirectoryName(target)!);
            guard.WriteAllBytes(target, bytes);
        }
        return (relative, sha, image, null);
    }

    /// <summary>The edit's image, refused when it's missing or no longer the file that was imported.</summary>
    public static (DecodedImage? Image, string? Why) ReadImage(string projectFolder, TextureEdit edit)
    {
        if (edit.Image == null) return (null, "no image");
        var path = Path.Combine(projectFolder, edit.Image);
        if (!File.Exists(path)) return (null, $"the imported image {edit.Image} is missing from the project");
        var bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (edit.ImageSha256 != null && sha != edit.ImageSha256) return (null, $"{edit.Image} changed since it was imported (sha256 {sha[..12]}…, expected {edit.ImageSha256[..12]}…)");
        return PngReader.Read(bytes);
    }
}

/// <summary>What one texture edit does, for the inspector, umgmod and the export's instructions.</summary>
/// <param name="Following">Other widgets that change with the picked one (in the storing packages)</param>
/// <param name="Pointed">Storing packages re-pointed by appending the new path and pointing the widgets at it
/// (<see cref="SoftPathPoint"/>); the others rename the stock path in place (<see cref="SoftPathRepoint"/>)</param>
public sealed record TextureEditInfo(
    TextureEdit Edit, string From, string To, IReadOnlyList<string> Storing, IReadOnlyList<TextureRef> Following,
    IReadOnlyList<string> ClonePaths, int Width, int Height, int StockWidth, int StockHeight, IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Pointed);

/// <summary>Every package a project's texture edits produce, each verified, plus what the mounted mod must decode to.</summary>
public sealed class TexturePlan
{
    public List<TextureEditInfo> Items { get; } = [];
    /// <summary>Re-pointed packages (final layer), at most one per package</summary>
    public List<ModPackage> Packages { get; } = [];
    public List<ModPackage> Clones { get; } = [];
    /// <summary>Each clone's package path and the mip 0 it must decode to</summary>
    public List<(string PackagePath, byte[] Mip0)> Expected { get; } = [];
    public List<string> Refusals { get; } = [];
    public List<string> Log { get; } = [];
    public bool Ok => Refusals.Count == 0;
    public IEnumerable<ModPackage> All => Packages.Concat(Clones);
}

/// <summary>
/// Plans texture edits (step 3): clones (<see cref="TextureClone.BuildResized"/> for an image, <see cref="TextureClone.Build"/>
/// for White) in every theme folder of the source, then per storing package one re-point layer per edit
/// (<see cref="SoftPathRepoint"/>, on top of the package's saved edit-set chunk when it has one), finished by
/// <see cref="LayerUnion.Check"/> against stock. Every tool refuses on its own checks; the refusals are collected.
/// </summary>
public static class TextureEdits
{
    public static readonly string[] ThemeFolders = ["Tex720", "0164F/Tex720"];

    /// <summary>The source texture and its theme copy: ".../Tex720/X" and ".../0164F/Tex720/X" when both exist.</summary>
    public static IReadOnlyList<string> ThemeCopies(GameSource source, string texture)
    {
        var copies = new List<string> { texture };
        var at = texture.LastIndexOf("/Tex720/", StringComparison.Ordinal);
        if (at >= 0 && !texture[..at].EndsWith("/0164F", StringComparison.Ordinal))
        {
            var other = texture[..at] + "/0164F" + texture[at..];
            if (Exists(source, other)) copies.Add(other);
        }
        return copies;
    }

    public static bool Exists(GameSource source, string packagePath) => source.ChunkIdHex(source.ResolveFileKey(packagePath)).Length > 0;

    public static UTexture2D? LoadTexture(GameSource source, string texture)
    {
        try { return source.LoadPackage(texture).GetExports().OfType<UTexture2D>().SingleOrDefault(); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Why a texture can't be a clone source (only uncompressed B8G8R8A8 with inline mips can, in every theme folder it
    /// has: a clone is made from each copy, and each copy's names must be renameable, <see cref="TextureClone.NameRefusal"/>),
    /// or null.
    /// </summary>
    public static string? SourceRefusal(GameSource source, string texture)
    {
        if (LoadTexture(source, texture) is not { } t) return $"{GamePaths.ShortName(texture)} isn't a Texture2D package";
        if (t.PlatformData.PixelFormat != "PF_B8G8R8A8") return $"{GamePaths.ShortName(texture)} is {t.PlatformData.PixelFormat}; only PF_B8G8R8A8 textures can be cloned (pick another source)";
        foreach (var copy in ThemeCopies(source, texture).Skip(1))
            if (LoadTexture(source, copy) is not { PlatformData.PixelFormat: "PF_B8G8R8A8" } c)
                return $"{GamePaths.ShortName(texture)}'s theme copy {copy[(copy.LastIndexOf("/0164F/", StringComparison.Ordinal) + 1)..]} is {LoadTexture(source, copy)?.PlatformData.PixelFormat ?? "not a Texture2D"}; only PF_B8G8R8A8 textures can be cloned (pick another source)";
        foreach (var copy in ThemeCopies(source, texture))
            if (TextureClone.NameRefusal(source, copy) is { } names) return $"{names} (pick another source)";
        return null;
    }

    public static int MipCount(GameSource source, string texture) =>
        source.LoadPackage(texture).GetExports().OfType<UTexture2D>().Single().PlatformData.Mips.Length;

    /// <summary>A clone of one stock texture at the image's size, the mip chain halved down to the stock mip count.</summary>
    public static TextureClone.Result CloneImage(GameSource source, string stockPath, string newName, TextureClone.Mip image)
    {
        var chain = new List<TextureClone.Mip> { image };
        for (int mips = MipCount(source, stockPath); chain.Count < mips;) chain.Add(TextureClone.Half(chain[^1]));
        return TextureClone.BuildResized(source, stockPath, newName, chain);
    }

    /// <summary>A same-size clone with every pixel's RGB white and the stock alpha kept; and the mip 0 it decodes to.</summary>
    public static (TextureClone.Result Result, byte[] Mip0) CloneWhite(GameSource source, string stockPath, string newName)
    {
        var stockMips = source.LoadPackage(stockPath).GetExports().OfType<UTexture2D>().Single().PlatformData.Mips;
        static byte[] Whiten(byte[] bgra) { var w = bgra.ToArray(); for (int i = 0; i < w.Length; i += 4) w[i] = w[i + 1] = w[i + 2] = 255; return w; }
        var r = TextureClone.Build(source, stockPath, newName, (w, h) => Whiten(stockMips.Single(m => m.SizeX == w && m.SizeY == h).BulkData!.Data!));
        return (r, Whiten(stockMips[0].BulkData!.Data!));
    }

    /// <summary>The name-map path of a new texture package next to <paramref name="source"/>: "…/Tex720/New_0.New".</summary>
    public static string NewNameMapPath(string source, string newName) => TextureUsage.NameMapForm(NewObjectPath(source, newName));

    /// <summary>The object path of a new texture package next to <paramref name="source"/>: "…/Tex720/New_0.New_0".</summary>
    public static string NewObjectPath(string source, string newName) => $"{source[..(source.LastIndexOf('/') + 1)]}{newName}.{newName}";

    /// <summary>
    /// A same-length, unused name for a clone of <paramref name="source"/> that keeps its FName number suffix:
    /// the short name's base with its end replaced by "U1", "U2", ….
    /// </summary>
    public static string SuggestName(GameSource source, string sourceTexture, IEnumerable<string> taken)
    {
        var shortName = GamePaths.ShortName(sourceTexture);
        var (stem, suffix) = TextureClone.SplitNumber(shortName);
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folder = sourceTexture[..(sourceTexture.LastIndexOf('/') + 1)];
        for (int n = 1; n < 1000; n++)
        {
            var tag = "U" + n;
            if (tag.Length >= stem.Length) break;
            var candidate = stem[..^tag.Length] + tag + suffix;
            if (TextureClone.SplitNumber(candidate).Suffix != suffix || used.Contains(candidate)) continue;
            if (ThemeFolderVariants(folder + candidate).Any(p => Exists(source, p))) continue;
            return candidate;
        }
        throw new InvalidOperationException($"no free name like {shortName}");
    }

    private static IEnumerable<string> ThemeFolderVariants(string texture)
    {
        yield return texture;
        var at = texture.LastIndexOf("/Tex720/", StringComparison.Ordinal);
        if (at >= 0) yield return texture[..at] + "/0164F" + texture[at..];
    }

    /// <summary>
    /// Plans every edit of a project. <paramref name="inputFor"/> gives a package's saved edit-set chunk (null = stock);
    /// <paramref name="embeddedBy"/> lists the packages that show a storing package (for the warnings).
    /// </summary>
    public static TexturePlan Plan(GameSource source, TextureUsage usage, string projectFolder, IReadOnlyList<TextureEdit> edits,
        Func<string, byte[]?>? inputFor = null, Func<string, IReadOnlyCollection<string>>? embeddedBy = null)
    {
        var plan = new TexturePlan();
        var layers = new Dictionary<string, List<byte[]>>(StringComparer.OrdinalIgnoreCase);
        var cloned = new Dictionary<string, (TextureEdit Edit, string Key)>(StringComparer.OrdinalIgnoreCase);
        // (package, stock path, export or "*" for a rename of the whole path) → the edit that re-pointed it
        var repointed = new Dictionary<(string Package, string From, string Export), int>();
        // an earlier edit that already changed this path here: any one for a rename, the same widget's for a point
        int? EarlierEdit(string package, string from, bool rename, IReadOnlyList<TextureRef> here)
        {
            if (repointed.TryGetValue((package, from, "*"), out var whole)) return whole;
            if (rename) return repointed.Where(kv => kv.Key.Package == package && kv.Key.From == from).Select(kv => (int?) kv.Value).FirstOrDefault();
            foreach (var r in here)
                if (repointed.TryGetValue((package, from, r.Export), out var one)) return one;
            return null;
        }
        foreach (var (edit, n) in edits.Select((e, i) => (e, i + 1)))
        {
            var head = $"texture edit {n} ({GamePaths.ShortName(edit.Package)}/{edit.Widget} → {edit.NewName})";
            void Refuse(string why) => plan.Refusals.Add($"{head}: {why}");
            if (GamePaths.IsLibrary(edit.Package)) { Refuse("library widgets are shared by the whole game; edit the widget that uses it"); continue; }
            if (usage.Of(edit.Package, edit.Widget) is not { IsImageTexture: true } picked) { Refuse($"{edit.Widget} in {GamePaths.ShortName(edit.Package)} isn't a uiImage with a stored texture"); continue; }
            if (!picked.Texture.Equals(edit.Texture, StringComparison.OrdinalIgnoreCase)) { Refuse($"{edit.Widget} names {picked.Texture}, the edit was made for {edit.Texture}"); continue; }
            if (SourceRefusal(source, edit.SourceTexture) is { } badSource) { Refuse(badSource); continue; }

            var sourceName = GamePaths.ShortName(edit.SourceTexture);
            if (edit.NewName.Length != sourceName.Length) { Refuse($"\"{edit.NewName}\" must be as long as \"{sourceName}\" ({sourceName.Length} characters)"); continue; }
            var from = picked.NameMapPath;
            var to = NewNameMapPath(edit.SourceTexture, edit.NewName);
            var toObject = NewObjectPath(edit.SourceTexture, edit.NewName);

            // the pixels: the image (any size, halved down the mip chain) or the stock pixels whitened
            var copies = ThemeCopies(source, edit.SourceTexture);
            var sourceTex = LoadTexture(source, edit.SourceTexture)!;
            var shown = LoadTexture(source, edit.Texture) ?? sourceTex; // what the widget shows now
            int width = sourceTex.PlatformData.SizeX, height = sourceTex.PlatformData.SizeY;
            TextureClone.Mip? image = null;
            if (edit.Mode == TextureEditMode.Image)
            {
                var (decoded, why) = TextureEditStore.ReadImage(projectFolder, edit);
                if (decoded == null) { Refuse(why!); continue; }
                image = new TextureClone.Mip(decoded.Width, decoded.Height, decoded.Bgra);
                (width, height) = (decoded.Width, decoded.Height);
            }
            var key = edit.Mode == TextureEditMode.White ? "white:" + edit.SourceTexture : $"image:{edit.ImageSha256}:{edit.SourceTexture}";
            var clonePaths = copies.Select(c => c[..(c.LastIndexOf('/') + 1)] + edit.NewName).ToList();
            if (cloned.TryGetValue(edit.NewName, out var earlier))
            {
                if (earlier.Key != key) { Refuse($"{edit.NewName} is already the name of another replacement (edit {edits.ToList().IndexOf(earlier.Edit) + 1}) with other pixels"); continue; }
            }
            else
            {
                bool ok = true;
                foreach (var copy in copies)
                {
                    var (result, mip0) = image != null ? (CloneImage(source, copy, edit.NewName, image), image.Bgra) : CloneWhite(source, copy, edit.NewName);
                    plan.Log.Add($"{head}: clone {copy[(copy.LastIndexOf("/Widget/", StringComparison.Ordinal) + 1)..]} → {edit.NewName}");
                    plan.Log.AddRange(result.Log.Select(l => "  " + l));
                    if (result.Package == null) { result.Refusals.ToList().ForEach(Refuse); ok = false; break; }
                    plan.Clones.Add(result.Package);
                    plan.Expected.Add((result.Package.PackagePath, mip0));
                }
                if (!ok) continue;
                cloned[edit.NewName] = (edit, key);
            }

            // the re-point: the picked widget, every widget of its package, or every package naming the texture
            var storing = edit.Scope == TextureEditScope.AllUsers
                ? usage.UsersOf(edit.Texture).Where(r => r.NameMapPath == from).Select(r => r.Package).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : [edit.Package];
            var changing = edit.Scope == TextureEditScope.Widget ? [picked] : storing.SelectMany(usage.In).Where(r => r.NameMapPath == from).ToList();
            var following = changing.Where(r => !(r.Package.Equals(edit.Package, StringComparison.OrdinalIgnoreCase) && r.Export == edit.Widget)).ToList();
            var pointed = new List<string>();
            foreach (var package in storing)
            {
                var here = changing.Where(r => r.Package.Equals(package, StringComparison.OrdinalIgnoreCase)).ToList();
                // rename the stock path in place (as C13-C16) when every widget naming it changes and the new path is as long;
                // otherwise append the new path and point only the changing widgets at it
                bool rename = to.Length == from.Length && usage.In(package).Where(r => r.NameMapPath == from).All(here.Contains);
                if (EarlierEdit(package, from, rename, here) is { } by)
                {
                    Refuse($"{GamePaths.ShortName(package)}'s path to {GamePaths.ShortName(edit.Texture)} is already re-pointed by texture edit {by}");
                    continue;
                }
                if (!rename && here.FirstOrDefault(r => !r.IsImageTexture) is { } other)
                {
                    Refuse($"{GamePaths.ShortName(package)}/{other.Export}.{other.Property} names the texture but isn't a uiImage texture: only those are pointed (the new path's length differs from the stock one)");
                    continue;
                }
                if (!layers.TryGetValue(package, out var list))
                {
                    layers[package] = list = [];
                    if (inputFor?.Invoke(package) is { } saved) list.Add(saved);
                }
                var values = new List<(string Export, string Path, double Value)>();
                if (edit.MarginsZero)
                {
                    var input = list.Count > 0 ? list[^1] : source.ReadPackageBytes(package).Bytes;
                    var parsed = source.ParsePackage(source.ResolveFileKey(package), input).ExportsLazy.Select(e => e.Value).ToList();
                    foreach (var r in here.Where(r => r.IsImageTexture))
                    {
                        var stored = WidgetTreeReader.StoredValues(parsed.Single(e => e.Name == r.Export));
                        foreach (var side in new[] { "Left", "Top", "Right", "Bottom" })
                            if (stored.ContainsKey("Margin." + side)) values.Add((r.Export, "Margin." + side, 0.0));
                            else plan.Log.Add($"{head}: {r.Export}.Margin.{side} isn't stored (already the default), left as is");
                    }
                }
                var repoint = rename
                    ? SoftPathRepoint.Build(source, package, from, to, values, list.Count > 0 ? list[^1] : null)
                    : SoftPathPoint.Build(source, package, picked.ObjectPath, toObject, here.Select(r => r.Export).ToList(), values, list.Count > 0 ? list[^1] : null);
                plan.Log.Add($"{head}: {GamePaths.ShortName(package)} {(rename ? "re-pointed (path renamed)" : $"{string.Join(", ", here.Select(r => r.Export))} pointed at the appended path")}{(values.Count > 0 ? $" + {values.Count} margins 0" : "")}");
                plan.Log.AddRange(repoint.Log.Select(l => "  " + l));
                if (repoint.Package == null) { repoint.Refusals.ToList().ForEach(Refuse); continue; }
                list.Add(repoint.Package.Chunk);
                if (rename) repointed[(package, from, "*")] = n;
                else
                {
                    pointed.Add(package);
                    foreach (var r in here) repointed[(package, from, r.Export)] = n;
                }
            }

            var warnings = new List<string>();
            foreach (var f in following)
                warnings.Add($"{GamePaths.ShortName(f.Package)}/{f.Export} names the same texture in the same package: it changes too");
            foreach (var keeps in storing.SelectMany(usage.In).Where(r => r.NameMapPath == from && !changing.Contains(r)))
                warnings.Add($"{GamePaths.ShortName(keeps.Package)}/{keeps.Export} names the same texture in the same package and keeps the stock image (scope This package changes it too)");
            foreach (var package in storing)
                if (embeddedBy?.Invoke(package) is { Count: > 0 } users)
                    warnings.Add($"{GamePaths.ShortName(package)} is shown in {users.Count} package(s): {string.Join(", ", users.Select(GamePaths.ShortName).Order(StringComparer.OrdinalIgnoreCase))}");
            foreach (var other in usage.UsersOf(edit.Texture).Select(r => r.Package).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !storing.Contains(p, StringComparer.OrdinalIgnoreCase)))
                warnings.Add($"{GamePaths.ShortName(other)} also names {GamePaths.ShortName(edit.Texture)} and keeps the stock image");
            if (copies.Count == 1 && ThemeCopies(source, edit.Texture).Count > 1)
                warnings.Add($"{GamePaths.ShortName(edit.SourceTexture)} has no 0164F theme copy: only the Tex720 clone is made");
            if (!edit.MarginsZero && edit.Mode == TextureEditMode.Image && StoredMarginWidgets(source, changing) is { Count: > 0 } sliced)
                warnings.Add($"{string.Join(", ", sliced)} {(sliced.Count == 1 ? "is" : "are")} 9-sliced (stored Margin): the image's borders stay at their own size and only the middle stretches; tick Margins 0 to stretch the whole image");
            if (pointed.Count > 0)
                warnings.Add($"{string.Join(", ", pointed.Select(GamePaths.ShortName))}: the new path is appended to the name map and only the changing widgets point at it (proven on device by canary C18)");
            plan.Items.Add(new TextureEditInfo(edit, from, to, storing, following, clonePaths, width, height, shown.PlatformData.SizeX, shown.PlatformData.SizeY, warnings, pointed));
        }

        foreach (var (package, list) in layers)
        {
            if (list.Count == 0 || list.Count == 1 && inputFor?.Invoke(package) != null) continue;
            var (fileKey, stock) = source.ReadPackageBytes(package);
            var (problems, changes) = LayerUnion.Check(source, fileKey, stock, list);
            if (problems.Count > 0) { plan.Refusals.Add($"{GamePaths.ShortName(package)}: the layers don't add up against stock: {string.Join(" | ", problems.Take(5))}"); continue; }
            plan.Log.Add($"{GamePaths.ShortName(package)}: final re-parse against stock = the union of {list.Count} layer(s) ({changes} values)");
            plan.Packages.Add(new ModPackage(package, fileKey, source.ChunkIdHex(fileKey), list[^1]));
        }
        return plan;
    }

    /// <summary>The uiImage widgets (package/export) among the re-pointed ones that store a non-zero Margin.</summary>
    private static List<string> StoredMarginWidgets(GameSource source, IReadOnlyList<TextureRef> changing)
    {
        var found = new List<string>();
        foreach (var group in changing.Where(r => r.IsImageTexture).GroupBy(r => r.Package, StringComparer.OrdinalIgnoreCase))
        {
            var package = group.Key;
            var exports = source.LoadPackage(package).GetExports().ToList();
            foreach (var r in group)
                if (exports.FirstOrDefault(e => e.Name == r.Export) is { } e && WidgetTreeReader.StoredValues(e)
                        .Any(kv => kv.Key.StartsWith("Margin.", StringComparison.Ordinal) && double.TryParse(kv.Value, System.Globalization.CultureInfo.InvariantCulture, out var v) && v != 0))
                    found.Add($"{GamePaths.ShortName(package)}/{r.Export}");
        }
        return found;
    }

    /// <summary>
    /// B8G8R8A8 textures (inline mips) a clone for <paramref name="current"/> can come from. Any path works: a path of
    /// another length is pointed at instead of renamed (<see cref="SoftPathPoint"/>). Sorted: same folder first, then
    /// sources with the same theme copies (Tex720 + 0164F when the current texture has both), then a path of the same
    /// length (renamed in place, the route proven since C13), then fewer mips, then by path.
    /// </summary>
    public static IReadOnlyList<string> Sources(GameSource source, TextureFormats formats, TextureRef current)
    {
        var length = current.NameMapPath.Length;
        var folder = current.Texture[..current.Texture.LastIndexOf('/')];
        bool themed = ThemeCopies(source, current.Texture).Count > 1;
        return formats.All.Where(f => f.Cloneable && !f.Texture.Contains("/0164F/", StringComparison.Ordinal))
            .Select(f => (f.Texture, f.Mips, Copies: ThemeCopies(source, f.Texture)))
            .Where(s => s.Copies.All(c => formats.Of(c)?.Cloneable == true)) // a clone is made from every theme copy
            .Select(s => (s.Texture, s.Mips, Themed: themed && s.Copies.Count > 1))
            .OrderBy(s => s.Texture.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(s => themed && !s.Themed ? 1 : 0)
            .ThenBy(s => NewNameMapPath(s.Texture, GamePaths.ShortName(s.Texture)).Length == length ? 0 : 1)
            .ThenBy(s => s.Mips).ThenBy(s => s.Texture, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.Texture).ToList();
    }
}
