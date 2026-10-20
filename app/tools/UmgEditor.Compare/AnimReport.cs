using System.Text;
using UmgEditor.Core;
using UmgEditor.Core.Animation;

/// <summary>
/// Step 4b/4c: every animation of the scoreboard family (MatchTime folder), described per widget channel, on mobile,
/// PC stock and PC with a mod on top (the UCL theme), side by side. Read-only; writes one markdown report.
/// </summary>
static class AnimReport
{
    public sealed record Side(string Name, GameSource Source);

    private sealed record Row(string Widget, string Channel, string Track);

    public static string Build(IReadOnlyList<string> packages, IReadOnlyList<Side> sides, IProgress<string>? log = null)
    {
        var sb = new StringBuilder();
        void L(string s = "") => sb.AppendLine(s);
        L("# Scoreboard animations: " + string.Join(" vs ", sides.Select(s => s.Name)));
        L();
        L("Every animation of the MatchTime folder, one row per widget channel that changes something (translation 0, scale 1, angle 0, shear 0 and");
        L("channels without keys are left out). Units are the widget's local design units (1920-wide canvas); times are seconds from the animation's start.");
        L("`(slot)` = the track drives the widget's slot (LayoutData.Offsets), so it moves the layout box; other tracks move the rendered widget on top of it.");
        L("Visibility: *shown* = Visible/HitTestInvisible/SelfHitTestInvisible, *hidden* = Collapsed/Hidden. `= X` = same as column X; `—` = no such track there.");
        L("A segment's curve is the interp mode of the key it starts at: cubic eases, linear is even speed, *jump* = constant (the value holds, then steps).");
        L();
        L("| package | animations | " + string.Join(" | ", sides.Skip(1).Select((s, i) => $"{s.Name} differs from {sides[i].Name}")) + " |");
        L("|---|---|" + string.Concat(sides.Skip(1).Select(_ => "---|")));
        var sections = new StringBuilder();
        foreach (var package in packages)
        {
            log?.Report(GamePaths.ShortName(package));
            var read = sides.Select(s =>
            {
                try { return AnimationReader.Read(s.Source, package).ToDictionary(a => a.Name); }
                catch (Exception e) when (e is KeyNotFoundException or FileNotFoundException or InvalidDataException or ArgumentException) { return null; }
            }).ToList();
            var names = read.Where(r => r != null).SelectMany(r => r!.Keys).Distinct().Order(StringComparer.Ordinal).ToList();
            var differs = sides.Skip(1).Select((s, i) => names.Count(n => Text(read[i], n) != Text(read[i + 1], n))).ToList();
            L($"| [{GamePaths.ShortName(package)}](#{GamePaths.ShortName(package).ToLowerInvariant()}) | {names.Count} | {string.Join(" | ", differs)} |");

            sections.AppendLine($"## {GamePaths.ShortName(package)}").AppendLine();
            for (int i = 0; i < sides.Count; i++) if (read[i] == null) sections.AppendLine($"*Not in {sides[i].Name}.*").AppendLine();
            foreach (var name in names)
            {
                var anims = read.Select(r => r?.GetValueOrDefault(name)).ToList();
                var playback = string.Join("; ", sides.Select((s, i) => anims[i] is { } a ? $"{s.Name} {AnimationText.S(a.Seconds(a.Start))}–{AnimationText.S(a.Seconds(a.End))}" : $"{s.Name} —"));
                sections.AppendLine($"### {GamePaths.ShortName(package)} · {name}").AppendLine().AppendLine($"Playback: {playback}.").AppendLine();
                var cells = anims.Select(a => a == null ? null : Cells(a)).ToList();
                var rows = cells.Where(c => c != null).SelectMany(c => c!.Keys).Distinct().ToList();
                if (rows.Count == 0) { sections.AppendLine("No track changes anything (neutral values only).").AppendLine(); continue; }
                sections.AppendLine("| widget | channel | " + string.Join(" | ", sides.Select(s => s.Name)) + " |");
                sections.AppendLine("|---|---|" + string.Concat(sides.Select(_ => "---|")));
                foreach (var row in rows.OrderBy(r => r.Widget, StringComparer.Ordinal).ThenBy(r => r.Channel, StringComparer.Ordinal))
                {
                    var texts = cells.Select(c => c == null ? null : c.GetValueOrDefault(row)).ToList();
                    var shown = texts.Select((t, i) =>
                    {
                        if (t == null) return cells[i] == null ? "(no animation)" : "—";
                        for (int j = 0; j < i; j++) if (texts[j] == t) return $"= {sides[j].Name}";
                        return t;
                    });
                    sections.AppendLine($"| {row.Widget} | {row.Channel} | {string.Join(" | ", shown.Select(Escape))} |");
                }
                sections.AppendLine();
            }
        }
        L();
        sb.Append(sections);
        return sb.ToString();
    }

    private static Dictionary<Row, string> Cells(AnimationInfo a)
    {
        var cells = new Dictionary<Row, string>();
        foreach (var t in a.Tracks)
            foreach (var d in AnimationText.Describe(t))
            {
                var row = new Row(AnimationText.Target(t), d.Name, t.Track);
                cells[row] = cells.TryGetValue(row, out var prev) ? prev + " / " + d.Text : d.Text;
            }
        return cells;
    }

    /// <summary>A whole animation as one comparable text (for the summary counts).</summary>
    private static string? Text(Dictionary<string, AnimationInfo>? read, string name) =>
        read?.GetValueOrDefault(name) is { } a
            ? $"{a.Start}..{a.End}|" + string.Join("|", Cells(a).OrderBy(kv => kv.Key.ToString(), StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))
            : null;

    private static string Escape(string s) => s.Replace("|", "\\|");
}
