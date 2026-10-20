namespace UmgEditor.Core;

/// <summary>Converts between provider file keys and engine object paths.</summary>
public static class GamePaths
{
    private const string GamePrefix = "/Game/";

    /// <summary>"PesMobile/Content/Assets/x/Y.uasset" (or PesConsole/Content/… on PC) → "/Game/Assets/x/Y"</summary>
    public static string ToPackagePath(string fileKey)
    {
        var noExt = fileKey.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ? fileKey[..^".uasset".Length] : fileKey;
        foreach (var p in GamePlatform.All)
            if (noExt.StartsWith(p.ContentPrefix, StringComparison.OrdinalIgnoreCase)) return GamePrefix + noExt[p.ContentPrefix.Length..];
        return noExt;
    }

    /// <summary>"/Game/Assets/x/Y" (or an object path "/Game/Assets/x/Y.Y_C") → "PesMobile/Content/Assets/x/Y.uasset" (the platform's content folder)</summary>
    public static string ToFileKey(string packageOrObjectPath, GamePlatform? platform = null)
    {
        var package = PackageOf(packageOrObjectPath);
        return package.StartsWith(GamePrefix, StringComparison.OrdinalIgnoreCase)
            ? (platform ?? GamePlatform.Mobile).ContentPrefix + package[GamePrefix.Length..] + ".uasset"
            : package + ".uasset";
    }

    /// <summary>"/Game/a/B.B_C" → "/Game/a/B"; "/Game/a/B.12" (export index form) → "/Game/a/B"</summary>
    public static string PackageOf(string objectPath)
    {
        var slash = objectPath.LastIndexOf('/');
        var dot = objectPath.IndexOf('.', slash + 1);
        return dot < 0 ? objectPath : objectPath[..dot];
    }

    /// <summary>Konami's shared library widgets (uiImage, uiText, uiEmblem…), embedded by thousands of packages.</summary>
    public const string LibraryFolder = "/Game/Assets/ui/library/";

    public static bool IsLibrary(string packagePath) => packagePath.StartsWith(LibraryFolder, StringComparison.OrdinalIgnoreCase);

    public static string ShortName(string path) => path[(path.LastIndexOf('/') + 1)..].Split('.')[0];
}
