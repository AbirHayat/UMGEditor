using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Step 4 precondition: the C3 canary edit (MatchTimeBase › Img_Plate, Offsets.Right 0 → 100) changes a value that is
/// stored in the file and that no animation drives, so the patched bytes are what the game uses.
/// </summary>
[TestClass]
public class C3TargetTests
{
    private const string Base = Tv + "MatchTime/MatchTimeBase";
    private const string Right = "LayoutData.Offsets.Right";

    [TestMethod]
    public void C3_ImgPlateRight_IsStored_AndLocatedOnce()
    {
        var tree = WidgetTreeReader.Build(Source, Base);
        var plate = tree.Find("Img_Plate")!;
        Assert.AreEqual(SlotEditor.CanvasSlot, plate.Slot!.ClassName);
        var field = plate.Slot.Fields.Single(f => f.Path == Right);
        Assert.IsTrue(field.Stored, "Offsets.Right must be stored (in-place patch)");
        Assert.IsNull(SlotEditor.FieldRefusal(plate, Right));

        var (layout, walker, chunk) = Layout(Base);
        var hit = walker.Walk(chunk, layout.SpanOf(plate.Slot.ExportIndex)).Single(f => f.Path == Right);
        Assert.AreEqual(EditSet.Parse(field.Value), hit.Value, "walker and CUE4Parse agree on the stock value");
        Assert.AreEqual(1, BytePlanner.CountOccurrences(chunk, chunk.AsSpan(hit.Offset - BytePlanner.Context, hit.Length + 2 * BytePlanner.Context)));
        Console.WriteLine($"Img_Plate {plate.Slot.Name}.{Right} = {field.Value} at 0x{hit.Offset:X} in a {chunk.Length}-byte chunk; ±{BytePlanner.Context} B context unique");
    }

    [TestMethod]
    public void C3_ImgPlate_IsNotAnimationDriven()
    {
        var tree = WidgetTreeReader.Build(Source, Base);
        var plate = tree.Find("Img_Plate")!;
        Console.WriteLine($"MatchTimeBase animations: {(tree.AnimationNames.Count == 0 ? "none" : string.Join(", ", tree.AnimationNames))}");
        Assert.AreEqual(0, plate.Animations.Count, string.Join("; ", plate.Animations.Select(a => $"{a.Animation}: {a.TracksText}")));
        foreach (var ancestor in PathTo(tree.Root!, plate)!)
            Assert.IsFalse(ancestor.IsAnimated, $"{ancestor.Name} (on the path to Img_Plate) is animated inside MatchTimeBase");

        // the margin keys live in MovieScene sections and their compiled templates; MatchTimeBase has none
        var pkg = Source.LoadPackage(Base);
        var sceneExports = pkg.ExportsLazy.Select(e => e.Value.ExportType).Where(t => t.StartsWith("MovieScene") || t == "WidgetAnimation").ToList();
        Assert.AreEqual(0, sceneExports.Count, string.Join(", ", sceneExports.Distinct()));

        // where MatchTimeBase is embedded, animations can only move the instance as a whole (render transform on top)
        foreach (var board in new[] { "MatchTime/MatchTime", "MatchTimeConnection/MatchTimeConnection" })
        {
            var host = WidgetTreeReader.Build(Source, Tv + board);
            foreach (var inst in host.All().Where(n => n.EmbeddedPackagePath?.Equals(Base, StringComparison.OrdinalIgnoreCase) == true))
                Console.WriteLine($"{GamePaths.ShortName(board)} › {inst.Name}: {(inst.Animations.Count == 0 ? "not animated" : string.Join("; ", inst.Animations.Select(a => $"\"{a.Animation}\" {a.TracksText} ({a.Target})")))}");
        }
    }

    /// <summary>Nodes from root down to target (inclusive), or null when target isn't below root.</summary>
    private static List<WidgetNode>? PathTo(WidgetNode root, WidgetNode target)
    {
        if (root == target) return [root];
        foreach (var c in root.Children)
            if (PathTo(c, target) is { } rest) return [root, .. rest];
        return null;
    }
}
