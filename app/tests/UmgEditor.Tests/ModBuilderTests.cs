using System.Diagnostics;
using UmgEditor.Core;
using UmgEditor.Core.Editing;
using UmgEditor.Core.Modding;
using UmgEditor.Core.Patching;
using static UmgEditor.Tests.GameFixture;

namespace UmgEditor.Tests;

/// <summary>
/// Step 5: ModBuilder (C# port of work/scripts/make_mod.py). Builds go to work\out\mods\test-&lt;id&gt;*, sessions to
/// work\edits\_tests\&lt;id&gt;; both are deleted afterwards.
/// </summary>
[TestClass]
public class ModBuilderTests
{
    private const string Base = Tv + "MatchTime/MatchTimeBase";
    private const string MatchTime = Tv + "MatchTime/MatchTime";
    private const string Container = "pc9999_mobile_and_P";
    private const string MatchTimeChunk = "4ea5d5c18de5157900000002";

    private static WriteGuard Mods => WriteGuard.ForMods(Source.Config);
    private static WriteGuard Edits => new(Source.Config);
    private string _id = null!;
    private string ModName => "test-" + _id;
    private string EditsFolder(string package) => Path.Combine(Edits.Root, "_tests", _id, GamePaths.ShortName(package));

    [TestInitialize]
    public void NewId() => _id = Guid.NewGuid().ToString("N")[..12];

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var dir in Directory.Exists(Mods.Root) ? Directory.GetDirectories(Mods.Root, $"test-{_id}*") : []) Mods.DeleteDirectory(dir);
        Edits.DeleteDirectory(Path.Combine(Edits.Root, "_tests", _id));
        var parent = Path.Combine(Edits.Root, "_tests");
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Edits.DeleteDirectory(parent);
    }

    private static ModBuilder Builder(IRetoc? retoc = null) => new(Source, retoc ?? new RetocProcess(Source.Config.RetocPath));

    private static string Work(params string[] parts) => Path.Combine([Source.Config.WorkDir, .. parts]);

    private static string Need(string path) => File.Exists(path) ? path : throw new AssertInconclusiveException($"{path} not found");

    /// <summary>A saved PatchSession for the package, after the given edits; returns its packable chunk.</summary>
    private ModPackage Saved(string package, Func<WidgetPackageTree, EditSet, EditResult>[] edits)
    {
        var s = PatchSession.Open(Source, package, EditsFolder(package));
        var tree = WidgetTreeReader.Build(Source, package);
        foreach (var e in edits) Assert.IsNull(s.Apply(e(tree, s.Current)));
        var r = s.Save();
        Assert.IsTrue(r.Saved, r.Summary);
        var (chunk, why) = s.ReadSavedChunk();
        Assert.IsNotNull(chunk, why);
        return new ModPackage(package, s.FileKey, s.ChunkIdHex, chunk);
    }

    private ModPackage Canary2() => Saved(MatchTime, [(t, e) => SlotEditor.SetField(t.Find("Mask")!, e, "LayoutData.Offsets.Left", 249)]);
    private ModPackage C3() => Saved(Base, [(t, e) => SlotEditor.SetField(t.Find("Img_Plate")!, e, "LayoutData.Offsets.Right", 100)]);

    private static void AssertRefused(ModResult r, string contains)
    {
        Assert.IsFalse(r.Built, "build must be refused");
        Assert.IsTrue(r.Refusals.Any(x => x.Contains(contains)), $"expected a refusal containing \"{contains}\"; got: {string.Join(" | ", r.Refusals)}");
        Console.WriteLine(string.Join("\n", r.Refusals));
    }

    // ---- header port ----

    [TestMethod]
    public void ContainerHeader_FromStockUtoc_EqualsUnpackRaw_AndRebuildsTheCanary2Header()
    {
        var key = Source.ResolveFileKey(MatchTime);
        var stock = Source.ReadContainerHeaderChunk(key);
        CollectionAssert.AreEqual(File.ReadAllBytes(Need(Work("extract", "unpack_pc1000_mobile_and", "chunks", "6473168f3157cb940000000a"))), stock,
            "CUE4Parse's read of pc1000's ContainerHeader chunk must equal retoc unpack-raw's");
        var header = ContainerHeader.Parse(stock);
        var packageId = BitConverter.ToUInt64(Convert.FromHexString(MatchTimeChunk[..16]));
        var entry = header.Entries[header.PackageIds.ToList().IndexOf(packageId)];
        Console.WriteLine($"pc1000: {header.PackageIds.Count} packages; MatchTime exports {entry.ExportCount}, bundles {entry.ExportBundleCount}, imports {entry.ImportedPackages.Count}");

        var reference = Need(Work("out", "canary2", "raw", "chunks", "4ab91e411434b1fd0000000a"));
        var cid = BitConverter.ToUInt64(Convert.FromHexString("4ab91e411434b1fd"));
        CollectionAssert.AreEqual(File.ReadAllBytes(reference), ContainerHeader.Build(cid, header.NameHashes, [(packageId, entry)]));
        Assert.ThrowsException<InvalidDataException>(() => ContainerHeader.Parse(stock.AsSpan(0, 40)));
    }

    // ---- make_mod.py equivalence ----

    /// <summary>Canary 2 input through make_mod.py and through ModBuilder: every output file byte-identical (and equal to the archived canary 2 mod).</summary>
    [TestMethod]
    public void Canary2_ByteIdenticalToMakeModPy()
    {
        var package = Canary2();
        var chunkFile = Need(Work("out", "canary2", "raw", "chunks", MatchTimeChunk));
        CollectionAssert.AreEqual(File.ReadAllBytes(chunkFile), package.Chunk, "the session's chunk is the canary 2 input");

        var pyOut = Path.Combine(Mods.Root, ModName + "-makemod");
        var py = RunPython(Work("scripts", "make_mod.py"),
            "--chunk", chunkFile, "--path", "../../../" + package.FileKey,
            "--source-header", Need(Work("extract", "unpack_pc1000_mobile_and", "chunks", "6473168f3157cb940000000a")),
            "--stub", Source.Config.ModStubPath, "--name", Container, "--out", pyOut, "--retoc", Source.Config.RetocPath);
        Assert.AreEqual(0, py.Exit, py.Output);

        var ours = Builder().Build(ModName, Container, [package]);
        Assert.IsTrue(ours.Built, string.Join(" | ", ours.Refusals) + "\n" + ours.Log);
        Console.WriteLine(ours.Log);

        var archived = Work("out", "canary2", "mod");
        foreach (var ext in new[] { ".utoc", ".ucas", ".pak" })
        {
            var name = Container + ext;
            var mine = File.ReadAllBytes(Path.Combine(ours.ModDir, name));
            CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(pyOut, "mod", name)), mine, $"{name} differs from make_mod.py's");
            if (File.Exists(Path.Combine(archived, name)))
                CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(archived, name)), mine, $"{name} differs from the archived canary 2 mod");
        }
        // the intermediate raw/ inputs to retoc are the same too (header chunk; the manifest only differs in line endings)
        foreach (var f in Directory.GetFiles(Path.Combine(pyOut, "raw", "chunks")))
            CollectionAssert.AreEqual(File.ReadAllBytes(f), File.ReadAllBytes(Path.Combine(ours.OutDir, "raw", "chunks", Path.GetFileName(f))), Path.GetFileName(f));
    }

    private static (int Exit, string Output) RunPython(string script, params string[] args)
    {
        var psi = new ProcessStartInfo("python") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(script);
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process p;
        try { p = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception) { throw new AssertInconclusiveException("python is not on PATH"); }
        using (p)
        {
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            Console.WriteLine("make_mod.py: " + output.Trim());
            return (p.ExitCode, output);
        }
    }

    // ---- several packages ----

    [TestMethod]
    public void TwoPackages_BuildVerifyAndMountBack()
    {
        var r = Builder().Build(ModName, Container, [C3(), Canary2()]);
        Assert.IsTrue(r.Built, string.Join(" | ", r.Refusals) + "\n" + r.Log);
        Console.WriteLine(r.Log);
        CollectionAssert.AreEqual(new[] { Container + ".pak", Container + ".ucas", Container + ".utoc" }, r.Files.Select(f => f.Name).ToArray());
        var header = ContainerHeader.Parse(File.ReadAllBytes(Directory.GetFiles(Path.Combine(r.OutDir, "raw", "chunks"), "*0000000a").Single()));
        Assert.AreEqual(2, header.PackageIds.Count);
        Assert.AreEqual(2u, header.PackageCount);
        CollectionAssert.AreEqual(header.PackageIds.Order().ToList(), header.PackageIds.ToList(), "ids sorted");
        StringAssert.Contains(File.ReadAllText(Path.Combine(r.OutDir, "build.json")), ModBuilder.TestedRetocSha256);
        Assert.IsFalse(Directory.Exists(Path.Combine(r.OutDir, "probe")));
    }

    // ---- refusals ----

    [TestMethod]
    public void Refuses_TwoEditsOfTheSamePackage()
    {
        var a = C3();
        var b = a with { Chunk = (byte[]) a.Chunk.Clone() };
        AssertRefused(Builder().Build(ModName, Container, [a, b]), "only one edit of a package");
        Assert.IsFalse(Directory.Exists(Path.Combine(Mods.Root, ModName)), "nothing written");
    }

    [TestMethod]
    public void Refuses_AnUnreadableContainerHeader()
    {
        var p = C3();
        var builder = Builder();
        builder.HeaderSource = _ => new byte[10];
        AssertRefused(builder.Build(ModName, Container, [p]), "the container header of pc1000_mobile_and.utoc can't be read");
        builder.HeaderSource = _ => throw new IOException("read error");
        AssertRefused(builder.Build(ModName, Container, [p]), "can't be read (read error)");
        // readable, but the package isn't in it
        builder.HeaderSource = _ => ContainerHeader.Build(1, [0, 0, 0x64, 0xC1, 0, 0, 0, 0], [(42ul, new StoreEntry(1, 1, []))]);
        AssertRefused(builder.Build(ModName, Container, [p]), "is not in the container header");
        Assert.IsFalse(Directory.Exists(Path.Combine(Mods.Root, ModName)), "nothing written");
    }

    [TestMethod]
    public void Refuses_BadNamesAndForeignChunkIds()
    {
        var p = C3();
        foreach (var name in new[] { "..", "a/b", @"..\..\x", "" })
            AssertRefused(Builder().Build(name, Container, [p]), "plain folder name");
        AssertRefused(Builder().Build(ModName, "pc9999_mobile_and", [p]), "ending in _P");
        AssertRefused(Builder().Build(ModName, Container, [p with { ChunkIdHex = MatchTimeChunk }]), "doesn't match the stock file's");
    }

    /// <summary>retoc verify fails → the export aborts, mod\ is removed, and nothing under Paks is touched.</summary>
    [TestMethod]
    public void FailedVerify_AbortsTheExport_AndDeletesNothingInPaks()
    {
        var p = C3();
        var paks = Source.Config.PaksDir;
        var before = new DirectoryInfo(paks).GetFiles().Select(f => (f.Name, f.Length, f.LastWriteTimeUtc)).OrderBy(x => x.Name).ToList();
        var r = Builder(new FailingVerify(new RetocProcess(Source.Config.RetocPath))).Build(ModName, Container, [p]);
        AssertRefused(r, "retoc verify failed");
        Assert.IsFalse(Directory.Exists(r.ModDir), "a half-built mod must not be left to copy");
        Assert.IsTrue(File.Exists(Path.Combine(r.OutDir, "FAILED.txt")));
        CollectionAssert.AreEqual(before, new DirectoryInfo(paks).GetFiles().Select(f => (f.Name, f.Length, f.LastWriteTimeUtc)).OrderBy(x => x.Name).ToList());
    }

    private sealed class FailingVerify(IRetoc inner) : IRetoc
    {
        public string ExePath => inner.ExePath;
        public RetocResult Run(params string[] args) => args[0] == "verify" ? new RetocResult(1, "", "simulated verify failure") : inner.Run(args);
    }

    [TestMethod]
    public void ModsGuard_WritesOnlyUnderOutMods()
    {
        var g = Mods;
        StringAssert.EndsWith(g.Root, @"work\out\mods");
        StringAssert.Contains(g.Refusal(Path.Combine(Source.Config.PaksDir, Container + ".utoc")), "under the game Paks folder");
        Assert.IsNotNull(g.Refusal(Path.Combine(Edits.Root, "x.bin")), "the mods writer can't write into edits");
        Assert.IsNotNull(g.Refusal(Work("out", "canary2", "mod", "x.bin")), "nor elsewhere under work\\out");
        Assert.IsNull(g.Refusal(Path.Combine(g.Root, "C1", "mod", Container + ".utoc")));
    }

    // ---- saved-chunk gate ----

    [TestMethod]
    public void ReadSavedChunk_RefusesUnsavedMissingAndChangedSaves()
    {
        var folder = EditsFolder(Base);
        var s = PatchSession.Open(Source, Base, folder);
        StringAssert.Contains(s.ReadSavedChunk().Refusal, "no saved edits");
        var plate = WidgetTreeReader.Build(Source, Base).Find("Img_Plate")!;
        Assert.IsNull(s.Apply(SlotEditor.SetField(plate, s.Current, "LayoutData.Offsets.Right", 100)));
        StringAssert.Contains(s.ReadSavedChunk().Refusal, "unsaved edits");
        Assert.IsTrue(s.Save().Saved);
        Assert.IsNotNull(s.ReadSavedChunk().Chunk);

        var bytes = File.ReadAllBytes(s.EditedPath);
        bytes[^1] ^= 1;
        File.WriteAllBytes(s.EditedPath, bytes);
        StringAssert.Contains(s.ReadSavedChunk().Refusal, "differs from a rebuild");
        StringAssert.Contains(PatchSession.Open(Source, Base, folder).ReadSavedChunk().Refusal, "doesn't match its manifest");
    }
}
