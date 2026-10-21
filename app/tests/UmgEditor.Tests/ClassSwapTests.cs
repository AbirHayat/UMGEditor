using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Branch experiment, canary C16: changing an export's class to a class from another stock package (the goal banner's
/// plate → DemoTitleBase, the replay plate → MatchTimeBase), and the refusals.
/// </summary>
[TestClass]
public class ClassSwapTests
{
    private const string Goal = Tv + "MatchTime/MatchTimeGoal", DemoTitle = Tv + "DemoTitle/DemoTitle";
    private const string DemoTitleBase = Tv + "DemoTitle/DemoTitleBase", MatchTimeBase = Tv + "MatchTime/MatchTimeBase";

    [TestMethod]
    public void GoalBannerPlate_ToDemoTitleBase()
    {
        var (fileKey, bytes) = Source.ReadPackageBytes(Goal);
        var r = ClassSwap.Swap(Source, fileKey, bytes, "MatchTimeBase_1", DemoTitleBase);
        Console.WriteLine(string.Join("\n", r.Log));
        Assert.IsNotNull(r.Chunk, string.Join("\n", r.Refusals));
        Assert.AreEqual(bytes.Length, r.Chunk.Length);
        var after = Source.ParsePackage(fileKey, r.Chunk).ExportsLazy.Select(e => e.Value).Single(e => e.Name == "MatchTimeBase_1");
        Assert.AreEqual("DemoTitleBase_C", after.Class?.Name);
        Assert.AreEqual(CUE4Parse.UE4.IO.Objects.FPackageId.FromName(Tv + "MatchTime/MatchTimeBase_1").id, r.PackageSwap.Old);
        Assert.AreEqual(CUE4Parse.UE4.IO.Objects.FPackageId.FromName(DemoTitleBase).id, r.PackageSwap.New);
        // stock keeps the graph's imported packages sorted by id; the swap moves DemoTitleBase into its place
        var before = GraphIds(bytes);
        var now = GraphIds(r.Chunk);
        CollectionAssert.AreEqual(before.Order().ToList(), before);
        CollectionAssert.AreEqual(now.Order().ToList(), now);
        CollectionAssert.AreEquivalent(before.Select(id => id == r.PackageSwap.Old ? r.PackageSwap.New : id).ToList(), now);
    }

    private static List<ulong> GraphIds(byte[] chunk)
    {
        int at = BitConverter.ToInt32(chunk, 52), count = BitConverter.ToInt32(chunk, at);
        var ids = new List<ulong>();
        for (int i = 0, p = at + 4; i < count; i++) { ids.Add(BitConverter.ToUInt64(chunk, p)); p += 12 + 8 * BitConverter.ToInt32(chunk, p + 8); }
        return ids;
    }

    [TestMethod]
    public void ReplayPlate_ToMatchTimeBase()
    {
        var (fileKey, bytes) = Source.ReadPackageBytes(DemoTitle);
        var r = ClassSwap.Swap(Source, fileKey, bytes, "Plate_DemoTitleBg", MatchTimeBase);
        Console.WriteLine(string.Join("\n", r.Log));
        Assert.IsNotNull(r.Chunk, string.Join("\n", r.Refusals));
        Assert.AreEqual("MatchTimeBase_C", Source.ParsePackage(fileKey, r.Chunk).ExportsLazy.Select(e => e.Value).Single(e => e.Name == "Plate_DemoTitleBg").Class?.Name);
    }

    /// <summary>C16's text colours: the switch brand rows of ColorTable (default theme) and ColorTable0164F → white / black.</summary>
    [TestMethod]
    public void ColorTables_SwitchBrandRows_ToWhiteAndBlack()
    {
        foreach (var table in new[] { "/Game/Assets/ui/Color/ColorTable", "/Game/Assets/ui/Color/ColorTable0164F" })
        {
            var (fileKey, bytes) = Source.ReadPackageBytes(table);
            var writes = new List<(int, byte[], string)>();
            var expected = new Dictionary<(int, string), string>();
            foreach (var (row, rgba) in new[] { ("STR_SWITCH_BRAND_1", ((byte) 255, (byte) 255, (byte) 255, (byte) 255)), ("STR_SWITCH_BRAND_2", ((byte) 0, (byte) 0, (byte) 0, (byte) 255)) })
            {
                var (plan, why) = DataTableColorEdit.Plan(Source, fileKey, bytes, row, rgba);
                Assert.IsNotNull(plan, $"{table} {row}: {why}");
                Console.WriteLine($"{table}: {plan.What}");
                writes.Add((plan.Offset, plan.Bgra, plan.What));
                foreach (var kv in plan.Expected) expected[kv.Key] = kv.Value;
            }
            var r = NameRenamePatch.Apply(Source, fileKey, bytes, [], writes, expected);
            Console.WriteLine(string.Join("\n", r.Log));
            Assert.IsTrue(r.Ok, string.Join("\n", r.Refusals));
        }
        var (fk, b) = Source.ReadPackageBytes("/Game/Assets/ui/Color/ColorTable");
        StringAssert.Contains(DataTableColorEdit.Plan(Source, fk, b, "STR_NO_SUCH_ROW", (0, 0, 0, 255)).Why, "no row");
    }

    [TestMethod]
    public void Refusals()
    {
        var (fileKey, bytes) = Source.ReadPackageBytes(Goal);
        // MatchTimeBase provides two plates in the goal banner: its class is referenced by another export too
        StringAssert.Contains(ClassSwap.Swap(Source, fileKey, bytes, "Plate_MatchTimeBg", DemoTitleBase).Refusals.Single(), "also referenced by export");
        StringAssert.Contains(ClassSwap.Swap(Source, fileKey, bytes, "NoSuchExport", DemoTitleBase).Refusals.Single(), "0 exports named");
        // the new class's package is already imported (MatchTimeBase_2's own package)
        StringAssert.Contains(ClassSwap.Swap(Source, fileKey, bytes, "MatchTimeBase_1", Tv + "MatchTime/MatchTimeBase_2").Refusals.Single(), "already imported");
    }
}
