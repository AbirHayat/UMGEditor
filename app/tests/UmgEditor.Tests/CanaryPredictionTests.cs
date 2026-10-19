using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Layout;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Step 3 check of the planned in-game canaries: does the edit trigger the rescale case, and what exactly should
/// the screenshot show? Writes the predicted edited layout (1786×1000, the validated game view) for validate_layout.py.
/// </summary>
[TestClass]
public class CanaryPredictionTests
{
    private const int W = 1786, H = 1000;

    private sealed record Canary(string Id, string Package, string Node, Func<WidgetNode, EditResult> Edit, string[] Watch);

    private static readonly Canary[] Canaries =
    [
        new("C1", "MatchTime/MatchTime", "Img_MatchTimeBg_1", n => SlotEditor.Move(n, EditSet.Empty, 20, 0), ["Normal_Display/Img_MatchTimeBg_1"]),
        new("C2", "MatchTime/MatchTime", "Img_MatchTimeBg_1", n => SlotEditor.SetField(n, EditSet.Empty, "LayoutData.Offsets.Right", 150), ["Normal_Display/Img_MatchTimeBg_1"]),
        new("C3-original", "MatchTime/MatchTime", "ScaleBox_0", n => SlotEditor.ResizeEdge(n, EditSet.Empty, Edge.Right, -200), ["Normal_Display/Img_MatchTimeBg_1"]),
        new("C3-alternative", "MatchTime/MatchTimeBase", "Img_Plate", n => SlotEditor.SetField(n, EditSet.Empty, "LayoutData.Offsets.Right", 100), ["Img_MatchTimeBg/CanvasPanel_0/Img_Plate"]),
    ];

    [TestMethod]
    public void Canaries_RescaleCase_AndPredictedRects()
    {
        var board = WidgetTreeReader.Build(Source, Tv + "MatchTime/MatchTime");
        var warnings = new EditWarnings(Engine, new UsageGraph(UiIndex), W, H);
        var outDir = Path.Combine(Source.Config.WorkDir, "out", "canary_predictions");
        Directory.CreateDirectory(outDir);
        var stock = Engine.Arrange(board, W, H);
        File.WriteAllText(Path.Combine(outDir, "stock.json"), LayoutExport.ToJson(stock, board.PackagePath));

        var results = new Dictionary<string, (bool Rescale, int OthersMove)>();
        foreach (var c in Canaries)
        {
            var tree = c.Package == "MatchTime/MatchTime" ? board : WidgetTreeReader.Build(Source, Tv + c.Package);
            var node = tree.Find(c.Node)!;
            var edit = c.Edit(node);
            Assert.IsTrue(edit.Allowed, $"{c.Id}: {edit.Reason}");
            var edits = EditSet.Empty.With(edit.Changes, _ => null);

            // consequences on the offline board (edits to child packages apply to their embedded instances)
            var pending = warnings.ForPendingEdit(board, board.Find(c.Package == "MatchTime/MatchTime" ? c.Node : "Img_MatchTimeBg")!, edits);
            var rescale = pending.Any(w => w.Kind == WarningKind.Rescale);
            var others = pending.Count(w => w.Kind == WarningKind.OthersMove);
            results[c.Id] = (rescale, others);

            var edited = Engine.Arrange(board, W, H, edits);
            File.WriteAllText(Path.Combine(outDir, c.Id + ".json"), LayoutExport.ToJson(edited, board.PackagePath,
                new { c.Id, Edits = edit.Changes.Select(ch => $"{GamePaths.ShortName(ch.Key.Package)} slot#{ch.Key.SlotExport} {ch.Key.Path}: {ch.OldValue} -> {ch.NewValue}") }));

            Console.WriteLine($"{c.Id}: {string.Join("; ", edit.Changes.Select(ch => $"{c.Node}.{ch.Key.Path.Replace("LayoutData.", "")} {ch.OldValue}→{ch.NewValue}"))}");
            foreach (var w in pending) Console.WriteLine($"    [{w.Severity}] {w.Message}");
            foreach (var watch in c.Watch)
            {
                var b = stock.All().Single(x => LayoutExport.PathOf(x).EndsWith(watch));
                var a = edited.All().Single(x => LayoutExport.PathOf(x).EndsWith(watch));
                Console.WriteLine($"    {watch}: stock x {b.Bounds.X:0.0} y {b.Bounds.Y:0.0} w {b.Bounds.Width:0.0} h {b.Bounds.Height:0.0} → edited x {a.Bounds.X:0.0} y {a.Bounds.Y:0.0} w {a.Bounds.Width:0.0} h {a.Bounds.Height:0.0} (game px, add 50/32 for the LDPlayer screenshot)");
            }
        }

        Assert.IsFalse(results["C1"].Rescale, "C1 must not rescale the board");
        Assert.IsFalse(results["C2"].Rescale, "C2 must not rescale the board");
        Assert.IsTrue(results["C3-original"].Rescale, "C3 on ScaleBox_0 rescales the board by construction");
        Assert.IsFalse(results["C3-alternative"].Rescale, "the C3 alternative must not rescale the board");
    }
}
