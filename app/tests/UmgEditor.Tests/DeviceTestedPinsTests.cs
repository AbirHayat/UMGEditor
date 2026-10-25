using System.Security.Cryptography;
using System.Text.Json;
using UmgEditor.Core.Modding;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// The device-tested canaries pinned by sha256 (recipes\pins\device-tested.json, taken from the files that were on the
/// phone and the chunks packed into them). The local copies must still hash as pinned, and what Core can rebuild today
/// (UclC16: C16, C16b, C17b) must give exactly the pinned chunks. C13–C15, C17a and C18 rebuild in their own anchors or
/// with their recipes.
/// </summary>
[TestClass]
public class DeviceTestedPinsTests
{
    private sealed record PinnedFile(long Size, string Sha256);
    private sealed record Canary(string? Container, string? Retoc, string? Stub, Dictionary<string, PinnedFile> Files, Dictionary<string, string> Chunks,
        Dictionary<string, string>? Inputs = null);
    private sealed record Pins(string About, Dictionary<string, Canary> Canaries);

    private static Pins Load()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "recipes", "pins", "device-tested.json"))) dir = dir.Parent;
        if (dir == null) Assert.Fail("recipes\\pins\\device-tested.json not found above the test folder");
        return JsonSerializer.Deserialize<Pins>(File.ReadAllText(Path.Combine(dir.FullName, "recipes", "pins", "device-tested.json")))!;
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [TestMethod]
    public void Pins_CoverEveryDeviceTestedCanary()
    {
        var pins = Load();
        CollectionAssert.AreEquivalent(new[] { "C13", "C14", "C15", "C16", "C16b", "C16b_test", "C17a", "C17b", "C18" }, pins.Canaries.Keys.ToList());
        Assert.AreEqual(83 + 74, pins.Canaries.Values.Sum(c => c.Chunks.Count), "83 canary chunks + C16b_test's 74");
        Assert.IsTrue(pins.Canaries.Values.SelectMany(c => c.Files.Values.Select(f => f.Sha256).Concat(c.Chunks.Values)).All(h => h.Length == 64 && h.All(Uri.IsHexDigit)));
    }

    [TestMethod]
    public void Pins_MatchTheDeviceTestedFilesOnDisk()
    {
        int checkedFiles = 0;
        var missing = new List<string>();
        foreach (var (name, canary) in Load().Canaries)
        {
            var mod = Path.Combine(Source.Config.WorkDir, "out", "mods", name, "mod");
            if (!Directory.Exists(mod)) { missing.Add(name); continue; }
            CollectionAssert.AreEquivalent(canary.Files.Keys.ToList(), Directory.GetFiles(mod).Select(Path.GetFileName).ToList(), $"{name}: the files in mod\\");
            foreach (var (file, pin) in canary.Files)
            {
                var bytes = File.ReadAllBytes(Path.Combine(mod, file));
                Assert.AreEqual(pin.Size, bytes.Length, $"{name}/{file} size");
                Assert.AreEqual(pin.Sha256, Sha(bytes), $"{name}/{file}");
                checkedFiles++;
            }
        }
        Console.WriteLine($"{checkedFiles} device files hash as pinned{(missing.Count > 0 ? $"; not on disk: {string.Join(", ", missing)}" : "")}");
        if (checkedFiles == 0) Assert.Inconclusive("no device-tested build found under work\\out\\mods");
    }

    /// <summary>C16, C16b and C17b rebuilt by UclC16 (as canary-ucl does) give exactly the pinned chunks.</summary>
    [TestMethod]
    [DataRow("C16", "C16", null, false, false)]
    [DataRow("C16b", "C16b", "C16b", true, false)]
    [DataRow("C17b", "C16b", "C16b", true, true)]
    public void UclBuilds_RebuildThePinnedChunks(string canary, string set, string? art, bool centre, bool font)
    {
        if (!Directory.Exists(Path.Combine(Source.Config.EditsDir, set))) Assert.Inconclusive($"edit set {set} is missing");
        var artDir = art == null ? null : Path.Combine(Source.Config.WorkDir, "out", "art", art);
        if (artDir != null && !Directory.Exists(artDir)) Assert.Inconclusive($"art {artDir} is missing");
        var groups = font ? [.. UclC16.AllGroups, UclC16.PesGroup] : UclC16.AllGroups;
        var built = UclC16.Build(Source, Source.Config, set, groups, artDir, centre, font);
        Assert.IsTrue(built.Steps.All(s => s.Ok), string.Join("\n", built.Steps.Where(s => !s.Ok).Select(s => $"{s.What}: {s.Detail}")));
        var pinned = Load().Canaries[canary].Chunks;
        CollectionAssert.AreEquivalent(pinned.Keys.ToList(), built.Packages.Select(p => p.PackagePath).ToList(), $"{canary}: the packages");
        foreach (var p in built.Packages) Assert.AreEqual(pinned[p.PackagePath], Sha(p.Chunk), $"{canary}: {p.PackagePath}");
        Console.WriteLine($"{canary}: {built.Packages.Count} chunks identical to the device-tested build");
    }
}
