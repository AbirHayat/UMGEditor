using System.Text.Json;

namespace UmgEditor.Core;

/// <summary>Where the stock game files live and where the editor may write. Stored as umgeditor.json next to the executable.</summary>
public sealed record GameConfig
{
    public string PaksDir { get; init; } = @"E:\n3tron\patch\obb\mobile\org\all\PesMobile\Content\Paks";
    public string AesKey { get; init; } = "0x4552D45005DFE94964893F4925EC747D3D591401E060ED8B3D58BE5721C81295";
    public string WorkDir { get; init; } = @"E:\n3tron\ai_tools\scoreboard\work";

    public string CacheDir => Path.Combine(WorkDir, "cache");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static GameConfig LoadOrDefault(string path)
    {
        if (!File.Exists(path)) return new GameConfig();
        return JsonSerializer.Deserialize<GameConfig>(File.ReadAllText(path), JsonOptions) ?? new GameConfig();
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}
