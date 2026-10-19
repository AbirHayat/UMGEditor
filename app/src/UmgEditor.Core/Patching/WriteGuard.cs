namespace UmgEditor.Core.Patching;

/// <summary>
/// The only gate for files the editor writes: every path must resolve under WorkDir\edits, and nothing may resolve
/// under the game's Paks folder (also when WorkDir itself is configured inside it). Junctions/symlinks between the
/// edits root and the target are refused, so a link can't redirect a write into the game files.
/// </summary>
public sealed class WriteGuard
{
    public string EditsRoot { get; }
    public string PaksDir { get; }

    public WriteGuard(GameConfig config)
    {
        EditsRoot = Normalize(Path.Combine(config.WorkDir, "edits"));
        PaksDir = Normalize(config.PaksDir);
    }

    /// <summary>Why this path may not be written, or null when it may.</summary>
    public string? Refusal(string path)
    {
        string full;
        try { full = Normalize(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return $"invalid path \"{path}\": {e.Message}"; }

        if (IsUnder(full, PaksDir)) return $"{full} is under the game Paks folder ({PaksDir}); the editor never writes there.";
        if (!IsUnder(full, EditsRoot) || full.Length == EditsRoot.Length) return $"{full} is outside {EditsRoot}; the editor only writes under work\\edits.";
        for (var dir = Path.GetDirectoryName(full); dir != null && dir.Length >= EditsRoot.Length; dir = Path.GetDirectoryName(dir))
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
