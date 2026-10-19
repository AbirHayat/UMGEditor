using System.Collections.Concurrent;
using System.Text.Json;
using CUE4Parse.UE4.Assets;

namespace UmgEditor.Core;

public enum UiAssetKind { Widget, Texture, Material, Font, Blueprint, Other, Error }

/// <param name="Path">Provider key, e.g. PesMobile/Content/Assets/ui/Data/Widget/.../MatchTime.uasset</param>
/// <param name="Group">Browsing group, e.g. "Widget/TVGraphics", "ui/library", "Android override"</param>
/// <param name="MainClass">Class of the package's main export, or why it couldn't be read</param>
/// <param name="Inspected">False for bulk image sets that are listed without opening the package</param>
public sealed record UiAssetEntry(
    string Path,
    string Name,
    string Folder,
    string Group,
    string Container,
    UiAssetKind Kind,
    string MainClass,
    int ExportCount,
    long Size,
    bool IsPlatformOverride,
    bool IsBulkImageSet,
    bool Inspected);

/// <summary>Every UI package in the stock containers (anything under a /ui/ folder), classified by export classes.</summary>
public static class UiAssetIndex
{
    private const int FormatVersion = 1;

    // 74k player thumbnails and emblems: game data images, not UMG; listed but not opened
    private static readonly string[] BulkImageFolders = ["/ui/Data/Thumbnail/", "/ui/Data/Symbol/"];

    public static bool IsUiPath(string path) =>
        path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && path.Contains("/ui/", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<UiAssetEntry> LoadOrBuild(GameSource source, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(source.Config.CacheDir);
        var cachePath = System.IO.Path.Combine(source.Config.CacheDir, $"ui_index_v{FormatVersion}_{source.Fingerprint}.json");
        if (File.Exists(cachePath))
        {
            var cached = JsonSerializer.Deserialize<List<UiAssetEntry>>(File.ReadAllText(cachePath));
            if (cached != null) return cached;
        }

        var entries = Build(source, progress, ct);
        File.WriteAllText(cachePath, JsonSerializer.Serialize(entries));
        return entries;
    }

    public static IReadOnlyList<UiAssetEntry> Build(GameSource source, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var paths = source.Provider.Files.Keys.Where(IsUiPath).ToList();
        var results = new ConcurrentBag<UiAssetEntry>();
        int done = 0;
        Parallel.ForEach(paths, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, path =>
        {
            results.Add(Describe(source, path));
            var n = Interlocked.Increment(ref done);
            if (n % 200 == 0 || n == paths.Count) progress?.Report((n, paths.Count));
        });
        return results.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static UiAssetEntry Describe(GameSource source, string path)
    {
        var file = source.Provider.Files[path];
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var folder = System.IO.Path.GetDirectoryName(path)!.Replace('\\', '/');
        var isOverride = path.Contains("/Platforms/_Android/", StringComparison.OrdinalIgnoreCase);
        var isBulk = BulkImageFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase));
        var group = GroupOf(path, isOverride);
        var container = source.ContainerOf(path);

        if (isBulk)
            return new UiAssetEntry(path, name, folder, group, container, UiAssetKind.Texture, "(not inspected)", 0, file.Size, isOverride, true, false);

        try
        {
            // header only: export classes are resolved from the export map, properties are not parsed
            if (source.Provider.LoadPackage(file) is not IoPackage pkg)
                return new UiAssetEntry(path, name, folder, group, container, UiAssetKind.Other, "(not an IoStore package)", 0, file.Size, isOverride, false, true);

            var classes = new string[pkg.ExportMap.Length];
            for (int i = 0; i < classes.Length; i++)
                classes[i] = pkg.ResolveObjectIndex(pkg.ExportMap[i].ClassIndex)?.Name.Text ?? "?";
            var (kind, main) = Classify(classes);
            return new UiAssetEntry(path, name, folder, group, container, kind, main, classes.Length, file.Size, isOverride, false, true);
        }
        catch (Exception e)
        {
            return new UiAssetEntry(path, name, folder, group, container, UiAssetKind.Error, e.GetType().Name + ": " + e.Message, 0, file.Size, isOverride, false, true);
        }
    }

    private static (UiAssetKind, string) Classify(string[] classes)
    {
        if (classes.Contains("WidgetBlueprintGeneratedClass")) return (UiAssetKind.Widget, "WidgetBlueprintGeneratedClass");
        if (classes.FirstOrDefault(c => c.StartsWith("Texture")) is { } tex) return (UiAssetKind.Texture, tex);
        if (classes.FirstOrDefault(c => c is "Font" or "FontFace") is { } font) return (UiAssetKind.Font, font);
        if (classes.FirstOrDefault(c => c.StartsWith("Material")) is { } mat) return (UiAssetKind.Material, mat);
        if (classes.Contains("BlueprintGeneratedClass")) return (UiAssetKind.Blueprint, "BlueprintGeneratedClass");
        return (UiAssetKind.Other, classes.FirstOrDefault() ?? "(no exports)");
    }

    /// <summary>Widget/&lt;Category&gt; for ui/Data/Widget, otherwise the first folder below ui/.</summary>
    private static string GroupOf(string path, bool isOverride)
    {
        if (isOverride) return "Android override";
        var i = path.IndexOf("/ui/Data/Widget/", StringComparison.OrdinalIgnoreCase);
        if (i >= 0)
        {
            var rest = path[(i + "/ui/Data/Widget/".Length)..];
            var slash = rest.IndexOf('/');
            return "Widget/" + (slash > 0 ? rest[..slash] : "(root)");
        }
        var u = path.IndexOf("/ui/", StringComparison.OrdinalIgnoreCase);
        var after = path[(u + 4)..];
        var parts = after.Split('/');
        var prefix = path.Contains("/Parkour/", StringComparison.OrdinalIgnoreCase) ? "Parkour/" : "ui/";
        if (parts.Length >= 3 && parts[0].Equals("Data", StringComparison.OrdinalIgnoreCase)) return prefix + "Data/" + parts[1];
        return prefix + (parts.Length > 1 ? parts[0] : "(root)");
    }
}
