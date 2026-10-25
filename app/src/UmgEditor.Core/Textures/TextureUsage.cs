using System.Collections.Concurrent;
using System.Text.Json;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Textures;

/// <summary>
/// One stored reference to a texture: an export property of a widget package holding the texture's soft object path.
/// The path is a name-map string: renaming it in place re-points every reference in the package; appending a new path
/// and pointing one property at it (<see cref="Modding.SoftPathPoint"/>) re-points only that one.
/// </summary>
/// <param name="Texture">The texture's package path, e.g. "/Game/…/Tex720/MatchTimePlate_0"</param>
/// <param name="ObjectPath">The path as the parse shows it, e.g. "/Game/…/Tex720/MatchTimePlate_0.MatchTimePlate_0"</param>
/// <param name="Package">The widget package that stores it</param>
/// <param name="Export">The export (e.g. the uiImage widget "Img_Plate")</param>
/// <param name="Property">The stored property, e.g. "texture"</param>
public sealed record TextureRef(string Texture, string ObjectPath, string Package, string Export, string ExportClass, string Property)
{
    /// <summary>The string the Zen name map holds for this path: an FName keeps a trailing "_N" as a number</summary>
    public string NameMapPath => TextureUsage.NameMapForm(ObjectPath);
    public bool IsImageTexture => ExportClass == "uiImage_C" && Property == "texture";
}

/// <summary>
/// Which widgets name which texture (step 3: texture edits). Built once per game fingerprint from the UI index: every
/// widget package whose name map holds a "/Game/…" object path is parsed, and every stored property whose value is the
/// object path of a texture package is recorded. Cached in work/cache.
/// </summary>
public sealed class TextureUsage
{
    private const int FormatVersion = 1;

    public IReadOnlyList<TextureRef> Refs { get; }
    private readonly ILookup<string, TextureRef> _byTexture, _byPackage;

    public TextureUsage(IReadOnlyList<TextureRef> refs)
    {
        Refs = refs;
        _byTexture = refs.ToLookup(r => r.Texture, StringComparer.OrdinalIgnoreCase);
        _byPackage = refs.ToLookup(r => r.Package, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every reference to a texture (package or object path)</summary>
    public IReadOnlyList<TextureRef> UsersOf(string texture) => _byTexture[GamePaths.PackageOf(texture)].ToList();

    /// <summary>Every texture reference stored in a package</summary>
    public IReadOnlyList<TextureRef> In(string package) => _byPackage[package].ToList();

    /// <summary>The texture an export of a package names (its uiImage "texture" first), or null</summary>
    public TextureRef? Of(string package, string export) =>
        _byPackage[package].Where(r => r.Export == export).OrderByDescending(r => r.IsImageTexture).FirstOrDefault();

    /// <summary>"…/X_0.X_0" → "…/X_0.X": the name-map string of an object path (the FName number suffix split off)</summary>
    public static string NameMapForm(string objectPath)
    {
        var dot = objectPath.LastIndexOf('.');
        if (dot < 0) return objectPath;
        var (_, suffix) = Modding.TextureClone.SplitNumber(objectPath[(dot + 1)..]);
        return objectPath[..^suffix.Length];
    }

    public static TextureUsage LoadOrBuild(GameSource source, IReadOnlyList<UiAssetEntry> index, IProgress<(int Done, int Total)>? progress = null)
    {
        Directory.CreateDirectory(source.Config.CacheDir);
        var cachePath = Path.Combine(source.Config.CacheDir, $"texture_usage_v{FormatVersion}_{source.Fingerprint}.json");
        if (File.Exists(cachePath) && JsonSerializer.Deserialize<List<TextureRef>>(File.ReadAllText(cachePath)) is { } cached)
            return new TextureUsage(cached);
        var refs = Build(source, index, progress);
        File.WriteAllText(cachePath, JsonSerializer.Serialize(refs));
        return new TextureUsage(refs);
    }

    public static IReadOnlyList<TextureRef> Build(GameSource source, IReadOnlyList<UiAssetEntry> index, IProgress<(int Done, int Total)>? progress = null)
    {
        var textures = index.Where(e => e.Kind == UiAssetKind.Texture).Select(e => GamePaths.ToPackagePath(e.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var widgets = index.Where(e => e.Kind == UiAssetKind.Widget && e.Inspected).ToList();
        var found = new ConcurrentBag<TextureRef>();
        int done = 0;
        Parallel.ForEach(widgets, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, entry =>
        {
            try
            {
                if (!source.Provider.TrySaveAsset(entry.Path, out var bytes)) return;
                // cheap filter: a texture soft path is a "/Game/…/X.Y" name
                if (!ZenNameMap.Read(bytes).Any(n => n.Text.StartsWith("/Game/", StringComparison.Ordinal) && n.Text.Contains('.')))
                    return;
                var package = GamePaths.ToPackagePath(entry.Path);
                foreach (var export in source.Provider.LoadPackage(entry.Path).GetExports())
                    foreach (var (key, value) in WidgetTreeReader.StoredValues(export))
                        if (value.StartsWith("/Game/", StringComparison.Ordinal) && value.Contains('.') && textures.Contains(GamePaths.PackageOf(value)))
                            found.Add(new TextureRef(GamePaths.PackageOf(value), value, package, export.Name, export.ExportType, key));
            }
            catch (Exception) { /* unreadable packages are listed as errors by the UI index already */ }
            finally
            {
                var n = Interlocked.Increment(ref done);
                if (n % 200 == 0 || n == widgets.Count) progress?.Report((n, widgets.Count));
            }
        });
        return found.OrderBy(r => r.Texture, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Package, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Export, StringComparer.Ordinal).ThenBy(r => r.Property, StringComparer.Ordinal).ToList();
    }
}
