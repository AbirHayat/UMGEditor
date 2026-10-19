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
    public static IReadOnlyDictionary<string, FileHash> StockHashesBefore { get; private set; } = new Dictionary<string, FileHash>();

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
        var lines = new List<string>();
        foreach (var name in StockHashesBefore.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            var (b, a) = (StockHashesBefore.GetValueOrDefault(name), after.GetValueOrDefault(name));
            var path = Path.Combine(paks, name);
            if (b == null) lines.Add($"  ADDED    {path}  ({a!.Size:N0} bytes, sha256 {a.Hash})");
            else if (a == null) lines.Add($"  REMOVED  {path}  (was {b.Size:N0} bytes, sha256 {b.Hash})");
            else if (a != b) lines.Add($"  MODIFIED {path}  size {b.Size:N0} -> {a.Size:N0} bytes, sha256 {b.Hash} -> {a.Hash}");
        }
        Console.WriteLine($"stock Paks: {after.Count} files hashed after the suite, {lines.Count} changed");
        if (lines.Count > 0)
            throw new InvalidOperationException(
                $"STOCK PAKS CHANGED during this test run ({lines.Count} file{(lines.Count == 1 ? "" : "s")}):{Environment.NewLine}{string.Join(Environment.NewLine, lines)}{Environment.NewLine}" +
                "This is the suite-wide check in GameFixture.Close (before/after sha256 of every file in PaksDir). MSTest reports it " +
                "on the last test that ran; that test's own assertions passed. Restore the files above from the pristine copy");
    }

    public sealed record FileHash(string Hash, long Size);

    public static Dictionary<string, FileHash> HashFolder(string dir) =>
        new DirectoryInfo(dir).GetFiles().AsParallel()
            .Select(f =>
            {
                using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                return (f.Name, Hash: new FileHash(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant(), f.Length));
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
