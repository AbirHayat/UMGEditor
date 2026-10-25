namespace UmgEditor.Core.Patching;

/// <summary>
/// The only gate for files the editor writes: every path must resolve under one root (WorkDir\edits for edit
/// sessions, WorkDir\out\mods for built mods), and nothing may resolve under the game's Paks folder (also when
/// WorkDir itself is configured inside it). Junctions/symlinks between the root and the target are refused, so a
/// link can't redirect a write into the game files.
/// </summary>
public sealed class WriteGuard
{
    public string Root { get; }
    public string PaksDir { get; }

    /// <summary>Guard for edit sessions: WorkDir\edits.</summary>
    public WriteGuard(GameConfig config) : this(config, config.EditsDir) { }

    private WriteGuard(GameConfig config, string root)
    {
        Root = Normalize(root);
        PaksDir = Normalize(config.PaksDir);
    }

    /// <summary>Guard for built mods: WorkDir\out\mods.</summary>
    public static WriteGuard ForMods(GameConfig config) => new(config, config.ModsDir);

    /// <summary>Guard for recipes written by umgmod recipe-from-set: WorkDir\out\recipes.</summary>
    public static WriteGuard ForRecipes(GameConfig config) => new(config, Path.Combine(config.WorkDir, "out", "recipes"));

    /// <summary>Why this path may not be written, or null when it may.</summary>
    public string? Refusal(string path)
    {
        string full;
        try { full = Normalize(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return $"invalid path \"{path}\": {e.Message}"; }

        if (IsUnder(full, PaksDir)) return $"{full} is under the game Paks folder ({PaksDir}); the editor never writes there.";
        if (!IsUnder(full, Root) || full.Length == Root.Length) return $"{full} is outside {Root}; this writer only writes below that folder.";
        for (var dir = Path.GetDirectoryName(full); dir != null && dir.Length >= Root.Length; dir = Path.GetDirectoryName(dir))
            if (Directory.Exists(dir) && new DirectoryInfo(dir).LinkTarget != null) return $"{dir} is a link; writes through links aren't allowed.";
        if (File.Exists(full) && new FileInfo(full).LinkTarget != null) return $"{full} is a link; writes through links aren't allowed.";
        return null;
    }

    /// <summary>The full path, or throws UnauthorizedAccessException with the reason.</summary>
    public string Check(string path) => Refusal(path) is { } why ? throw new UnauthorizedAccessException(why) : Normalize(path);

    public void WriteAllBytes(string path, byte[] bytes)
    {
        var full = Check(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    public void WriteAllText(string path, string text)
    {
        var full = Check(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary>Copies a file (the source may be anywhere, e.g. a stock .pak stub, and is only read).</summary>
    public void CopyFrom(string source, string to)
    {
        var full = Check(to);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.Copy(source, full, overwrite: true);
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(Check(path));

    public void Move(string from, string to) => File.Move(Check(from), Check(to), overwrite: true);

    public void Delete(string path)
    {
        var full = Check(path);
        if (File.Exists(full)) File.Delete(full);
    }

    public void DeleteDirectory(string path)
    {
        var full = Check(path);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsUnder(string full, string dir) =>
        full.Equals(dir, StringComparison.OrdinalIgnoreCase) ||
        full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
