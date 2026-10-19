using System.Collections.Concurrent;
using System.Globalization;
using UmgEditor.Core;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>Step 1: find the exact bytes of stored slot values in the raw Zen chunk, without legacy conversion.</summary>
[TestClass]
public class LocateBytesTests
{
    [TestMethod]
    public void ExportSpans_AreContiguousInsideTheChunk_ForMatchTime()
    {
        var (chunk, pkg) = Load(Tv + "MatchTime/MatchTime");
        var layout = ZenPackageLayout.Read(chunk, pkg);

        Assert.AreEqual(1473, pkg.ExportMap.Length);
        Assert.AreEqual(pkg.ExportMap.Length, layout.Exports.Count, "every export of this package carries data");
        var byStart = layout.Exports.OrderBy(s => s.Start).ToList();
        Assert.AreEqual(layout.ExportDataStart, byStart[0].Start);
        for (int i = 1; i < byStart.Count; i++)
            Assert.AreEqual(byStart[i - 1].End, byStart[i].Start, $"gap or overlap before export {byStart[i].ExportIndex}");
        Assert.IsTrue(byStart[^1].End <= chunk.Length, $"data ends at {byStart[^1].End}, chunk is {chunk.Length}");
    }

    /// <summary>Canary 1 patched these exact offsets in game (Mask Left 129 at 0x133557, Top 7 at 0x133574).</summary>
    [TestMethod]
    public void Walker_FindsMaskOffsets_AtTheCanaryProvenBytes()
    {
        var (layout, walker, chunk) = Layout(Tv + "MatchTime/MatchTime");
        var tree = WidgetTreeReader.Build(Source, Tv + "MatchTime/MatchTime");
        var slot = tree.Find("Mask")!.Slot!;
        var fields = walker.Walk(chunk, layout.SpanOf(slot.ExportIndex));

        var left = fields.Single(f => f.Path == "LayoutData.Offsets.Left");
        var top = fields.Single(f => f.Path == "LayoutData.Offsets.Top");
        Assert.AreEqual(0x133557, left.Offset);
        Assert.AreEqual(129.0, left.Value);
        Assert.AreEqual(0x133574, top.Offset);
        Assert.AreEqual(7.0, top.Value);
        CollectionAssert.AreEquivalent(
            new[] { "LayoutData.Offsets.Left", "LayoutData.Offsets.Top", "LayoutData.Offsets.Right", "LayoutData.Offsets.Bottom", "ZOrder" },
            fields.Select(f => f.Path).ToArray());
    }

    /// <summary>
    /// Every stored numeric/bool slot value in every widget blueprint must have exactly one walker location whose bytes
    /// decode to the value CUE4Parse parsed, and the walker must not report numeric fields CUE4Parse didn't see.
    /// </summary>
    [TestMethod]
    public void Walker_MatchesCue4Parse_ForEveryStoredSlotValue_InAllWidgets()
    {
        var widgets = UiIndex.Where(e => e.Kind == UiAssetKind.Widget).ToList();
        var problems = new ConcurrentBag<string>();
        int slots = 0, fieldsChecked = 0;

        Parallel.ForEach(widgets, entry =>
        {
            var path = GamePaths.ToPackagePath(entry.Path);
            try
            {
                var tree = WidgetTreeReader.Build(Source, path);
                var (layout, walker, chunk) = Layout(path);
                foreach (var node in tree.All())
                {
                    if (node.Slot is not { } slot) continue;
                    Interlocked.Increment(ref slots);
                    var located = walker.Walk(chunk, layout.SpanOf(slot.ExportIndex)).ToLookup(f => f.Path);
                    foreach (var field in slot.Fields.Where(f => f.Stored))
                    {
                        if (!TryNumber(field.Value, out var expected)) continue; // enums and object refs aren't patched
                        Interlocked.Increment(ref fieldsChecked);
                        var hits = located[field.Path].ToList();
                        if (hits.Count != 1) problems.Add($"{entry.Name}/{node.Name} {slot.Name}.{field.Path}: {hits.Count} locations");
                        else if (Math.Abs(hits[0].Value - expected) > 1e-6 * Math.Max(1, Math.Abs(expected)))
                            problems.Add($"{entry.Name}/{node.Name} {slot.Name}.{field.Path}: walker {hits[0].Value} at 0x{hits[0].Offset:X}, CUE4Parse {expected}");
                    }
                    foreach (var extra in located.Select(g => g.Key).Where(k => slot.Fields.All(f => !f.Stored || f.Path != k)))
                        problems.Add($"{entry.Name}/{node.Name} {slot.Name}.{extra}: walker found a value CUE4Parse didn't report");
                }
            }
            catch (Exception e)
            {
                problems.Add($"{entry.Name}: {e.GetType().Name}: {e.Message}");
            }
        });

        Console.WriteLine($"{widgets.Count} widget blueprints, {slots} slots, {fieldsChecked} stored numeric/bool fields checked, {problems.Count} problems");
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Take(20)));
        Assert.IsTrue(fieldsChecked > 20000, $"only {fieldsChecked} fields checked");
    }

    private static bool TryNumber(string value, out double number)
    {
        if (value is "true" or "false") { number = value == "true" ? 1 : 0; return true; }
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }
}
