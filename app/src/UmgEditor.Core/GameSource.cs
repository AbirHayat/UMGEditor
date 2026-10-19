using System.Security.Cryptography;
using System.Text;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.IO.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.UE4.VirtualFileSystem;

namespace UmgEditor.Core;

/// <summary>Read-only view of the stock containers. Nothing here writes into PaksDir.</summary>
public sealed class GameSource : IDisposable
{
    public GameConfig Config { get; }
    public DefaultFileProvider Provider { get; }

    /// <summary>Changes whenever a container file is added, removed, resized or touched; used to invalidate caches.</summary>
    public string Fingerprint { get; }

    private GameSource(GameConfig config, DefaultFileProvider provider, string fingerprint)
    {
        Config = config;
        Provider = provider;
        Fingerprint = fingerprint;
    }

    public static GameSource Open(GameConfig config)
    {
        if (!Directory.Exists(config.PaksDir)) throw new DirectoryNotFoundException($"Paks folder not found: {config.PaksDir}");

        // GAME_eFootball: CUE4Parse knows eFootball's non-standard package store entries.
        // Ordinal paths: the game ships both ui/library/core and ui/library/Core.
        var provider = new DefaultFileProvider(config.PaksDir, SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_eFootball), StringComparer.Ordinal);
        provider.Initialize();
        provider.SubmitKey(new FGuid(), new FAesKey(config.AesKey)); // stock containers use the zero key GUID
        return new GameSource(config, provider, ComputeFingerprint(config.PaksDir));
    }

    private Dictionary<string, string>? _caseInsensitiveKeys;

    /// <summary>
    /// Provider key for a package or object path. Paths inside assets don't always match the file's letter case
    /// (e.g. "MiniGame/CommonHud" vs "CommonHUD"); the engine resolves them case-insensitively, so this does too.
    /// The exact key wins when it exists (the game ships both ui/library/core and ui/library/Core).
    /// </summary>
    public string ResolveFileKey(string packageOrObjectPath)
    {
        var key = GamePaths.ToFileKey(packageOrObjectPath);
        if (Provider.Files.ContainsKey(key)) return key;
        var map = LazyInitializer.EnsureInitialized(ref _caseInsensitiveKeys, () =>
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in Provider.Files.Keys) d.TryAdd(k, k);
            return d;
        });
        if (map.TryGetValue(key, out var actual)) return actual;

        // cooked data can reference another platform's override folder (seen: Platforms/_Windows); this build ships _Android
        var platforms = key.IndexOf("/Platforms/_", StringComparison.OrdinalIgnoreCase);
        if (platforms >= 0)
        {
            var slash = key.IndexOf('/', platforms + "/Platforms/_".Length);
            var android = key[..platforms] + "/Platforms/_Android" + key[slash..];
            if (map.TryGetValue(android, out actual)) return actual;
        }
        return key;
    }

    /// <summary>Loads a package by /Game path (or object path), resolving letter case like the engine.</summary>
    public CUE4Parse.UE4.Assets.IPackage LoadPackage(string packageOrObjectPath) => Provider.LoadPackage(ResolveFileKey(packageOrObjectPath));

    /// <summary>The raw package chunk the provider serves for this package (for IoStore: the ExportBundleData chunk).</summary>
    public (string FileKey, byte[] Bytes) ReadPackageBytes(string packagePath)
    {
        var key = ResolveFileKey(packagePath);
        if (!Provider.TrySaveAsset(key, out var bytes)) throw new FileNotFoundException($"package not found in the stock containers: {packagePath}", key);
        return (key, bytes);
    }

    /// <summary>Parses package bytes from anywhere (e.g. an edited copy) as if they were this IoStore file.</summary>
    public IoPackage ParsePackage(string fileKey, byte[] bytes)
    {
        if (!Provider.Files.TryGetValue(fileKey, out var file) || file is not FIoStoreEntry entry)
            throw new FileNotFoundException($"not an IoStore package: {fileKey}");
        return new IoPackage(new FByteArchive(fileKey, bytes, Provider.Versions), entry.IoStoreReader.ContainerHeader, (FArchive?) null, null, Provider);
    }

    /// <summary>The 12-byte IoStore chunk id as 24 hex digits (on-disk order), the file name `retoc pack-raw` expects.</summary>
    public string ChunkIdHex(string fileKey)
    {
        if (!Provider.Files.TryGetValue(fileKey, out var file) || file is not FIoStoreEntry entry) return "";
        var id = entry.ChunkId;
        var b = new byte[12];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(b, id.ChunkId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(8), id._chunkIndex);
        b[10] = id._padding;
        b[11] = id.ChunkType;
        return Convert.ToHexString(b).ToLowerInvariant();
    }

    /// <summary>Raw ContainerHeader chunk of the stock container that serves this file (what `retoc unpack-raw` writes as *0000000a).</summary>
    public byte[] ReadContainerHeaderChunk(string fileKey)
    {
        if (!Provider.Files.TryGetValue(fileKey, out var file) || file is not FIoStoreEntry entry)
            throw new FileNotFoundException($"not an IoStore package: {fileKey}");
        var reader = entry.IoStoreReader;
        return reader.Read(new FIoChunkId(reader.TocResource.Header.ContainerId.Id, 0, EIoChunkType.ContainerHeader));
    }

    public string ContainerOf(string path) =>
        Provider.Files.TryGetValue(path, out var file) && file is VfsEntry entry ? entry.Vfs.Name : "?";

    private static string ComputeFingerprint(string paksDir)
    {
        var sb = new StringBuilder();
        foreach (var f in new DirectoryInfo(paksDir).GetFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            sb.Append(f.Name).Append('|').Append(f.Length).Append('|').Append(f.LastWriteTimeUtc.Ticks).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    public void Dispose() => Provider.Dispose();
}
