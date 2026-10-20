using System.Buffers.Binary;
using System.Globalization;
using UmgEditor.Core;
using UmgEditor.Core.Animation;
using UmgEditor.Core.Editing;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>Branch experiment, step 4a: animation keys with their byte locations, readable descriptions, the inspector's rows.</summary>
[TestClass]
public class AnimationTests
{
    private const string Goal = Tv + "MatchTime/MatchTimeGoal";

    private static IEnumerable<string> MatchTimeFamily() =>
        UiIndex.Where(e => e.Kind == UiAssetKind.Widget).Select(e => GamePaths.ToPackagePath(e.Path))
            .Where(p => p.StartsWith(Tv + "MatchTime/", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal);

    [TestMethod]
    public void Bytes_EveryChannelOfTheMatchTimeFamily_IsLocated_AndBothCopiesDecodeToTheParse()
    {
        int channels = 0, keys = 0, compiled = 0;
        var problems = new List<string>();
        foreach (var package in MatchTimeFamily())
        {
            var bytes = Source.ReadPackageBytes(package).Bytes;
            foreach (var a in AnimationReader.Read(Source, package))
                foreach (var t in a.Tracks)
                    foreach (var c in t.Channels)
                    {
                        channels++;
                        var where = $"{GamePaths.ShortName(package)} {a.Name} {t.Widget} {c.Path}";
                        if (c.Kind is not { } kind) { problems.Add(where + ": not located"); continue; }
                        if (c.Compiled == "same" && c.CompiledAt == null) problems.Add(where + ": compiled copy not located");
                        foreach (var k in c.Keys)
                        {
                            keys++;
                            foreach (var (copy, frameAt, valueAt) in new[] { ("section", k.FrameAt, k.ValueAt), ("compiled", k.CompiledFrameAt, k.CompiledValueAt) })
                            {
                                if (copy == "compiled" && frameAt < 0) continue;
                                if (copy == "compiled") compiled++;
                                var frame = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(frameAt));
                                var ok = kind switch
                                {
                                    ChannelKind.Float => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(valueAt)) == float.Parse(k.Value, CultureInfo.InvariantCulture)
                                                         && (k.Interp == null || bytes[valueAt + 24] == Array.IndexOf(new[] { "Linear", "Constant", "Cubic", "None" }, k.Interp)),
                                    ChannelKind.Integer => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(valueAt)).ToString(CultureInfo.InvariantCulture) == k.Value,
                                    _ => bytes[valueAt] == (k.Value switch { "true" => 1, "false" => 0, _ => int.Parse(k.Value, CultureInfo.InvariantCulture) }),
                                };
                                if (frame != k.Frame || !ok) problems.Add($"{where} key {k.Frame} ({copy}): bytes don't hold {k.Frame}={k.Value}");
                            }
                        }
                    }
        }
        Console.WriteLine($"{channels} channels, {keys} keys, {compiled} compiled-copy keys checked");
        Assert.IsTrue(channels > 1000 && compiled > 300, $"{channels} channels, {compiled} compiled keys");
        Assert.AreEqual(0, problems.Count, string.Join("\n", problems.Take(20)));
    }

    [TestMethod]
    public void Describe_GoalBanner_ShowAndHide()
    {
        var anims = AnimationReader.Read(Source, Goal).ToDictionary(a => a.Name);
        string Y(string anim) => AnimationText.Describe(anims[anim].Tracks.Single(t => t.Widget == "Goal" && t.Track == "2DTransformTrack"))
            .Single(d => d.Name == "Translation.Y").Text;
        Assert.AreEqual("-96 → 0 over 0 s–0.4 s (cubic); holds 0 0.4 s–0.8 s", Y("Show"));
        Assert.AreEqual("0 until 0.4 s; 0 → -96 over 0.4 s–0.8 s (cubic)", Y("Hide"));
        var visibility = AnimationText.Describe(anims["Off"].Tracks.Single(t => t.Widget == "Goal" && t.Track == "ByteTrack")).Single();
        Assert.AreEqual("Visibility", visibility.Name);
        Assert.AreEqual("set to hidden (Collapsed) at 0 s", visibility.Text);
        // neutral channels (scale 1, angle 0, shear 0) are left out
        Assert.IsFalse(AnimationText.Describe(anims["Show"].Tracks.Single(t => t.Widget == "Goal" && t.Track == "2DTransformTrack")).Any(d => d.Name.StartsWith("Scale")));
    }

    [TestMethod]
    public void Bytes_AgreeWithTheC9Patch()
    {
        // AnimationPatch (device-tested in C9) finds the key block on its own; the bytes it changes are the reader's key values
        var anims = AnimationReader.Read(Source, Goal).ToDictionary(a => a.Name);
        var show = anims["Show"].Tracks.Single(t => t.Widget == "Goal" && t.Track == "2DTransformTrack");
        var y = show.Channels.Single(c => c.Path == "Translation[1]");
        var patch = new AnimationPatch(Source, Goal);
        Assert.IsTrue(patch.SetKeys(show.SectionExport, "Translation[1]", [new(1, Value: -96f, InterpMode: 1), new(2, Frame: 108000, Value: 0f)]));
        var stock = Source.ReadPackageBytes(Goal).Bytes;
        var changed = Enumerable.Range(0, stock.Length).Where(i => stock[i] != patch.Bytes[i]).ToList();
        var allowed = new[] { (y.Keys[1].ValueAt, 28), (y.Keys[2].FrameAt, 4), (y.Keys[2].ValueAt, 28) };
        Assert.IsTrue(changed.Count > 0 && changed.All(o => allowed.Any(r => o >= r.Item1 && o < r.Item1 + r.Item2)),
            string.Join(", ", changed.Select(o => $"0x{o:X}")));
        Assert.AreEqual(1, patch.Bytes[y.Keys[1].ValueAt + 24], "interp mode byte of key 1 = Constant");
    }

    [TestMethod]
    public void Inspect_GoalBanner_ListsItsTracks_WithKeysAndBytes()
    {
        var c = new EditorController(Source, new UsageGraph(UiIndex), Engine, p => WidgetTreeReader.Build(Source, p), project: "zztest-anim-readonly");
        var tree = WidgetTreeReader.Build(Source, Goal);
        var i = c.Inspect(tree, tree.Find("Goal")!);
        Assert.IsTrue(i.Animations.Count >= 6, i.AnimationsHeader);
        var show = i.Animations.Single(a => a.Animation == "Show" && a.Track.Track == "2DTransformTrack");
        StringAssert.Contains(show.DoesText, "Translation.Y: -96 → 0 over 0 s–0.4 s (cubic)");
        StringAssert.Contains(show.KeysText, "(no compiled copy: evaluated from the section)");
        StringAssert.Contains(show.KeysText, "frame @0x");
        StringAssert.Contains(i.AnimationsHeader, "change something");
        Assert.IsFalse(Directory.Exists(Path.Combine(Source.Config.EditsDir, "zztest-anim-readonly")), "inspecting creates nothing");
    }
}
