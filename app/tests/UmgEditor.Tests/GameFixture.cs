using CUE4Parse.UE4.Assets;
using UmgEditor.Core;
using UmgEditor.Core.Patching;

namespace UmgEditor.Tests;

/// <summary>Opens the stock containers once for the whole test run (read-only).</summary>
[TestClass]
public static class GameFixture
{
    public static GameSource Source { get; private set; } = null!;
    public static IReadOnlyList<UiAssetEntry> UiIndex { get; private set; } = [];

    public const string Tv = "/Game/Assets/ui/Data/Widget/TVGraphics/SwitchAssets_Original/";

    [AssemblyInitialize]
    public static void Open(TestContext _)
    {
        var config = GameConfig.LoadOrDefault(Path.Combine(AppContext.BaseDirectory, "umgeditor.json"));
        if (!Directory.Exists(config.PaksDir)) Assert.Inconclusive($"stock Paks not found at {config.PaksDir}");
        Source = GameSource.Open(config);
        UiIndex = UiAssetIndex.LoadOrBuild(Source);
    }

    [AssemblyCleanup]
    public static void Close() => Source?.Dispose();

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
