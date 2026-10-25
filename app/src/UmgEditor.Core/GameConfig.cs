using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace UmgEditor.Core;

/// <summary>Where the stock game files live and where the editor may write. Read from %APPDATA%\UmgEditor\umgeditor.json
/// (user-wide; it holds the AES key, which is never in the repo), then umgeditor.json next to the executable (overrides).</summary>
public sealed record GameConfig
{
    public string PaksDir { get; init; } = @"E:\n3tron\patch\obb\mobile\org\all\PesMobile\Content\Paks";
    /// <summary>The game's AES key; empty unless a settings file gives it (see <see cref="UserSettingsPath"/>).</summary>
    public string AesKey { get; init; } = "";
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

    /// <summary>The user-wide settings file: %APPDATA%\UmgEditor\umgeditor.json.</summary>
    public static string UserSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UmgEditor", "umgeditor.json");

    /// <summary>Defaults, then the user-wide file, then the file at <paramref name="path"/>: each field a later file
    /// names replaces the earlier value.</summary>
    public static GameConfig LoadOrDefault(string path)
    {
        var merged = new JsonObject();
        foreach (var file in new[] { UserSettingsPath, path })
            if (Read(file) is { } settings)
                foreach (var (name, value) in settings) merged[name] = value?.DeepClone();
        return merged.Deserialize<GameConfig>(JsonOptions) ?? new GameConfig();
    }

    /// <summary>Writes every setting to <paramref name="path"/> except the AES key, unless that file already held one
    /// (the key stays in the user-wide file and isn't copied next to an executable).</summary>
    public void Save(string path)
    {
        var settings = JsonSerializer.SerializeToNode(this, JsonOptions)!.AsObject();
        if (Read(path)?.ContainsKey(nameof(AesKey)) != true) settings.Remove(nameof(AesKey));
        File.WriteAllText(path, settings.ToJsonString(JsonOptions));
    }

    private static JsonObject? Read(string file) => File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject : null;
}
