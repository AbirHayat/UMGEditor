namespace UmgEditor.Core;

/// <summary>Converts between provider file keys and engine object paths.</summary>
public static class GamePaths
{
    private const string ContentPrefix = "PesMobile/Content/";
    private const string GamePrefix = "/Game/";

    /// <summary>"PesMobile/Content/Assets/x/Y.uasset" → "/Game/Assets/x/Y"</summary>
    public static string ToPackagePath(string fileKey)
    {
        var noExt = fileKey.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) ? fileKey[..^".uasset".Length] : fileKey;
        return noExt.StartsWith(ContentPrefix, StringComparison.OrdinalIgnoreCase) ? GamePrefix + noExt[ContentPrefix.Length..] : noExt;
    }

    /// <summary>"/Game/Assets/x/Y" (or an object path "/Game/Assets/x/Y.Y_C") → "PesMobile/Content/Assets/x/Y.uasset"</summary>
    public static string ToFileKey(string packageOrObjectPath)
    {
        var package = PackageOf(packageOrObjectPath);
        return package.StartsWith(GamePrefix, StringComparison.OrdinalIgnoreCase)
            ? ContentPrefix + package[GamePrefix.Length..] + ".uasset"
            : package + ".uasset";
    }

    /// <summary>"/Game/a/B.B_C" → "/Game/a/B"; "/Game/a/B.12" (export index form) → "/Game/a/B"</summary>
    public static string PackageOf(string objectPath)
    {
        var slash = objectPath.LastIndexOf('/');
        var dot = objectPath.IndexOf('.', slash + 1);
        return dot < 0 ? objectPath : objectPath[..dot];
    }

    public static string ShortName(string path) => path[(path.LastIndexOf('/') + 1)..].Split('.')[0];
}
