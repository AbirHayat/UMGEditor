using UmgEditor.Core;
using UmgEditor.Core.Textures;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>Step 3 (textures): which widgets name which texture, as the inspector's "Used by" list shows it.</summary>
[TestClass]
public class TextureUsageTests
{
    private const string MT = Tv + "MatchTime/", DT = Tv + "DemoTitle/";
    private static TextureUsage? _usage;
    public static TextureUsage Usage => _usage ??= TextureUsage.LoadOrBuild(Source, UiIndex);

    [TestMethod]
    public void UsersOf_ThePlatesAndTheLogo()
    {
        foreach (var t in new[] { MT + "Tex720/MatchTimePlate_0", MT + "Tex720/MatchTimePlate_1", MT + "Tex720/MatchTimeLogo", DT + "Tex720/DemoTitlePlate" })
        {
            var users = Usage.UsersOf(t);
            Console.WriteLine($"{GamePaths.ShortName(t)}: {users.Count} reference(s)");
            foreach (var u in users) Console.WriteLine($"  {GamePaths.ShortName(u.Package)}/{u.Export} ({u.ExportClass}) .{u.Property} = {u.ObjectPath}  [name map: {u.NameMapPath}]");
        }
        Assert.IsTrue(Usage.UsersOf(MT + "Tex720/MatchTimePlate_0").Any(r => r.Package == MT + "MatchTimeBase" && r.Export == "Img_Plate" && r.IsImageTexture));
        Assert.IsTrue(Usage.UsersOf(MT + "Tex720/MatchTimePlate_0").Any(r => r.Package == MT + "MatchTime"), "the board names the bar texture itself");
        Assert.IsTrue(Usage.UsersOf(MT + "Tex720/MatchTimeLogo").Any(r => r.Package == MT + "MatchTime" && r.IsImageTexture));
        var plate = Usage.Of(DT + "DemoTitleBase", "Img_Plate");
        Assert.IsNotNull(plate);
        Assert.AreEqual(DT + "Tex720/DemoTitlePlate", plate.Texture);
        Assert.AreEqual(DT + "Tex720/DemoTitlePlate.DemoTitlePlate", plate.NameMapPath, "C14's re-point string");
        Assert.AreEqual(MT + "Tex720/MatchTimePlate_0.MatchTimePlate",
            Usage.UsersOf(MT + "Tex720/MatchTimePlate_0").First(r => r.Package == MT + "MatchTimeBase").NameMapPath, "C16's re-point string (FName number split off)");
    }

    [TestMethod]
    public void NameMapForm_SplitsOnlyTheNumberSuffix()
    {
        Assert.AreEqual("/Game/a/X_0.X", TextureUsage.NameMapForm("/Game/a/X_0.X_0"));
        Assert.AreEqual("/Game/a/X.X", TextureUsage.NameMapForm("/Game/a/X.X"));
        Assert.AreEqual("/Game/a/X_01.X_01", TextureUsage.NameMapForm("/Game/a/X_01.X_01"), "a leading zero isn't an FName number");
    }

    [TestMethod]
    public void Usage_Summary()
    {
        var textures = Usage.Refs.Select(r => r.Texture).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var shared = Usage.Refs.GroupBy(r => r.Texture, StringComparer.OrdinalIgnoreCase).Count(g => g.Select(r => r.Package).Distinct().Count() > 1);
        Console.WriteLine($"{Usage.Refs.Count} references to {textures} textures in {Usage.Refs.Select(r => r.Package).Distinct().Count()} packages; {shared} textures named by more than one package");
        Assert.IsTrue(Usage.Refs.Count > 100);
    }
}
