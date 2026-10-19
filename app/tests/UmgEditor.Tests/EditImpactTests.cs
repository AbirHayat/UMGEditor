using UmgEditor.Core;
using UmgEditor.Core.Editing;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>Where an edit shows up: the edited package and every package that embeds it (pinned to in-game measurements).</summary>
[TestClass]
public class EditImpactTests
{
    private const int W = 1786, H = 1000;
    private const string Base = Tv + "MatchTime/MatchTimeBase";
    private const string Plate = "Plate_MatchTimeBg/CanvasPanel_0/Img_Plate";

    private static readonly string[] BaseUsers =
    [
        "MatchTime", "MatchTimeCard", "MatchTimeGoal", "MatchTimeMemberChange",
        "MatchTimeConnection", "MatchTimeConnectionCard", "MatchTimeConnectionGoal", "MatchTimeConnectionMemberChange",
    ];

    private static EditSet C3Edit()
    {
        var plate = WidgetTreeReader.Build(Source, Base).Find("Img_Plate")!;
        return EditSet.Empty.With(SlotEditor.SetField(plate, EditSet.Empty, "LayoutData.Offsets.Right", 100).Changes, _ => null);
    }

    [TestMethod]
    public void AffectedPackages_MatchTimeBase_AreTheEightEmbeddingPackages()
    {
        var affected = EditImpact.AffectedPackages(new UsageGraph(UiIndex), [Base]);
        Assert.IsTrue(affected[0].Edited);
        CollectionAssert.AreEquivalent(BaseUsers, affected.Where(a => !a.Edited).Select(a => a.Name).ToArray());
        Assert.AreEqual("Offline scoreboard", affected.Single(a => a.Name == "MatchTime").Label);
        Assert.IsTrue(affected.Single(a => a.Name == "MatchTimeConnectionGoal").IsOnline);
        Assert.AreEqual("MatchTimeBase → MatchTimeGoal", affected.Single(a => a.Name == "MatchTimeGoal").Via);
    }

    /// <summary>
    /// C3 in game (ss/c3_b.png, validate_layout.py): the goal banner's navy plate right edge measured 463 px (game view),
    /// stock (ss/c1_b.png) 555 px. The prediction must give the same within ±3 px, and every embedding package must change.
    /// </summary>
    [TestMethod]
    public void Predict_C3_ChangesEveryEmbeddingPackage_AndMatchesTheGoalBannerInGame()
    {
        var impacts = new EditImpact(Engine, new UsageGraph(UiIndex), W, H).Predict(C3Edit(), p => WidgetTreeReader.Build(Source, p));
        foreach (var i in impacts)
        {
            Assert.IsNull(i.Error, i.Package.Name);
            Assert.IsTrue(i.Changes.Count > 0, $"{i.Package.Name}: nothing changes");
            Assert.IsTrue(i.Changes.All(c => c.OwnerPackage == "MatchTimeBase" && c.Path.EndsWith("Img_Plate")), $"{i.Package.Name}: {string.Join(", ", i.Changes.Select(c => c.Path))}");
            Assert.IsTrue(i.Changes.All(c => Math.Abs(c.DRight + 92.59f) < 0.05f && c.DLeft == 0 && c.DTop == 0 && c.DBottom == 0), $"{i.Package.Name}: only the right edge moves, by −100 units");
            Console.WriteLine($"{i.Package.Name}: {string.Join("; ", i.Changes.Select(c => $"{c.Path.Split('/').TakeLast(4).Aggregate((a, b) => a + "/" + b)} R {c.Before.Right:0.0}→{c.After.Right:0.0}"))}");
        }

        var goal = impacts.Single(i => i.Package.Name == "MatchTimeGoal").Changes.Single(c => c.Path.EndsWith("Goal/" + Plate));
        Assert.AreEqual(555.0, goal.Before.Right, 3.0, "stock, measured in ss/c1_b.png");
        Assert.AreEqual(463.0, goal.After.Right, 3.0, "C3, measured in ss/c3_b.png");
    }

    [TestMethod]
    public void PreEditWarning_NamesEveryEmbeddingPackage()
    {
        var tree = WidgetTreeReader.Build(Source, Base);
        var report = new EditWarnings(Engine, new UsageGraph(UiIndex), W, H).ForSelection(tree, tree.Find("Img_Plate")!);
        var boards = report.Warnings.Single(w => w.Kind == WarningKind.Boards);
        StringAssert.Contains(boards.Message, "Embedded by 8 packages, which change with it:");
        foreach (var name in BaseUsers) StringAssert.Contains(boards.Message, name);
        StringAssert.Contains(boards.Message, "MatchTime (Offline scoreboard)");
        StringAssert.Contains(boards.Message, "MatchTimeConnectionGoal (online)");
        foreach (var name in BaseUsers) StringAssert.Contains(report.ConfirmationText, name);
        Assert.AreEqual(8, report.DirectUsers);
        Console.WriteLine(report.ConfirmationText);

        // a package nobody embeds says so instead of a count
        var board = WidgetTreeReader.Build(Source, Tv + "MatchTime/MatchTime");
        var own = new EditWarnings(Engine, new UsageGraph(UiIndex), W, H).ForSelection(board, board.Find("Img_MatchTimeBg_1")!);
        StringAssert.Contains(own.ConfirmationText, "No other package embeds MatchTime");
    }
}
