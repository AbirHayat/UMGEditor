using System.Security.Cryptography;
using System.Text;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
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
