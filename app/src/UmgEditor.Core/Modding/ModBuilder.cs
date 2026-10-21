using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using UmgEditor.Core.Patching;

namespace UmgEditor.Core.Modding;

/// <summary>One patched package to ship: its stock file key, ExportBundleData chunk id and the patched chunk bytes.</summary>
public sealed record ModPackage(string PackagePath, string FileKey, string ChunkIdHex, byte[] Chunk)
{
    /// <summary>
    /// A package that isn't in the stock containers (branch experiment, C13: a cloned texture): the stock file it was
    /// made from, whose store entry the mod's container header copies. Its chunk id must be FPackageId of PackagePath.
    /// </summary>
    public string? CloneOf { get; init; }

    /// <summary>
    /// Imported packages swapped in this package's graph data (branch experiment, C16: ClassSwap), applied to the store
    /// entry copied into the mod's container header too: each (old, new) package id, old must be in the stock entry.
    /// </summary>
    public IReadOnlyList<(ulong Old, ulong New)> ImportedPackageSwaps { get; init; } = [];
}

public sealed record ModFile(string Name, long Size, string Sha256);

/// <summary>Outcome of a build. When not built, Refusals says why; nothing under PaksDir is ever written or deleted.</summary>
public sealed record ModResult(bool Built, IReadOnlyList<string> Refusals, string OutDir, IReadOnlyList<ModFile> Files, string Log)
{
    public string ModDir => Path.Combine(OutDir, "mod");
}

/// <summary>
/// C# port of work/scripts/make_mod.py (kept as the reference), extended to several packages. Steps, in make_mod order:
/// 1. raw/chunks/&lt;chunk id&gt; per package + raw/manifest.json (DirectoryIndex, mount point ../../../);
/// 2. `retoc pack-raw` once without a header into probe/ to learn the container id retoc derives from the name;
/// 3. the eFootball container header: each package's store entry copied from the stock container header that serves
///    it (read from the stock .utoc via CUE4Parse), NameHashes from the source header; parsed back and compared;
/// 4. `retoc pack-raw` into mod/&lt;container&gt;.utoc/.ucas, the stock .pak stub copied as mod/&lt;container&gt;.pak;
/// 5. `retoc verify`; then (beyond make_mod) CUE4Parse mounts mod/ and must read back every chunk byte for byte.
/// A failure at 4–5 aborts: mod/ is emptied so a half-built mod can't be copied (raw/ stays for diagnosis).
/// Everything is written through WriteGuard.ForMods (only under work\out\mods\&lt;name&gt;).
/// </summary>
public sealed class ModBuilder(GameSource source, IRetoc retoc)
{
    /// <summary>sha256 of the retoc.exe the canaries and the make_mod.py comparison were built with (see app/README.md).</summary>
    public const string TestedRetocSha256 = "1587f00d57c5046dc51dbc5c0e39f65d937a57943cac1db4ce059335d3a1e3a4";

    /// <summary>Stock container header chunk for a file key (default: read from the stock .utoc). A seam for tests.</summary>
    internal Func<string, byte[]> HeaderSource { get; set; } = source.ReadContainerHeaderChunk;

    private static readonly Regex SimpleName = new("^[A-Za-z0-9][A-Za-z0-9_.-]*$");
    private static readonly Regex ContainerName = new("^[A-Za-z0-9]+(_[A-Za-z0-9]+)*_P$");

    /// <summary>Why a mod name can't be used as its folder under work\out\mods, or null.</summary>
    public static string? ModNameRefusal(string modName) =>
        !SimpleName.IsMatch(modName) || modName.Contains("..") ? $"mod name \"{modName}\" must be a plain folder name (letters, digits, _ . -)" : null;

    /// <summary>Why a container name can't be used (it must end in _P, the patch suffix the game mounts last), or null.</summary>
    public static string? ContainerNameRefusal(string containerName) =>
        !ContainerName.IsMatch(containerName) ? $"container name \"{containerName}\" must look like pc9999_mobile_and_P (letters/digits/underscores, ending in _P)" : null;

    public ModResult Build(string modName, string containerName, IReadOnlyList<ModPackage> packages)
    {
        var guard = WriteGuard.ForMods(source.Config);
        var outDir = Path.Combine(guard.Root, modName);
        var log = new StringBuilder();
        var refusals = new List<string>();
        ModResult Fail(params string[] why) { refusals.AddRange(why); return new ModResult(false, refusals, outDir, [], log.ToString()); }

        // ---- checks that need no writes ----
        if (ModNameRefusal(modName) is { } badName) return Fail(badName);
        if (ContainerNameRefusal(containerName) is { } badContainer) return Fail(badContainer);
        if (guard.Refusal(outDir) is { } bad) return Fail(bad);
        if (packages.Count == 0) return Fail("no packages to pack");
        foreach (var dup in packages.GroupBy(p => p.FileKey, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            refusals.Add($"{dup.Key} is given {dup.Count()} times; one mod can carry only one edit of a package (merge the edits into one session)");
        foreach (var dup in packages.GroupBy(p => p.ChunkIdHex, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1 && g.Select(p => p.FileKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
            refusals.Add($"chunk id {dup.Key} is used by {string.Join(", ", dup.Select(p => p.FileKey))}");
        if (refusals.Count > 0) return Fail();

        var stub = source.Config.ModStubPath;
        if (!File.Exists(stub)) return Fail($"the .pak stub {stub} doesn't exist (setting ModStubPak)");
        if (!File.Exists(retoc.ExePath)) return Fail($"retoc not found at {retoc.ExePath} (setting RetocPath)");

        var sources = new List<(ModPackage Package, ulong PackageId, ContainerHeader Header, StoreEntry Entry)>();
        foreach (var p in packages)
        {
            var id = p.ChunkIdHex.ToLowerInvariant();
            if (id.Length != 24 || !id.All(Uri.IsHexDigit) || !id.EndsWith("0002")) { refusals.Add($"{p.FileKey}: \"{p.ChunkIdHex}\" is not an ExportBundleData chunk id (24 hex digits ending in 0002)"); continue; }
            var stockId = source.ChunkIdHex(p.FileKey);
            if (p.CloneOf is { } cloneOf)
            {
                if (stockId.Length > 0) { refusals.Add($"{p.FileKey}: a new package can't use the path of a stock file"); continue; }
                var expected = BitConverter.GetBytes(CUE4Parse.UE4.IO.Objects.FPackageId.FromName(p.PackagePath).id);
                if (Convert.ToHexString(expected).ToLowerInvariant() + "00000002" != id) { refusals.Add($"{p.FileKey}: chunk id {id} isn't the package id of {p.PackagePath}"); continue; }
            }
            else if (stockId != id) { refusals.Add($"{p.FileKey}: chunk id {id} doesn't match the stock file's {(stockId.Length == 0 ? "(not an IoStore file)" : stockId)}"); continue; }
            if (p.Chunk.Length == 0) { refusals.Add($"{p.FileKey}: empty chunk"); continue; }
            var packageId = BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString(id[..16])); // chunk id = package id (LE) + index 0 + type 2

            // the store entry comes from the stock package (for a clone: the one it was made from)
            var entryKey = p.CloneOf ?? p.FileKey;
            var entryId = p.CloneOf is { } src ? source.ChunkIdHex(src) : id;
            if (entryId.Length != 24) { refusals.Add($"{p.FileKey}: the clone's source {p.CloneOf} is not a stock IoStore file"); continue; }
            var container = source.ContainerOf(entryKey);
            ContainerHeader header;
            try { header = ContainerHeader.Parse(HeaderSource(entryKey)); }
            catch (Exception e) { refusals.Add($"{p.FileKey}: the container header of {container} can't be read ({e.Message})"); continue; }
            var index = header.PackageIds.ToList().IndexOf(BinaryPrimitives.ReadUInt64LittleEndian(Convert.FromHexString(entryId[..16])));
            if (index < 0) { refusals.Add($"{p.FileKey}: package id {entryId[..16]} is not in the container header of {container}"); continue; }
            var storeEntry = header.Entries[index];
            if (p.ImportedPackageSwaps.Count > 0)
            {
                var imported = storeEntry.ImportedPackages.ToList();
                var unswappable = p.ImportedPackageSwaps.Where(s => imported.Count(x => x == s.Old) != 1 || imported.Contains(s.New)).ToList();
                if (unswappable.Count > 0) { refusals.Add($"{p.FileKey}: the store entry can't take the imported package swaps {string.Join(", ", unswappable.Select(b => $"{b.Old:x16}→{b.New:x16}"))}"); continue; }
                foreach (var (o, n) in p.ImportedPackageSwaps) imported[imported.IndexOf(o)] = n;
                if (storeEntry.ImportedPackages.SequenceEqual(storeEntry.ImportedPackages.Order())) imported.Sort(); // stock keeps them sorted
                storeEntry = storeEntry with { ImportedPackages = imported };
                log.AppendLine($"{p.FileKey}: store entry imported packages swapped: {string.Join(", ", p.ImportedPackageSwaps.Select(x => $"{x.Old:x16}→{x.New:x16}"))}");
            }
            sources.Add((p, packageId, header, storeEntry));
        }
        if (refusals.Count > 0) return Fail();
        var nameHashes = sources[0].Header.NameHashes;
        if (sources.Any(s => !s.Header.NameHashes.AsSpan().SequenceEqual(nameHashes)))
            return Fail("the source container headers have different NameHashes; packages from them can't share one mod header");

        // ---- build (make_mod order) ----
        var raw = Path.Combine(outDir, "raw");
        var chunks = Path.Combine(raw, "chunks");
        var mod = Path.Combine(outDir, "mod");
        var probe = Path.Combine(outDir, "probe");
        guard.DeleteDirectory(outDir);
        guard.CreateDirectory(chunks);
        guard.CreateDirectory(mod);

        var ordered = sources.OrderBy(s => s.PackageId).ToList();
        foreach (var s in ordered)
        {
            guard.WriteAllBytes(Path.Combine(chunks, s.Package.ChunkIdHex.ToLowerInvariant()), s.Package.Chunk);
            log.AppendLine($"chunk {s.Package.ChunkIdHex.ToLowerInvariant()}  {s.Package.Chunk.Length} bytes  {Sha256(s.Package.Chunk)}  {s.Package.FileKey}");
        }
        guard.WriteAllText(Path.Combine(raw, "manifest.json"), Manifest(ordered.Select(s => (s.Package.ChunkIdHex.ToLowerInvariant(), "../../../" + s.Package.FileKey))));

        // the container id comes from the name; pack once without a header to learn it
        var probeUtoc = Path.Combine(probe, containerName + ".utoc");
        guard.CreateDirectory(probe);
        var probeRun = retoc.Run("pack-raw", raw, guard.Check(probeUtoc));
        if (!probeRun.Ok) return Abort($"retoc pack-raw (probe) failed: {probeRun.Output}");
        var containerId = BinaryPrimitives.ReadUInt64LittleEndian(File.ReadAllBytes(probeUtoc).AsSpan(0x38, 8));
        guard.DeleteDirectory(probe);
        log.AppendLine($"container id {containerId:x16} (from retoc for \"{containerName}\")");

        var headerBytes = ContainerHeader.Build(containerId, nameHashes, ordered.Select(s => (s.PackageId, s.Entry)).ToList());
        var check = ContainerHeader.Parse(headerBytes);
        if (check.ContainerId != containerId || !check.PackageIds.SequenceEqual(ordered.Select(s => s.PackageId)) || !check.Entries.Zip(ordered).All(x => x.First.SameAs(x.Second.Entry)))
            return Abort("the built container header doesn't parse back to its inputs");
        var cidHex = Convert.ToHexString(BitConverter.GetBytes(containerId)).ToLowerInvariant(); // LE bytes, as in make_mod
        guard.WriteAllBytes(Path.Combine(chunks, cidHex + "0000000a"), headerBytes);
        log.AppendLine($"header {headerBytes.Length} bytes, {ordered.Count} package{(ordered.Count == 1 ? "" : "s")}: {string.Join(", ", ordered.Select(s => $"{s.PackageId:x16} (exports {s.Entry.ExportCount}, bundles {s.Entry.ExportBundleCount}, imports {s.Entry.ImportedPackages.Count})"))}");

        var utoc = Path.Combine(mod, containerName + ".utoc");
        var pack = retoc.Run("pack-raw", raw, guard.Check(utoc));
        if (!pack.Ok) return Abort($"retoc pack-raw failed: {pack.Output}");
        guard.CopyFrom(stub, Path.Combine(mod, containerName + ".pak"));

        var verify = retoc.Run("verify", guard.Check(utoc));
        log.AppendLine($"retoc verify: {verify.Output}");
        if (!verify.Ok) return Abort($"retoc verify failed (exit {verify.ExitCode}): {verify.Output}");

        if (MountCheck(mod, ordered.Select(s => s.Package).ToList()) is { } mountProblem) return Abort(mountProblem);

        var files = new DirectoryInfo(mod).GetFiles().OrderBy(f => f.Name, StringComparer.Ordinal)
            .Select(f => new ModFile(f.Name, f.Length, Sha256(File.ReadAllBytes(f.FullName)))).ToList();
        foreach (var f in files) log.AppendLine($"{f.Sha256}  {f.Name}  ({f.Size} bytes)");
        guard.WriteAllText(Path.Combine(outDir, "build.json"), JsonSerializer.Serialize(new
        {
            Mod = modName,
            Container = containerName,
            BuiltUtc = DateTime.UtcNow,
            Retoc = new { Path = retoc.ExePath, Sha256 = RetocProcess.Sha256Of(retoc.ExePath) },
            Stub = new { Path = stub, Sha256 = Sha256(File.ReadAllBytes(stub)) },
            ContainerId = $"{containerId:x16}",
            Packages = ordered.Select(s => new { s.Package.PackagePath, s.Package.FileKey, ChunkId = s.Package.ChunkIdHex, PackageId = $"{s.PackageId:x16}", SourceContainer = source.ContainerOf(s.Package.CloneOf ?? s.Package.FileKey), s.Package.CloneOf, ChunkSha256 = Sha256(s.Package.Chunk) }),
            Verify = verify.Output,
            Files = files,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return new ModResult(true, [], outDir, files, log.ToString());

        ModResult Abort(string why)
        {
            // empty mod/ so nothing half-built can be installed; raw/ stays for diagnosis. Only under work\out\mods.
            guard.DeleteDirectory(mod);
            guard.DeleteDirectory(probe);
            guard.WriteAllText(Path.Combine(outDir, "FAILED.txt"), why + Environment.NewLine + log);
            log.AppendLine("ABORTED: " + why);
            return Fail(why);
        }
    }

    /// <summary>Mounts the built container with CUE4Parse (as the game would: eFootball header, .pak stub) and reads every chunk back.</summary>
    private string? MountCheck(string modDir, IReadOnlyList<ModPackage> packages)
    {
        try
        {
            using var provider = new DefaultFileProvider(modDir, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_eFootball), StringComparer.Ordinal);
            provider.Initialize();
            provider.SubmitKey(new FGuid(), new FAesKey(source.Config.AesKey));
            foreach (var p in packages)
            {
                if (!provider.TrySaveAsset(p.FileKey, out var back)) return $"mount check: {p.FileKey} is not in the built container";
                if (!back.AsSpan().SequenceEqual(p.Chunk)) return $"mount check: {p.FileKey} reads back different bytes from the built container";
            }
            return null;
        }
        catch (Exception e)
        {
            return $"mount check: CUE4Parse can't read the built container ({e.GetType().Name}: {e.Message})";
        }
    }

    /// <summary>The manifest make_mod.py writes (json.dumps indent=2), byte for byte.</summary>
    private static string Manifest(IEnumerable<(string ChunkId, string Path)> chunks)
    {
        var sb = new StringBuilder("{\n  \"chunk_paths\": {\n");
        sb.Append(string.Join(",\n", chunks.Select(c => $"    {JsonSerializer.Serialize(c.ChunkId)}: {JsonSerializer.Serialize(c.Path)}")));
        sb.Append("\n  },\n  \"version\": \"DirectoryIndex\",\n  \"mount_point\": \"../../../\"\n}");
        return sb.ToString();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
