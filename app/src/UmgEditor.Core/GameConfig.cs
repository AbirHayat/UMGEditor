using System.Text.Json;
using System.Text.Json.Serialization;

namespace UmgEditor.Core;

/// <summary>Where the stock game files live and where the editor may write. Stored as umgeditor.json next to the executable.</summary>
public sealed record GameConfig
{
    public string PaksDir { get; init; } = @"E:\n3tron\patch\obb\mobile\org\all\PesMobile\Content\Paks";
    public string AesKey { get; init; } = "0x4552D45005DFE94964893F4925EC747D3D591401E060ED8B3D58BE5721C81295";
    public string WorkDir { get; init; } = @"E:\n3tron\ai_tools\scoreboard\work";
    /// <summary>Folders mounted on top of PaksDir (read-only), e.g. a mod's ~mods folder; empty = stock only.</summary>
    public IReadOnlyList<string> ExtraPaksDirs { get; init; } = [];

    /// <summary>retoc used to pack mods (pack-raw, verify); see app/README.md for the tested build.</summary>
    public string RetocPath { get; init; } = @"E:\n3tron\patch\Tools\retoc-x86_64-pc-windows-msvc\retoc.exe";
    /// <summary>Stock .pak mount stub copied next to each mod container (a file name in PaksDir, or a full path; only read).</summary>
    public string ModStubPak { get; init; } = "pc0100_mobile_and.pak";

    [JsonIgnore] public string CacheDir => Path.Combine(WorkDir, "cache");
    [JsonIgnore] public string EditsDir => Path.Combine(WorkDir, "edits");
    [JsonIgnore] public string ModsDir => Path.Combine(WorkDir, "out", "mods");
    [JsonIgnore] public string ModStubPath => Path.IsPathRooted(ModStubPak) ? ModStubPak : Path.Combine(PaksDir, ModStubPak);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static GameConfig LoadOrDefault(string path)
    {
        if (!File.Exists(path)) return new GameConfig();
        return JsonSerializer.Deserialize<GameConfig>(File.ReadAllText(path), JsonOptions) ?? new GameConfig();
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
}
