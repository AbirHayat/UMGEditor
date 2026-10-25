using UmgEditor.Core;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using UmgEditor.Core.Textures;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// A texture named like a folder of its own path (CmnIconAlert: /…/Icon/CmnIconAlert/Tex720/CmnIconAlert). The name map
/// holds the package path and the short name as two names; renaming both renames the path's last segment only. The
/// re-parse model must rename each stored name once, left to right, longest first, and never rename inside text it
/// already replaced: chained string replaces renamed the folder too and refused a correct clone (2026-10-25).
/// </summary>
[TestClass]
public class TextureNameCollisionTests
{
    private const string Folder = "/Game/Assets/ui/Data/Widget/Parts/Icon/CmnIconAlert/Tex720/";
    private const string Alert = Folder + "CmnIconAlert", NewShort = "CmnIconAleU1";
    private const string Correct = Folder + NewShort;                                            // folder kept
    private const string Wrong = "/Game/Assets/ui/Data/Widget/Parts/Icon/CmnIconAleU1/Tex720/" + NewShort; // folder renamed too
    private static readonly (string From, string To)[] Renames = [(Alert, Correct), ("CmnIconAlert", NewShort)];

    /// <summary>The old model: one String.Replace per rename, longest first; each pass rescans the text the last one wrote.</summary>
    private static string ChainedReplace(string s) => Renames.OrderByDescending(r => r.From.Length).Aggregate(s, (t, r) => t.Replace(r.From, r.To, StringComparison.Ordinal));

    /// <summary>Every value of the clone that differs from stock, side by side: stock, parsed clone, the old model, the new one.</summary>
    [TestMethod]
    public void CmnIconAlert_ExpectedVsActualNames_SideBySide()
    {
        var (fileKey, stock) = Source.ReadPackageBytes(Alert);
        var holders = ZenNameMap.Read(stock).Where(n => n.Text.Contains("CmnIconAlert", StringComparison.Ordinal)).Select(n => n.Text).ToList();
        Console.WriteLine($"names holding \"CmnIconAlert\": {string.Join(" | ", holders)}");
        CollectionAssert.AreEquivalent(new[] { Alert, "CmnIconAlert" }, holders, "exactly the package path and the export name");

        var (renamed, why) = ZenNameMap.Rename(stock, Renames);
        Assert.IsNotNull(renamed, why);
        var a = TagInserter.Flatten(Source.ParsePackage(fileKey, stock).ExportsLazy[0].Value);
        var b = TagInserter.Flatten(Source.ParsePackage(fileKey, renamed).ExportsLazy[0].Value);
        int oldWrong = 0, newWrong = 0, changed = 0;
        foreach (var k in a.Keys.Union(b.Keys).Where(k => a.GetValueOrDefault(k) != b.GetValueOrDefault(k)))
        {
            var x = a.GetValueOrDefault(k)!; var y = b.GetValueOrDefault(k)!;
            var old = ChainedReplace(x); var now = NameRenamePatch.RenameText(x, Renames);
            Console.WriteLine($"{k}\n  stock     {x}\n  actual    {y}\n  old model {old}{(old == y ? "" : "   ≠ actual")}\n  new model {now}{(now == y ? "" : "   ≠ actual")}");
            changed++;
            if (old != y) oldWrong++;
            if (now != y) newWrong++;
        }
        Console.WriteLine($"{changed} values differ from stock; the old model mispredicts {oldWrong}, the new one {newWrong}");
        Assert.IsTrue(changed > 0);
        Assert.IsTrue(oldWrong > 0, "the old model should have mispredicted (the cause of the false refusal)");
        Assert.AreEqual(0, newWrong);
    }

    [TestMethod]
    public void RenameText_SinglePass_LeftToRight_LongestFirst_NoRescan()
    {
        Assert.AreEqual(Correct, NameRenamePatch.RenameText(Alert, Renames));
        Assert.AreEqual(Correct + "." + NewShort, NameRenamePatch.RenameText(Alert + ".CmnIconAlert", Renames));
        Assert.AreEqual(NewShort, NameRenamePatch.RenameText("CmnIconAlert", Renames));
        Assert.AreEqual("/x/CmnIconAleU1/y", NameRenamePatch.RenameText("/x/CmnIconAlert/y", Renames), "a lone short name is still renamed");
        // replaced text is never scanned again: a → b, b → c gives b, not c
        Assert.AreEqual("bc", NameRenamePatch.RenameText("ab", [("a", "b"), ("b", "c")]));
        // at one position the longest match wins, whatever the list order
        Assert.AreEqual("X", NameRenamePatch.RenameText("abc", [("ab", "Y"), ("abc", "X")]));
        Assert.AreEqual("Yd", NameRenamePatch.RenameText("abd", [("ab", "Y"), ("abc", "X")]));
        Assert.AreEqual("", NameRenamePatch.RenameText("", Renames));
        Assert.AreEqual("unchanged", NameRenamePatch.RenameText("unchanged", Renames));
    }

    /// <summary>The regression: CmnIconAlert's white copy and a same-size resized clone now pass, and keep the folder.</summary>
    [TestMethod]
    public void CmnIconAlert_ClonesAndKeepsItsFolder()
    {
        var white = TextureClone.Build(Source, Alert, NewShort, TextureClone.Solid(255, 255, 255));
        Console.WriteLine(string.Join("\n", white.Log));
        Assert.IsNotNull(white.Package, string.Join("\n", white.Refusals));
        Assert.AreEqual(Correct, white.Package.PackagePath);
        var names = ZenNameMap.Read(white.Package.Chunk).Select(n => n.Text).ToList();
        CollectionAssert.Contains(names, Correct);
        CollectionAssert.DoesNotContain(names, Wrong);
        CollectionAssert.DoesNotContain(names, Alert);

        var t = TextureEdits.LoadTexture(Source, Alert)!;
        var mips = t.PlatformData.Mips.Select(m => new TextureClone.Mip(m.SizeX, m.SizeY, TextureClone.Solid(0, 255, 0)(m.SizeX, m.SizeY))).ToList();
        var resized = TextureClone.BuildResized(Source, Alert, NewShort, mips);
        Assert.IsNotNull(resized.Package, string.Join("\n", resized.Refusals));
        Assert.AreEqual(Correct, resized.Package.PackagePath);

        Assert.IsNull(TextureClone.NameRefusal(Source, Alert));
        Assert.IsNull(TextureEdits.SourceRefusal(Source, Alert), "CmnIconAlert can be a source");
    }

    /// <summary>A clone whose folder was renamed too (what the old model expected) is refused by the re-parse: the comparison is as strict as before.</summary>
    [TestMethod]
    public void WronglyRenamedClone_IsStillRefused()
    {
        var (fileKey, stock) = Source.ReadPackageBytes(Alert);
        // the bytes of a wrong clone: the path's folder renamed as well (same length, valid hashes)
        var (wrong, why) = ZenNameMap.Rename(stock, [(Alert, Wrong), ("CmnIconAlert", NewShort)]);
        Assert.IsNotNull(wrong, why);
        var r = NameRenamePatch.Verify(Source, fileKey, stock, wrong, Renames);
        Console.WriteLine(string.Join("\n", r.Refusals));
        Assert.IsFalse(r.Ok);
        Assert.IsTrue(r.Refusals.Any(x => x.Contains("unintended difference") && x.Contains(Wrong)), string.Join("\n", r.Refusals));

        // the same check passes the correct clone, and Apply gives exactly those bytes
        var (right, _) = ZenNameMap.Rename(stock, Renames);
        Assert.IsTrue(NameRenamePatch.Verify(Source, fileKey, stock, right!, Renames).Ok);
        CollectionAssert.AreEqual(right, NameRenamePatch.Apply(Source, fileKey, stock, Renames).Chunk);

        // and the old model's wrong prediction is not what the clone holds
        Assert.AreNotEqual(ChainedReplace(Alert), NameRenamePatch.RenameText(Alert, Renames));
    }

    /// <summary>
    /// Read-only scan of every UI texture: its short name (without an FName number) is a folder of its own path ("folder"),
    /// is part of a folder's name ("in folder"), or is held by another name of the package: the same path in another
    /// letter case ("case", already refused as a source by the name check) or anything else ("other"). Each cloneable one
    /// must clone, with its folders unchanged.
    /// </summary>
    [TestMethod]
    public void Scan_TexturesNamedLikeAFolderOrAnotherName()
    {
        var formats = TextureFormatsOf();
        int scanned = 0;
        var lines = new List<(string Kinds, string Line)>();
        foreach (var f in formats.All)
        {
            byte[] stock;
            try { stock = Source.ReadPackageBytes(f.Texture).Bytes; } catch { continue; }
            scanned++;
            var slash = f.Texture.LastIndexOf('/');
            var folder = f.Texture[..(slash + 1)];
            var (b, _) = TextureClone.SplitNumber(f.Texture[(slash + 1)..]);
            var segments = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var others = ZenNameMap.Read(stock).Select(n => n.Text).Where(n => n.Contains(b, StringComparison.Ordinal) && n != folder + b && n != b).ToList();
            var kinds = new List<string>();
            if (segments.Contains(b)) kinds.Add("folder");
            else if (segments.Any(x => x.Contains(b, StringComparison.Ordinal))) kinds.Add("in folder");
            if (others.Any(o => o.Equals(folder + b, StringComparison.OrdinalIgnoreCase))) kinds.Add("case");
            if (others.Any(o => !o.Equals(folder + b, StringComparison.OrdinalIgnoreCase))) kinds.Add("other: " + string.Join(", ", others.Where(o => !o.Equals(folder + b, StringComparison.OrdinalIgnoreCase))));
            if (kinds.Count == 0) continue;
            string clone = f.Cloneable ? "" : f.PixelFormat == "PF_B8G8R8A8" && f.InlineMips ? "not a source: " + f.NameProblem : $"not a source ({f.PixelFormat}{(f.InlineMips ? "" : ", streamed")})";
            if (f.Cloneable)
            {
                var newShort = b[..^2] + "U1" + f.Texture[(slash + 1 + b.Length)..];
                var r = TextureClone.Build(Source, f.Texture, newShort, TextureClone.Solid(255, 255, 255));
                Assert.IsNotNull(r.Package, $"{f.Texture}: {string.Join("; ", r.Refusals)}");
                Assert.AreEqual(folder + newShort, r.Package.PackagePath);
                clone = $"clones as {newShort}, folders kept";
            }
            lines.Add((string.Join(" + ", kinds), $"[{string.Join(" + ", kinds)}] {f.Texture} → {clone}"));
        }
        Console.WriteLine($"{scanned} UI textures scanned, {lines.Count} hits; by kind: {string.Join(", ", lines.GroupBy(l => l.Kinds.Split(':')[0]).Select(g => $"{g.Key} {g.Count()}"))}");
        foreach (var l in lines.OrderBy(l => l.Kinds, StringComparer.Ordinal).ThenBy(l => l.Line, StringComparer.Ordinal)) Console.WriteLine(l.Line);
    }

    private static TextureFormats? _formats;
    private static TextureFormats TextureFormatsOf() => _formats ??= TextureFormats.LoadOrBuild(Source, UiIndex);
}
