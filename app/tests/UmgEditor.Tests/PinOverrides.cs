using System.Text.Json;

namespace UmgEditor.Tests;

/// <summary>
/// recipes\pins\changed-since-device-test.json: pins that deliberately differ from device-tested.json (which stays the
/// record of what was on the phone). A recipe of a device-tested canary pins device-tested.json with these replacements.
/// </summary>
internal static class PinOverrides
{
    public sealed record Change(string Was, string Now);
    public sealed record Canary(string Date, string Why, Dictionary<string, Change> Chunks, Dictionary<string, Change> Files);
    private sealed record File_(string About, Dictionary<string, Canary> Changes);

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "recipes"))) dir = dir.Parent;
        return dir?.FullName ?? throw new AssertFailedException("recipes\\ not found above the test folder");
    }

    public static Dictionary<string, Canary> Load() =>
        JsonSerializer.Deserialize<File_>(File.ReadAllText(Path.Combine(RepoRoot(), "recipes", "pins", "changed-since-device-test.json")))!.Changes;

    /// <summary>The changed chunks of one canary (package → new sha256), empty when it has none.</summary>
    public static IReadOnlyDictionary<string, string> ChunksOf(string canary) =>
        Load().TryGetValue(canary, out var c) ? c.Chunks.ToDictionary(kv => kv.Key, kv => kv.Value.Now) : new Dictionary<string, string>();
}
