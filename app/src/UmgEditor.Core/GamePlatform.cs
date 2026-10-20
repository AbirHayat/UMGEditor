namespace UmgEditor.Core;

/// <summary>Which build the containers belong to: where /Game is mounted and which platform override folder it ships.</summary>
/// <param name="Root">Project folder in the file keys: "PesMobile" or "PesConsole"</param>
/// <param name="OverrideFolder">The build's own Platforms/_X folder: "_Android" or "_Windows"</param>
public sealed record GamePlatform(string Name, string Root, string OverrideFolder)
{
    public static readonly GamePlatform Mobile = new("Mobile", "PesMobile", "_Android");
    public static readonly GamePlatform Pc = new("PC", "PesConsole", "_Windows");

    public static IReadOnlyList<GamePlatform> All { get; } = [Mobile, Pc];

    public string ContentPrefix => Root + "/Content/";
    public string ConfigPrefix => Root + "/Config/";

    /// <summary>"/Platforms/_Android/"</summary>
    public string OverrideMarker => "/Platforms/" + OverrideFolder + "/";

    /// <summary>The platform whose content folder holds most of these file keys; Mobile when none match.</summary>
    public static GamePlatform Detect(IEnumerable<string> fileKeys)
    {
        var counts = All.ToDictionary(p => p, _ => 0);
        foreach (var key in fileKeys)
            foreach (var p in All)
                if (key.StartsWith(p.ContentPrefix, StringComparison.OrdinalIgnoreCase)) counts[p]++;
        var best = counts.MaxBy(c => c.Value);
        return best.Value > 0 ? best.Key : Mobile;
    }
}
