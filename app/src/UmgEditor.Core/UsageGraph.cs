namespace UmgEditor.Core;

/// <summary>An in-match board the user cares about, identified by its top-level widget package.</summary>
public sealed record Scoreboard(string Label, string PackagePath);

/// <summary>Who imports whom, reversed: answers "which packages (and which scoreboards) use this widget?"</summary>
public sealed class UsageGraph
{
    private const string Tv = "/Game/Assets/ui/Data/Widget/TVGraphics/SwitchAssets_Original/";

    public static readonly IReadOnlyList<Scoreboard> Scoreboards =
    [
        new("Offline scoreboard", Tv + "MatchTime/MatchTime"),
        new("Online scoreboard", Tv + "MatchTimeConnection/MatchTimeConnection"),
        new("Halftime / full-time board", Tv + "MatchScore/MatchScore"),
        new("Stats board", Tv + "MatchStats/MatchStats"),
    ];

    private readonly Dictionary<string, List<string>> _usersOf = new(StringComparer.OrdinalIgnoreCase);

    public UsageGraph(IEnumerable<UiAssetEntry> entries)
    {
        foreach (var e in entries)
        {
            var user = GamePaths.ToPackagePath(e.Path);
            foreach (var imported in e.Imports)
            {
                if (!_usersOf.TryGetValue(imported, out var list)) _usersOf[imported] = list = [];
                list.Add(user);
            }
        }
    }

    public IReadOnlyList<string> DirectUsers(string packagePath) =>
        _usersOf.TryGetValue(packagePath, out var list) ? list : [];

    /// <summary>Every package that uses this one directly or through other widgets.</summary>
    public IReadOnlySet<string> AllUsers(string packagePath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>([packagePath]);
        while (queue.Count > 0)
            foreach (var user in DirectUsers(queue.Dequeue()))
                if (seen.Add(user)) queue.Enqueue(user);
        return seen;
    }

    /// <summary>The scoreboards that show this package: the package itself if it is one, plus every board that uses it.</summary>
    public IReadOnlyList<Scoreboard> ScoreboardsUsing(string packagePath)
    {
        var users = AllUsers(packagePath);
        return Scoreboards.Where(b => b.PackagePath.Equals(packagePath, StringComparison.OrdinalIgnoreCase) || users.Contains(b.PackagePath)).ToList();
    }
}
