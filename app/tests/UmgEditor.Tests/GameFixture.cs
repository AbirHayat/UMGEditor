using CUE4Parse.UE4.Assets;
using UmgEditor.Core;
using UmgEditor.Core.Layout;
using UmgEditor.Core.Patching;
using UmgEditor.Core.Textures;

namespace UmgEditor.Tests;

/// <summary>Opens the stock containers once for the whole test run (read-only).</summary>
[TestClass]
public static class GameFixture
{
    public static GameSource Source { get; private set; } = null!;
    public static IReadOnlyList<UiAssetEntry> UiIndex { get; private set; } = [];
    public static LayoutEngine Engine { get; private set; } = null!;

    public const string Tv = "/Game/Assets/ui/Data/Widget/TVGraphics/SwitchAssets_Original/";

    /// <summary>sha256 of every file in the stock Paks folder at the start of the run; compared again at the end.</summary>
    public static IReadOnlyDictionary<string, string> StockHashesBefore { get; private set; } = new Dictionary<string, string>();

    [AssemblyInitialize]
    public static void Open(TestContext _)
    {
        var config = GameConfig.LoadOrDefault(Path.Combine(AppContext.BaseDirectory, "umgeditor.json"));
        if (!Directory.Exists(config.PaksDir)) Assert.Inconclusive($"stock Paks not found at {config.PaksDir}");
        StockHashesBefore = HashFolder(config.PaksDir);
        Source = GameSource.Open(config);
        UiIndex = UiAssetIndex.LoadOrBuild(Source);
        var textures = new TextureCache(Source, new AssetRipperTextureDecoder());
        var embedded = new System.Collections.Concurrent.ConcurrentDictionary<string, WidgetPackageTree?>(StringComparer.OrdinalIgnoreCase);
        Engine = new LayoutEngine(UiScaleSettings.Load(Source),
            p => embedded.GetOrAdd(p, k => { try { return WidgetTreeReader.Build(Source, k, readOnly: true); } catch { return null; } }),
            t => textures.Get(t) is { IsPlaceholder: false } img ? (img.Width, img.Height) : null);
    }

    /// <summary>The whole suite must leave the stock Paks byte-identical; a difference fails the run.</summary>
    [AssemblyCleanup]
    public static void Close()
    {
        var paks = Source?.Config.PaksDir;
        Source?.Dispose();
        if (paks == null) return;
        var after = HashFolder(paks);
        var changed = StockHashesBefore.Keys.Union(after.Keys)
            .Where(f => StockHashesBefore.GetValueOrDefault(f) != after.GetValueOrDefault(f)).Order().ToList();
        Console.WriteLine($"stock Paks: {after.Count} files hashed after the suite, {changed.Count} changed");
        if (changed.Count > 0) throw new InvalidOperationException($"stock Paks changed during the test run: {string.Join(", ", changed)}");
    }

    public static Dictionary<string, string> HashFolder(string dir) =>
        new DirectoryInfo(dir).GetFiles().AsParallel()
            .Select(f =>
            {
                using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                return (f.Name, Hash: Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant());
            })
            .ToDictionary(x => x.Name, x => x.Hash, StringComparer.OrdinalIgnoreCase);

    /// <summary>Raw Zen chunk bytes (what the provider serves for the .uasset) plus CUE4Parse's parse of them.</summary>
    public static (byte[] Chunk, IoPackage Package) Load(string packagePath)
    {
        var key = Source.ResolveFileKey(packagePath);
        if (!Source.Provider.TrySaveAsset(key, out var bytes)) throw new FileNotFoundException(key);
        return (bytes, (IoPackage) Source.LoadPackage(packagePath));
    }

    public static (ZenPackageLayout Layout, TaggedPropertyWalker Walker, byte[] Chunk) Layout(string packagePath)
    {
        var (chunk, pkg) = Load(packagePath);
        var layout = ZenPackageLayout.Read(chunk, pkg);
        return (layout, new TaggedPropertyWalker(layout.NameMap), chunk);
    }
}
