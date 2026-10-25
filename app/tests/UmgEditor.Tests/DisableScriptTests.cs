using System.Diagnostics;
using System.Security.Cryptography;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Stage 2f: DISABLE.ps1. Files reach the device by hand, so the script prints a checklist, or moves only this mod's files
/// (name AND sha256) in a folder on this PC into _disabled\&lt;mod&gt; and back, dry run unless -Apply. These tests run the
/// generated script in Windows PowerShell on a synthetic mod under work\out\mods\zzds-&lt;id&gt; (deleted afterwards).
/// </summary>
[TestClass]
public class DisableScriptTests
{
    private static readonly WriteGuard Mods = WriteGuard.ForMods(Source.Config);
    private string _root = "";

    [TestInitialize]
    public void Init() => _root = Path.Combine(Mods.Root, "zzds-" + Guid.NewGuid().ToString("N")[..8]);

    [TestCleanup]
    public void Cleanup() => Mods.DeleteDirectory(_root);

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>The synthetic mod: a container (stub, ucas, utoc) and a loose-file pak, as C17b ships.</summary>
    private static readonly Dictionary<string, byte[]> ModFiles = new()
    {
        ["pc9999_mobile_and_P.pak"] = [1, 2, 3], ["pc9999_mobile_and_P.ucas"] = [4, 5, 6, 7], ["pc9999_mobile_and_P.utoc"] = [8, 9],
        ["pc9998_mobile_and_P.pak"] = [10, 11, 12, 13, 14],
    };

    private string Built()
    {
        foreach (var (name, bytes) in ModFiles) Mods.WriteAllBytes(Path.Combine(_root, "mod", name), bytes);
        var (script, files, why) = DisableScript.Write(Source.Config, _root, new DateTime(2026, 10, 25));
        Assert.IsNotNull(script, why);
        Assert.AreEqual(ModFiles.Count, files!.Count);
        var text = File.ReadAllText(script);
        Assert.IsTrue(text.All(c => c < 127), "the script is ASCII (PowerShell 5.1 reads a BOM-less file as ANSI)");
        return script;
    }

    private string Folder(string name, params (string Name, byte[] Bytes)[] files)
    {
        var dir = Path.Combine(_root, name);
        Mods.CreateDirectory(dir);
        foreach (var (n, b) in files) Mods.WriteAllBytes(Path.Combine(dir, n), b);
        return dir;
    }

    private static (string Name, byte[] Bytes)[] AllModFiles => ModFiles.Select(kv => (kv.Key, kv.Value)).ToArray();

    private static string Listing(string dir) => string.Join("\n", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(f => $"{Path.GetRelativePath(dir, f)} {Sha(File.ReadAllBytes(f))}"));

    private static (int Exit, string Output) Run(string script, params string[] args)
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(exe)) Assert.Inconclusive("Windows PowerShell isn't installed");
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        Console.WriteLine($"> {string.Join(" ", args)}  (exit {p.ExitCode})\n{output}");
        return (p.ExitCode, output);
    }

    [TestMethod]
    public void NoDir_PrintsTheChecklist_AndChangesNothing()
    {
        var script = Built();
        var (exit, output) = Run(script);
        Assert.AreEqual(0, exit);
        foreach (var (name, bytes) in ModFiles) StringAssert.Contains(output, $"{name}  {bytes.Length} bytes  sha256 {Sha(bytes)}");
        Assert.AreEqual(2, Run(script, "-Apply").Exit, "-Apply needs -Dir");
    }

    [TestMethod]
    public void DryRun_MovesNothing_Apply_MovesOnlyThisModsFiles_Restore_PutsThemBack()
    {
        var script = Built();
        var dir = Folder("device", [.. AllModFiles, ("pc0010_mobile_and.pak", [42]), ("other_mod_P.pak", [43]), ("notes.txt", [44])]);
        var before = Listing(dir);

        var (exit, output) = Run(script, "-Dir", dir);
        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "4 file(s) would move");
        Assert.AreEqual(before, Listing(dir), "a dry run changes nothing");

        (exit, output) = Run(script, "-Dir", dir, "-Apply");
        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "moved 4 file(s)");
        StringAssert.Contains(output, "-Restore -Apply");
        CollectionAssert.AreEquivalent(new[] { "notes.txt", "other_mod_P.pak", "pc0010_mobile_and.pak" }, Directory.GetFiles(dir).Select(Path.GetFileName).ToList());
        foreach (var (name, bytes) in ModFiles)
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(dir, "_disabled", Path.GetFileName(_root), name)));

        (exit, _) = Run(script, "-Dir", dir, "-Restore", "-Apply");
        Assert.AreEqual(0, exit);
        Assert.AreEqual(before, Listing(dir), "restored byte for byte; nothing else touched (an empty _disabled folder may stay)");
    }

    [TestMethod]
    public void AnotherModsContainer_KeepsItsStub_EvenWhenTheStubIsIdentical()
    {
        var script = Built();
        // another mod built into the same container name: the same stub (every pc9999 stub is the same file), its own ucas/utoc
        var dir = Folder("device", ("pc9999_mobile_and_P.pak", ModFiles["pc9999_mobile_and_P.pak"]), ("pc9999_mobile_and_P.ucas", [99, 98]),
            ("pc9999_mobile_and_P.utoc", [97]), ("pc9998_mobile_and_P.pak", ModFiles["pc9998_mobile_and_P.pak"]));
        var (exit, output) = Run(script, "-Dir", dir, "-Apply");
        Assert.AreEqual(0, exit);
        StringAssert.Contains(output, "left alone: pc9999_mobile_and_P.pak, pc9999_mobile_and_P.ucas, pc9999_mobile_and_P.utoc (they move only together");
        CollectionAssert.AreEquivalent(new[] { "pc9999_mobile_and_P.pak", "pc9999_mobile_and_P.ucas", "pc9999_mobile_and_P.utoc" },
            Directory.GetFiles(dir).Select(Path.GetFileName).ToList(), "the other mod's container stays whole");
        Assert.IsTrue(File.Exists(Path.Combine(dir, "_disabled", Path.GetFileName(_root), "pc9998_mobile_and_P.pak")), "this mod's own font pak moved");

        // and a container with one of its files missing stays too
        var partial = Folder("partial", ("pc9999_mobile_and_P.pak", ModFiles["pc9999_mobile_and_P.pak"]), ("pc9999_mobile_and_P.ucas", ModFiles["pc9999_mobile_and_P.ucas"]));
        (_, output) = Run(script, "-Dir", partial, "-Apply");
        StringAssert.Contains(output, "missing: pc9999_mobile_and_P.utoc");
        Assert.AreEqual(2, Directory.GetFiles(partial).Length);
    }

    [TestMethod]
    public void NeverOverwrites_AndRefusesTheStockPaksFolder()
    {
        var script = Built();
        var dir = Folder("device", AllModFiles);
        Mods.WriteAllBytes(Path.Combine(dir, "_disabled", Path.GetFileName(_root), "pc9998_mobile_and_P.pak"), [7, 7, 7]);
        var before = Listing(dir);
        var (exit, output) = Run(script, "-Dir", dir, "-Apply");
        Assert.AreEqual(1, exit, "a file left alone because its target exists");
        StringAssert.Contains(output, "already exists");
        CollectionAssert.AreEqual(new byte[] { 7, 7, 7 }, File.ReadAllBytes(Path.Combine(dir, "_disabled", Path.GetFileName(_root), "pc9998_mobile_and_P.pak")), "not overwritten");
        Assert.IsTrue(File.Exists(Path.Combine(dir, "pc9998_mobile_and_P.pak")));
        Assert.AreNotEqual(before, Listing(dir), "the container group still moved");

        // the stock Paks folder is refused even for a dry run (GameFixture checks its hashes after the run)
        (exit, output) = Run(script, "-Dir", Source.Config.PaksDir);
        Assert.AreEqual(2, exit);
        StringAssert.Contains(output, "is the stock Paks folder");
    }

    [TestMethod]
    public void Generator_Refuses_StockNames_AndFilesThatArentThePins()
    {
        ModFile F(string name) => new(name, 3, new string('a', 64));
        Assert.IsNotNull(DisableScript.Make("zz", [F("pc0010_mobile_and.pak")], [], "C:\\Paks", DateTime.Now).Why);
        Assert.IsNotNull(DisableScript.Make("zz", [F("global.utoc")], [], "C:\\Paks", DateTime.Now).Why);
        Assert.IsNotNull(DisableScript.Make("zz", [F("x_P.pak")], ["X_P.PAK"], "C:\\Paks", DateTime.Now).Why, "a name in the stock folder, any case");
        Assert.IsNotNull(DisableScript.Make("zz", [F("it's_P.pak")], [], "C:\\Paks", DateTime.Now).Why, "a quote in a name");
        Assert.IsNotNull(DisableScript.Make("../zz", [F("x_P.pak")], [], "C:\\Paks", DateTime.Now).Why);
        Assert.IsNotNull(DisableScript.Make("zz", [new ModFile("x_P.pak", 3, "xyz")], [], "C:\\Paks", DateTime.Now).Why);
        Assert.IsNull(DisableScript.Make("zz", [F("x_P.pak")], [], "C:\\Paks", DateTime.Now).Why);

        // a mod whose files aren't what its recipe.json pins gets no script
        foreach (var (name, bytes) in ModFiles) Mods.WriteAllBytes(Path.Combine(_root, "mod", name), bytes);
        var recipe = File.ReadAllText(Path.Combine(RecipeRoot(), "recipes", "C17b.json"));
        Mods.WriteAllText(Path.Combine(_root, UmgEditor.Core.Recipes.RecipeBuild.RecipeCopy), recipe);
        var (script, _, why) = DisableScript.Write(Source.Config, _root, DateTime.Now);
        Assert.IsNull(script);
        StringAssert.Contains(why, "aren't the ones");
        Assert.IsFalse(File.Exists(Path.Combine(_root, DisableScript.FileName)));
    }

    private static string RecipeRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "recipes"))) dir = dir.Parent;
        return dir?.FullName ?? throw new AssertFailedException("recipes\\ not found above the test folder");
    }
}
